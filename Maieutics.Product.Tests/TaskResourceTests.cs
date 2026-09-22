using System.Text.Json;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

public sealed class TaskResourceTests
{
    [Fact]
    public void TaskSchemeIsReservedForTheBuiltInPlane()
    {
        var registry = new ResourceRegistry([new ClaimingProvider(new ResourceClaim("task"))]);

        registry.Resolve(new Uri("task://terminal/a/b")).Should().BeNull();
        registry.GetConflicts().Should().ContainSingle()
            .Which.Reason.Should().Be("reserved_scheme");
    }

    [Fact]
    public async Task TaskProviderClaimsOneAuthorityPerSource()
    {
        var registry = new ResourceRegistry([
            new WorkspaceResourceProvider(Workspace.Create(Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory())),
            new TaskResourceProvider([new TerminalTaskResourceSource(CreateBareRegistry())])
        ]);

        var provider = registry.Providers.Single(provider => provider.Id == "task");
        provider.Class.Should().Be(ResourceProviderClass.BuiltIn);
        provider.Claims.Should().ContainSingle().Which.Should().Be(new ResourceClaim("task", "terminal"));
        registry.Resolve(new Uri("task://terminal/a/b")).Should().Be(provider);
        registry.Resolve(new Uri("task://other/a/b")).Should().BeNull();
    }

    [Fact]
    public async Task UnknownAuthoritiesAndMalformedPathsAreNotFound()
    {
        await using var harness = new TaskHarness();
        var provider = harness.Resources.Providers.Single(provider => provider.Id == "task");
        var request = new ResourceReadRequest(16 * 1024);
        var token = TestContext.Current.CancellationToken;

        var unknownAuthority = () => provider.ReadAsync("task://unknown/a/b", request, token).AsTask();
        (await unknownAuthority.Should().ThrowAsync<ResourceException>())
            .Which.Code.Should().Be("resource_not_found");

        var malformedPath = () => provider.ReadAsync(
                $"task://terminal/{AgentSessionId.Create().Value.ToString("N")}/onlyonesegment",
                request,
                token)
            .AsTask();
        (await malformedPath.Should().ThrowAsync<ResourceException>())
            .Which.Code.Should().Be("resource_not_found");

        var unknownSession = () => provider.ReadAsync(
                $"task://terminal/{AgentSessionId.Create().Value.ToString("N")}/{Guid.NewGuid().ToString("N")}",
                request,
                token)
            .AsTask();
        (await unknownSession.Should().ThrowAsync<ResourceException>())
            .Which.Code.Should().Be("resource_not_found");
    }

    [Fact(Timeout = 15_000)]
    public async Task TimedOutOneShotExposesASnapshotCatalogEntryAndLifecycle()
    {
        await using var harness = new TaskHarness();
        var owner = AgentSessionId.Create();
        var runTask = harness.Registry.RunOnceAsync(
            owner,
            "sh",
            ["-c", "sleep 60"],
            TimeSpan.FromMilliseconds(200),
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);
        var result = await runTask;

        result.Settled.Should().BeFalse();
        result.TaskUri.Should().Be($"task://terminal/{owner.Value.ToString("N")}/{result.SessionId}");

        var snapshot = await ReadSnapshotAsync(harness, result.TaskUri!);
        snapshot.GetProperty("uri").GetString().Should().Be(result.TaskUri);
        snapshot.GetProperty("kind").GetString().Should().Be("terminal");
        snapshot.GetProperty("status").GetString().Should().Be("working");
        snapshot.GetProperty("terminal").GetProperty("agentSessionId").GetString()
            .Should().Be(owner.Value.ToString("N"));
        snapshot.GetProperty("terminal").GetProperty("sessionId").GetString().Should().Be(result.SessionId);
        snapshot.GetProperty("terminal").TryGetProperty("exitCode", out _).Should().BeFalse();

        var catalog = await ListResourcesAsync(harness);
        catalog.Resources.Should().ContainSingle().Which.Uri.Should().Be(result.TaskUri);
        catalog.Resources.Single().Kind.Should().Be("task");
        catalog.Conflicts.Should().BeEmpty();

        // The child settles after the deadline: the next read reports completion.
        harness.Process.EndOfOutput();
        harness.Process.RaiseExited(0);
        var settled = await ReadSnapshotAsync(harness, result.TaskUri!);
        settled.GetProperty("status").GetString().Should().Be("complete");
        settled.GetProperty("terminal").GetProperty("exitCode").GetInt32().Should().Be(0);

        // Closing the session removes the task resource.
        await harness.Registry.CloseAsync(owner, result.SessionId, TestContext.Current.CancellationToken);
        var failure = await InvokeReadTextAsync(harness.WorkspaceFunctions, result.TaskUri!);
        failure.IsFailure.Should().BeTrue();
        failure.Code.Should().Be("resource_not_found");
    }

    [Fact(Timeout = 10_000)]
    public async Task SettledOneShotDoesNotCarryATaskUri()
    {
        await using var harness = new TaskHarness();
        var owner = AgentSessionId.Create();
        var runTask = harness.Registry.RunOnceAsync(
            owner,
            "sh",
            ["-c", "echo done"],
            TimeSpan.FromSeconds(5),
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);
        harness.Process.EndOfOutput();
        harness.Process.RaiseExited(0);
        var result = await runTask;

        result.Settled.Should().BeTrue();
        result.TaskUri.Should().BeNull();
    }

    private static async Task<JsonElement> ReadSnapshotAsync(TaskHarness harness, string taskUri)
    {
        var invocation = await InvokeReadTextAsync(harness.WorkspaceFunctions, taskUri);
        invocation.IsFailure.Should().BeFalse();
        var read = invocation.Result!.Value.Deserialize(WorkspaceJsonSerializerContext.Default.ReadTextResult)!;
        using var document = JsonDocument.Parse(read.Text);
        return document.RootElement.Clone();
    }

    private static async Task<ResourceCatalogResult> ListResourcesAsync(TaskHarness harness)
    {
        var listing = await harness.ResourceFunctions.Functions.Single()
            .InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);
        return ((JsonElement)listing!).Deserialize(ResourceJsonSerializerContext.Default.ResourceCatalogResult)!;
    }

    private static async Task<(bool IsFailure, string? Code, JsonElement? Result)> InvokeReadTextAsync(
        WorkspaceFunctions functions,
        string uri)
    {
        var readText = functions.Functions.Single(function => function.Name == "read_text");
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["uri"] = uri });
        try
        {
            var result = await readText.InvokeAsync(arguments, TestContext.Current.CancellationToken);
            return (false, null, result as JsonElement?);
        }
        catch (AgentToolException exception)
        {
            return (true, exception.Code, null);
        }
    }

    private static TerminalRegistry CreateBareRegistry()
    {
        return new TerminalRegistry(
            Workspace.Create(Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory()),
            new TerminalOptions { SettleTimeout = TimeSpan.FromMilliseconds(30) },
            new FakeTerminalProcessFactory(new FakeTerminalProcess()),
            NullLogger<TerminalSession>.Instance,
            TestPermissionPolicies.Unconfigured());
    }

    private sealed class TaskHarness : IAsyncDisposable
    {
        internal TaskHarness()
        {
            WorkspaceRoot = TemporaryWorkspace.Create();
            Process = new FakeTerminalProcess();
            var workspace = Workspace.Create(WorkspaceRoot.Path, WorkspaceRoot.Path);
            Registry = new TerminalRegistry(
                workspace,
                new TerminalOptions { SettleTimeout = TimeSpan.FromMilliseconds(30) },
                new FakeTerminalProcessFactory(Process),
                NullLogger<TerminalSession>.Instance,
                TestPermissionPolicies.Unconfigured());
            Resources = new ResourceRegistry(
            [
                new WorkspaceResourceProvider(workspace),
                new TaskResourceProvider([new TerminalTaskResourceSource(Registry)])
            ]);
            WorkspaceFunctions = new WorkspaceFunctions(workspace, resources: Resources);
            ResourceFunctions = new ResourceFunctions(Resources);
        }

        internal TemporaryWorkspace WorkspaceRoot { get; }

        internal FakeTerminalProcess Process { get; }

        internal TerminalRegistry Registry { get; }

        internal ResourceRegistry Resources { get; }

        internal WorkspaceFunctions WorkspaceFunctions { get; }

        internal ResourceFunctions ResourceFunctions { get; }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            WorkspaceRoot.Dispose();
        }
    }

    private sealed class ClaimingProvider(ResourceClaim claim) : IResourceProvider
    {
        public string Id => "claiming";

        public ResourceProviderClass Class => ResourceProviderClass.Custom;

        public IReadOnlyList<ResourceClaim> Claims => [claim];

        public ValueTask<ResourceReadResult> ReadAsync(
            string uri,
            ResourceReadRequest request,
            CancellationToken cancellationToken)
        {
            throw new ResourceException("resource_provider_failed", "The claiming provider never reads.");
        }
    }
}
