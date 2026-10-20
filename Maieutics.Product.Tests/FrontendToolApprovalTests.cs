using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Frontend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>
///     The frontend half of the tool-approval gate: the request reaches the session's stream
///     as an <c>input.request</c> frame, the shared input endpoint completes it, and every
///     non-answer path denies rather than hanging or executing.
/// </summary>
public sealed class FrontendToolApprovalTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AnApprovalRequestPublishesAnInputRequestFrame()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);
        var sessionId = AgentSessionId.Create();

        var approval = presenter.RequestApprovalAsync(Request(sessionId, "remove_file"), deadline.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);

        var frame = publisher.Single("input.request");
        Assert.Equal(sessionId, frame.SessionId);
        var request = JsonSerializer.Deserialize(
            frame.Data.GetRawText(),
            FrontendJsonContext.Default.FrontendInputRequest)!;
        Assert.StartsWith(FrontendToolApprovalPresenter.RequestIdPrefix, request.RequestId);
        Assert.Equal(FrontendInputKind.Approval, request.Kind);
        Assert.Equal("remove_file", request.Tool);
        Assert.Contains("destroy", request.Prompt);
        Assert.False(request.Password);

        // Complete it so the pending request does not outlive the test.
        Assert.True(presenter.TryCompleteInput(request.RequestId, "accept", string.Empty));
        var decision = await approval;
        Assert.True(decision.Approved);
        Assert.False(decision.AlwaysAllow);
    }

    [Fact]
    public async Task AlwaysAllowIsCarriedThroughTheDecision()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);

        var approval = presenter.RequestApprovalAsync(Request(AgentSessionId.Create(), "remove_file"), deadline.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);
        var requestId = publisher.Single("input.request").Data
            .Deserialize(FrontendJsonContext.Default.FrontendInputRequest)!.RequestId;

        Assert.True(presenter.TryCompleteInput(requestId, "always", string.Empty));
        var decision = await approval;
        Assert.True(decision.Approved);
        Assert.True(decision.AlwaysAllow);
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("cancel")]
    [InlineData("deny")]
    [InlineData(null)]
    public async Task ANonAcceptActionDenies(string? action)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);

        var approval = presenter.RequestApprovalAsync(Request(AgentSessionId.Create(), "remove_file"), deadline.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);
        var requestId = publisher.Single("input.request").Data
            .Deserialize(FrontendJsonContext.Default.FrontendInputRequest)!.RequestId;

        Assert.True(presenter.TryCompleteInput(requestId, action, "because"));
        var decision = await approval;
        Assert.False(decision.Approved);
        Assert.False(decision.AlwaysAllow);
    }

    [Fact]
    public async Task ADeclinedAnswerCarriesTheUserReason()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);

        var approval = presenter.RequestApprovalAsync(Request(AgentSessionId.Create(), "remove_file"), deadline.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);
        var requestId = publisher.Single("input.request").Data
            .Deserialize(FrontendJsonContext.Default.FrontendInputRequest)!.RequestId;

        presenter.TryCompleteInput(requestId, "decline", "That file is load-bearing.");
        var decision = await approval;
        Assert.False(decision.Approved);
        Assert.Equal("That file is load-bearing.", decision.Reason);
    }

    [Fact]
    public async Task NoLiveStreamDeniesImmediately()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var presenter = CreatePresenter(new RecordingFramePublisher(publishable: false));

        // Nothing to present on: the gate must not wait and must not approve.
        var decision = await presenter.RequestApprovalAsync(
            Request(AgentSessionId.Create(), "remove_file"),
            deadline.Token);

        Assert.False(decision.Approved);
        Assert.Contains("No frontend", decision.Reason);
    }

    [Fact]
    public async Task AWaitedApprovalDeniesOnTimeout()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        // A tiny budget keeps the timeout path fast; the production default is 120 s.
        var presenter = new FrontendToolApprovalPresenter(
            publisher,
            NullLogger<FrontendToolApprovalPresenter>.Instance,
            TimeSpan.FromMilliseconds(50));

        var decision = await presenter.RequestApprovalAsync(
            Request(AgentSessionId.Create(), "remove_file"),
            deadline.Token);

        Assert.False(decision.Approved);
        Assert.Contains("timed out", decision.Reason);
    }

    [Fact]
    public async Task AWaitedApprovalDeniesWhenTheRunEnds()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);

        using var run = new CancellationTokenSource();
        var approval = presenter.RequestApprovalAsync(
            Request(AgentSessionId.Create(), "remove_file"),
            run.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);

        await run.CancelAsync();
        var decision = await approval;
        Assert.False(decision.Approved);
        Assert.Contains("run ended", decision.Reason);
    }

    [Fact]
    public async Task AnsweringTwiceIsRejected()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);

        var approval = presenter.RequestApprovalAsync(Request(AgentSessionId.Create(), "remove_file"), deadline.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);
        var requestId = publisher.Single("input.request").Data
            .Deserialize(FrontendJsonContext.Default.FrontendInputRequest)!.RequestId;

        Assert.True(presenter.TryCompleteInput(requestId, "accept", string.Empty));
        Assert.False(presenter.TryCompleteInput(requestId, "decline", string.Empty));
        Assert.True((await approval).Approved);
    }

    [Fact]
    public void TheRequestIdPrefixClaimsOnlyItsOwnIds()
    {
        Assert.True(FrontendToolApprovalPresenter.OwnsRequestId("approve-abc"));
        Assert.False(FrontendToolApprovalPresenter.OwnsRequestId("elicit-abc"));
        Assert.False(FrontendToolApprovalPresenter.OwnsRequestId("input-abc"));
        Assert.False(FrontendToolApprovalPresenter.OwnsRequestId(""));
    }

    [Fact]
    public async Task AChildRunsRequestIsPresentedOnItsParentsStream()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);
        var parent = AgentSessionId.Create();
        var child = AgentSessionId.Create();

        // A child session never announces a frontend stream of its own (only a
        // frontend-submitted turn does), so presenting on the child id would find nothing and
        // deny with a false "no frontend connected" reason.
        presenter.RegisterChildScope(child, parent);

        var approval = presenter.RequestApprovalAsync(Request(child, "remove_file"), deadline.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);

        var frame = publisher.Single("input.request");
        Assert.Equal(parent, frame.SessionId);

        var requestId = frame.Data
            .Deserialize(FrontendJsonContext.Default.FrontendInputRequest)!.RequestId;
        Assert.True(presenter.TryCompleteInput(requestId, "accept", string.Empty));
        Assert.True((await approval).Approved);
    }

    [Fact]
    public async Task AReleasedChildScopeFallsBackToItsOwnSession()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        // A publishing fake that reports "no live stream": the child has no stream of its own
        // and its parent link was released, so the request must deny rather than hang.
        var presenter = CreatePresenter(new RecordingFramePublisher(publishable: false));
        var parent = AgentSessionId.Create();
        var child = AgentSessionId.Create();
        presenter.RegisterChildScope(child, parent);
        presenter.ReleaseChildScope(child);

        var decision = await presenter.RequestApprovalAsync(Request(child, "remove_file"), deadline.Token);
        Assert.False(decision.Approved);
        Assert.Contains("No frontend", decision.Reason);
    }

    [Fact]
    public async Task AnAnswerThatRacedTheTimeoutIsNotReportedAsAccepted()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        // A budget short enough that the wait's own expiry wins the race deterministically:
        // the answer below arrives after it.
        var presenter = new FrontendToolApprovalPresenter(
            publisher,
            NullLogger<FrontendToolApprovalPresenter>.Instance,
            TimeSpan.FromMilliseconds(50));

        var decision = await presenter.RequestApprovalAsync(
            Request(AgentSessionId.Create(), "remove_file"),
            deadline.Token);
        Assert.False(decision.Approved);

        var requestId = publisher.Single("input.request").Data
            .Deserialize(FrontendJsonContext.Default.FrontendInputRequest)!.RequestId;

        // The wait already claimed the request, so the late answer is a 404 rather than a 200
        // for a decision nobody consumed. Reporting success would tell the user their approval
        // counted while the model saw a timeout denial.
        Assert.False(presenter.TryCompleteInput(requestId, "accept", string.Empty));
    }

    [Fact]
    public async Task AnAnswerThatWonTheRaceIsTheDecisionTheModelSees()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);

        var approval = presenter.RequestApprovalAsync(Request(AgentSessionId.Create(), "remove_file"), deadline.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);
        var requestId = publisher.Single("input.request").Data
            .Deserialize(FrontendJsonContext.Default.FrontendInputRequest)!.RequestId;

        // The answer claims the entry first; the wait then returns that decision rather than
        // its own timeout.
        Assert.True(presenter.TryCompleteInput(requestId, "accept", string.Empty));
        var decision = await approval;
        Assert.True(decision.Approved);
        Assert.Equal("Approved by the user.", decision.Reason);
    }

    [Fact]
    public async Task TheDeferredGateResolvesTheRealPresenter()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var publisher = new RecordingFramePublisher();
        var presenter = CreatePresenter(publisher);
        var services = new ServiceCollection();
        services.AddSingleton(presenter);
        services.AddSingleton<IAgentToolApprovalGate>(new DeferredToolApprovalGate(services.BuildServiceProvider()));

        var gate = services.BuildServiceProvider().GetRequiredService<IAgentToolApprovalGate>();
        var approval = gate.RequestApprovalAsync(Request(AgentSessionId.Create(), "remove_file"), deadline.Token);
        await publisher.WaitForCountAsync(1, deadline.Token);
        var requestId = publisher.Single("input.request").Data
            .Deserialize(FrontendJsonContext.Default.FrontendInputRequest)!.RequestId;

        presenter.TryCompleteInput(requestId, "accept", string.Empty);
        Assert.True((await approval).Approved);
    }

    private static FrontendToolApprovalPresenter CreatePresenter(RecordingFramePublisher publisher) =>
        new(publisher, NullLogger<FrontendToolApprovalPresenter>.Instance, TimeSpan.FromSeconds(30));

    private static AgentToolApprovalRequest Request(AgentSessionId sessionId, string toolName)
    {
        using var document = JsonDocument.Parse("""{"path":"/tmp/example.txt"}""");
        return new AgentToolApprovalRequest(
            sessionId,
            AgentRunId.Create(),
            AgentToolCallId.Create(),
            toolName,
            document.RootElement.Clone(),
            new AgentToolClassification(ReadOnly: false, Destructive: true),
            $"The tool '{toolName}' declares that it may destroy data or resources.");
    }
}
