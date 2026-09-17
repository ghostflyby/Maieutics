using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace Maieutics.Product.Tests;

public sealed class McpElicitationTests
{
    private static AgentToolCallId RegisterCall(McpElicitationCoordinator coordinator, out AgentSessionId sessionId)
    {
        sessionId = AgentSessionId.Create();
        var callId = AgentToolCallId.Create();
        coordinator.RegisterCall(callId, sessionId);
        return callId;
    }

    private static ElicitRequestParams BuildRequest(string message = "Provide the token")
    {
        return new ElicitRequestParams
        {
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties =
                {
                    ["token"] = new ElicitRequestParams.StringSchema { Description = "The token" }
                }
            }
        };
    }

    [Fact]
    public async Task AttributedElicitationIsPresentedAndMappedToAccept()
    {
        var coordinator = new McpElicitationCoordinator(
            new StubPresenter(_ => ValueTask.FromResult(new McpElicitationAnswer("accept", """{"token":"s3cret"}"""))),
            NullLogger.Instance);
        var callId = RegisterCall(coordinator, out _);

        var result = await coordinator.HandleElicitationAsync("srv", BuildRequest(), TestContext.Current.CancellationToken);

        result.Action.Should().Be("accept");
        result.Content.Should().ContainKey("token");
        result.Content!["token"].GetString().Should().Be("s3cret");
        coordinator.UnregisterCall(callId);
    }

    [Fact]
    public async Task UnattributedElicitationIsCancelledWithoutPresenting()
    {
        var presented = false;
        var coordinator = new McpElicitationCoordinator(
            new StubPresenter(_ =>
            {
                presented = true;
                return ValueTask.FromResult(new McpElicitationAnswer("accept", null));
            }),
            NullLogger.Instance);

        var result = await coordinator.HandleElicitationAsync("srv", BuildRequest(), TestContext.Current.CancellationToken);

        result.Action.Should().Be("cancel");
        presented.Should().BeFalse();
    }

    [Fact]
    public async Task AmbiguousAttributionIsCancelledWithoutPresenting()
    {
        var coordinator = new McpElicitationCoordinator(
            new StubPresenter(_ => ValueTask.FromResult(new McpElicitationAnswer("accept", null))),
            NullLogger.Instance);
        var first = RegisterCall(coordinator, out _);
        var second = RegisterCall(coordinator, out _);

        var result = await coordinator.HandleElicitationAsync("srv", BuildRequest(), TestContext.Current.CancellationToken);

        result.Action.Should().Be("cancel");
        coordinator.UnregisterCall(first);
        coordinator.UnregisterCall(second);
    }

    [Fact]
    public async Task ConcurrentElicitationsAnswerCancel()
    {
        // The first presenter call holds until we release it, keeping the coordinator's
        // in-flight gate closed while the second request is refused.
        var hold = new TaskCompletionSource<McpElicitationAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new McpElicitationCoordinator(
            new StubPresenter(_ => Interlocked.Increment(ref calls) == 1
                ? new ValueTask<McpElicitationAnswer>(hold.Task)
                : ValueTask.FromResult(new McpElicitationAnswer("accept", null))),
            NullLogger.Instance);
        RegisterCall(coordinator, out _);

        var first = coordinator.HandleElicitationAsync("srv", BuildRequest(), TestContext.Current.CancellationToken);
        var second = await coordinator.HandleElicitationAsync("srv", BuildRequest(), TestContext.Current.CancellationToken);

        second.Action.Should().Be("cancel");
        hold.TrySetResult(new McpElicitationAnswer("accept", null));
        (await first).Action.Should().Be("accept");
        // The second request was refused at the in-flight gate, before any presenter call.
        Volatile.Read(ref calls).Should().Be(1);
    }

    [Fact]
    public async Task UnansweredElicitationTimesOutToCancel()
    {
        var coordinator = new McpElicitationCoordinator(
            new StubPresenter(_ => new ValueTask<McpElicitationAnswer>(new TaskCompletionSource<McpElicitationAnswer>().Task)),
            NullLogger.Instance,
            waitTimeout: TimeSpan.FromMilliseconds(150));
        RegisterCall(coordinator, out _);

        var result = await coordinator.HandleElicitationAsync("srv", BuildRequest(), TestContext.Current.CancellationToken);

        result.Action.Should().Be("cancel");
    }

    [Fact]
    public async Task DeclinedAndCancelledAnswersMapThrough()
    {
        var coordinator = new McpElicitationCoordinator(
            new StubPresenter(_ => ValueTask.FromResult(new McpElicitationAnswer("decline", null))),
            NullLogger.Instance);
        RegisterCall(coordinator, out _);

        (await coordinator.HandleElicitationAsync("srv", BuildRequest(), TestContext.Current.CancellationToken))
            .Action.Should().Be("decline");
    }

    [Fact]
    public async Task OversizedMessageIsCancelledWithoutPresenting()
    {
        var presented = false;
        var coordinator = new McpElicitationCoordinator(
            new StubPresenter(_ =>
            {
                presented = true;
                return ValueTask.FromResult(new McpElicitationAnswer("accept", null));
            }),
            NullLogger.Instance);
        RegisterCall(coordinator, out _);

        var result = await coordinator.HandleElicitationAsync(
            "srv",
            BuildRequest(new string('x', 5 * 1024)),
            TestContext.Current.CancellationToken);

        result.Action.Should().Be("cancel");
        presented.Should().BeFalse();
    }

    private sealed class StubPresenter(Func<McpElicitationRequest, ValueTask<McpElicitationAnswer>> answer)
        : IMcpElicitationPresenter
    {
        public ValueTask<McpElicitationAnswer> PresentAsync(
            McpElicitationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return answer(request);
        }
    }
}
