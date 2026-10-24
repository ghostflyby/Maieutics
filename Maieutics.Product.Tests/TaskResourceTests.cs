using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

public sealed class TaskResourceTests
{
    [Fact]
    public void TaskSchemeIsReservedForTheBuiltInPlane()
    {
        var registry = new ResourceRegistry([new ClaimingProvider(new ResourceClaim("task"))]);

        Assert.Null(registry.Resolve(new Uri("task://terminal/a/b")));
        Assert.Equal("reserved_scheme", Assert.Single(registry.GetConflicts()).Reason);
    }

    [Fact(Timeout = 30_000)]
    public void TaskProviderClaimsOneAuthorityPerSource()
    {
        _ = TestContext.Current.CancellationToken;
        var registry = new ResourceRegistry([
            new WorkspaceResourceProvider(Workspace.Create(Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory())),
            new TaskResourceProvider([new TerminalTaskResourceSource(CreateBareRegistry())])
        ]);

        var provider = registry.Providers.Single(provider => provider.Id == "task");
        Assert.Equal(ResourceProviderClass.BuiltIn, provider.Class);
        Assert.Equal(new ResourceClaim("task", "terminal"), Assert.Single(provider.Claims));
        Assert.Equal(provider, registry.Resolve(new Uri("task://terminal/a/b")));
        Assert.Null(registry.Resolve(new Uri("task://other/a/b")));
    }

    [Fact(Timeout = 30_000)]
    public async Task UnknownAuthoritiesAndMalformedPathsAreNotFound()
    {
        await using var harness = new TaskHarness();
        var provider = harness.Resources.Providers.Single(provider => provider.Id == "task");
        var request = new ResourceReadRequest(16 * 1024);
        var token = TestContext.Current.CancellationToken;

        var unknownAuthority = () => provider.ReadAsync("task://unknown/a/b", request, token).AsTask();
        Assert.Equal("resource_not_found", (await Assert.ThrowsAnyAsync<ResourceException>(async () => { await unknownAuthority.Invoke(); })).Code);

        var malformedPath = () => provider.ReadAsync(
                $"task://terminal/{AgentSessionId.Create().Value:N}/onlyonesegment",
                request,
                token)
            .AsTask();
        Assert.Equal("resource_not_found", (await Assert.ThrowsAnyAsync<ResourceException>(async () => { await malformedPath.Invoke(); })).Code);

        var unknownSession = () => provider.ReadAsync(
                $"task://terminal/{AgentSessionId.Create().Value:N}/{Guid.NewGuid():N}",
                request,
                token)
            .AsTask();
        Assert.Equal("resource_not_found", (await Assert.ThrowsAnyAsync<ResourceException>(async () => { await unknownSession.Invoke(); })).Code);
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

        Assert.False(result.Settled);
        Assert.Equal($"task://terminal/{owner.Value:N}/{result.SessionId}", result.TaskUri);

        var snapshot = await ReadSnapshotAsync(harness, result.TaskUri!);
        Assert.Equal(result.TaskUri, snapshot.GetProperty("uri").GetString());
        Assert.Equal("terminal", snapshot.GetProperty("kind").GetString());
        Assert.Equal("working", snapshot.GetProperty("status").GetString());
        Assert.Equal(owner.Value.ToString("N"), snapshot.GetProperty("terminal").GetProperty("agentSessionId").GetString());
        Assert.Equal(result.SessionId, snapshot.GetProperty("terminal").GetProperty("sessionId").GetString());
        Assert.False(snapshot.GetProperty("terminal").TryGetProperty("exitCode", out _));

        var catalog = await ListResourcesAsync(harness);
        Assert.Equal(result.TaskUri, Assert.Single(catalog.Resources).Uri);
        Assert.Equal("task", catalog.Resources.Single().Kind);
        Assert.Empty(catalog.Conflicts);

        // The child settles after the deadline: the next read reports completion.
        harness.Process.EndOfOutput();
        harness.Process.RaiseExited(0);
        var settled = await ReadSnapshotAsync(harness, result.TaskUri!);
        Assert.Equal("complete", settled.GetProperty("status").GetString());
        Assert.Equal(0, settled.GetProperty("terminal").GetProperty("exitCode").GetInt32());

        // Closing the session removes the task resource.
        await harness.Registry.CloseAsync(owner, result.SessionId, TestContext.Current.CancellationToken);
        var failure = await InvokeReadTextAsync(harness.WorkspaceFunctions, result.TaskUri!);
        Assert.True(failure.IsFailure);
        Assert.Equal("resource_not_found", failure.Code);
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

        Assert.True(result.Settled);
        Assert.Null(result.TaskUri);
    }

    [Fact(Timeout = 20_000)]
    public async Task WaitResolvesWithTheTerminalSnapshotWhenTheProcessExits()
    {
        await using var harness = new TaskHarness();
        var owner = AgentSessionId.Create();
        var result = await StartTimedOutOneShotAsync(harness, owner);
        var wait = harness.Provider.WaitTaskAsync(
            result.TaskUri!, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Either ordering is valid: a wait that starts before the exit awaits the signal, and
        // one that starts after it returns immediately.
        harness.Process.EndOfOutput();
        harness.Process.RaiseExited(0);
        var snapshot = await wait;

        Assert.Equal("complete", snapshot.Status);
        Assert.Equal(0, snapshot.Terminal!.ExitCode);
    }

    [Fact(Timeout = 10_000)]
    public async Task WaitTimesOutTypedWhileTheTaskSurvives()
    {
        await using var harness = new TaskHarness();
        var owner = AgentSessionId.Create();
        var result = await StartTimedOutOneShotAsync(harness, owner);

        Assert.Equal("task_wait_timeout", (await Assert.ThrowsAnyAsync<ResourceException>(async () => await harness.Provider.WaitTaskAsync(
            result.TaskUri!, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken))).Code);

        harness.Process.EndOfOutput();
        harness.Process.RaiseExited(0);
        var snapshot = await ReadSnapshotAsync(harness, result.TaskUri!);
        Assert.Equal("complete", snapshot.GetProperty("status").GetString());
    }

    [Fact(Timeout = 10_000)]
    public async Task CancelSettlesTheTaskAndRemovesItFromThePlane()
    {
        await using var harness = new TaskHarness();
        var owner = AgentSessionId.Create();
        var result = await StartTimedOutOneShotAsync(harness, owner);

        var snapshot = await harness.Provider.CancelTaskAsync(
            result.TaskUri!, owner, TestContext.Current.CancellationToken);
        Assert.Equal("cancel", snapshot.Status);
        Assert.Equal("closed", snapshot.Terminal!.State);

        Assert.Equal("resource_not_found", (await Assert.ThrowsAnyAsync<ResourceException>(async () =>
        {
            await harness.Provider.ReadAsync(
                result.TaskUri!, new ResourceReadRequest(16 * 1024), TestContext.Current.CancellationToken);
        })).Code);
    }

    [Fact(Timeout = 10_000)]
    public async Task CancelIsIdempotentOnATerminalTask()
    {
        await using var harness = new TaskHarness();
        var owner = AgentSessionId.Create();
        var result = await StartTimedOutOneShotAsync(harness, owner);
        harness.Process.EndOfOutput();
        harness.Process.RaiseExited(0);

        var snapshot = await harness.Provider.CancelTaskAsync(
            result.TaskUri!, owner, TestContext.Current.CancellationToken);
        Assert.Equal("complete", snapshot.Status);
        Assert.Equal(0, snapshot.Terminal!.ExitCode);
    }

    [Fact(Timeout = 10_000)]
    public async Task CancelRejectsACallerSessionMismatch()
    {
        await using var harness = new TaskHarness();
        var owner = AgentSessionId.Create();
        var result = await StartTimedOutOneShotAsync(harness, owner);

        Assert.Equal("task_forbidden", (await Assert.ThrowsAnyAsync<ResourceException>(async () =>
        {
            await harness.Provider.CancelTaskAsync(
                result.TaskUri!, AgentSessionId.Create(), TestContext.Current.CancellationToken);
        })).Code);

        var stillWorking = await ReadSnapshotAsync(harness, result.TaskUri!);
        Assert.Equal("working", stillWorking.GetProperty("status").GetString());
    }

    [Fact(Timeout = 30_000)]
    public async Task ObjectsPlaneReadsStoredContentAndRejectsUnknownAddresses()
    {
        var store = new InMemoryAgentObjectStore();
        var descriptor = store.Ingest(new MemoryStream("[1,2,3]"u8.ToArray(), writable: false));
        var provider = new AgentObjectResourceProvider(store);
        var uri = $"objects://{descriptor.Sha256}";

        var read = await provider.ReadAsync(
            uri, new ResourceReadRequest(16 * 1024), TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(read.Content, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("[1,2,3]", document.RootElement.GetRawText());
        Assert.Equal("application/json", read.MimeType);

        async Task ThrowMissing() => await provider.ReadAsync(
            $"objects://{new string('a', 64)}",
            new ResourceReadRequest(16 * 1024),
            TestContext.Current.CancellationToken);
        async Task ThrowMalformed() => await provider.ReadAsync(
            "objects://NOTAHASH",
            new ResourceReadRequest(16 * 1024),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ResourceException>(ThrowMissing);
        await Assert.ThrowsAsync<ResourceException>(ThrowMalformed);
    }

    private sealed class InMemoryAgentObjectStore : IAgentObjectStore
    {
        private readonly Dictionary<string, byte[]> _objects = [];

        public AgentObjectDescriptor Ingest(Stream content)
        {
            using var buffered = new MemoryStream();
            content.CopyTo(buffered);
            var bytes = buffered.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
            _objects[sha256] = bytes;
            return new AgentObjectDescriptor(sha256, bytes.Length, "application/json");
        }

        public Stream Open(string sha256)
        {
            if (!Regex.IsMatch(
                    sha256, "^[0-9a-f]{64}$") || !_objects.TryGetValue(sha256, out var bytes))
                throw new FileNotFoundException("No such object.", sha256);
            return new MemoryStream(bytes, writable: false);
        }
    }

    private static async Task<TerminalRunResult> StartTimedOutOneShotAsync(TaskHarness harness, AgentSessionId owner)
    {
        var result = await harness.Registry.RunOnceAsync(
            owner,
            "sh",
            ["-c", "sleep 60"],
            TimeSpan.FromMilliseconds(200),
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);
        Assert.False(result.Settled);
        Assert.NotNull(result.TaskUri);
        return result;
    }

    private static async Task<JsonElement> ReadSnapshotAsync(TaskHarness harness, string taskUri)
    {
        var invocation = await InvokeReadTextAsync(harness.WorkspaceFunctions, taskUri);
        Assert.False(invocation.IsFailure);
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
            Provider = new TaskResourceProvider([new TerminalTaskResourceSource(Registry)]);
            Resources = new ResourceRegistry(
            [
                new WorkspaceResourceProvider(workspace),
                Provider
            ]);
            WorkspaceFunctions = new WorkspaceFunctions(workspace, resources: Resources);
            ResourceFunctions = new ResourceFunctions(Resources);
        }

        internal TemporaryWorkspace WorkspaceRoot { get; }

        internal FakeTerminalProcess Process { get; }

        internal TerminalRegistry Registry { get; }

        internal TaskResourceProvider Provider { get; }

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
