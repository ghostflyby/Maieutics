using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Permissions;
using Maieutics.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

/// <summary>
///     B4 (ADR 0020): the kernel pre-caches the REPL's real <c>EffectivePolicy</c> per session
///     before the host derives the REPL, and the <c>host.repl.spawned</c> report registers that
///     policy instead of the <c>Default</c> placeholder. These tests drive the pre-cache
///     (<see cref="DenoReplPolicyCache"/>, built on the shared <see cref="DenoReplPolicyBuilder"/>)
///     and observe the permission broker's verdict through a real Deno child, exactly like the B2
///     registration tests.
/// </summary>
public sealed class DenoReplPolicyCacheTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact(Timeout = 60_000)]
    public async Task PreparedPolicyGovernsTheHostDerivedReplProbe()
    {
        // The shared builder produces a real baseline policy (working-directory read grant, no
        // HOME/PATH env grants). Pre-caching it, then reporting the host-derived pid, must make
        // the broker decide per that policy — not per Default.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker);
        var root = CreateProbeRoot();
        try
        {
            var policy = DenoReplPolicyBuilder.Build(
                Path.Combine(root, "modules"),
                root,
                Path.Combine(root, "deno.json"),
                Path.Combine(root, "deno.lock"),
                ReplControlHost.CreateControlAddress(),
                Path.Combine(root, "esbuild.wasm"));
            DenoReplPolicyCache.Cache(manager, "prepared-session", policy);

            using var process = StartProbe(broker, root, deadline.Token);
            var outputTask = ReadProbeAsync(process, deadline.Token);
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "prepared-session", 1, process.Id)));

            registry.IsOwnedBy(process.Id, "prepared-session").Should().BeTrue();
            var output = await outputTask;
            output.Should().Contain("read OK");
            output.Should().Contain("HOME denied");
            output.Should().Contain("PATH denied");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task PrepareAsyncPreCachesTheBuilderPolicyForTheSession()
    {
        // The session-start pre-cache path: real deno eval resolves esbuild-wasm against the
        // materialized module graph and the baseline overlay produces the policy. The report must
        // then register that policy. Environment-dependent esbuild-wasm resolution is handled
        // honestly: a resolved payload is asserted through the probe's grants, an unresolved one
        // (no network/cache) degrades explicitly — either outcome is observed via the broker.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker);
        var root = CreateProbeRoot();
        var modules = new DenoReplModule();
        try
        {
            var policy = await DenoReplPolicyCache.PrepareAsync(
                manager,
                "deno",
                modules.ModuleDirectory,
                root,
                modules.ConfigFile,
                modules.LockFile,
                ReplControlHost.CreateControlAddress(),
                null,
                "prepared-session",
                NullLogger.Instance,
                deadline.Token);

            using var process = StartProbe(broker, root, deadline.Token);
            var outputTask = ReadProbeAsync(process, deadline.Token);
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "prepared-session", 1, process.Id)));

            var output = await outputTask;
            if (policy is not null)
            {
                output.Should().Contain("read OK");
                output.Should().Contain("HOME denied");
            }
            else
            {
                output.Should().Contain("read denied");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task EsbuildResolutionFailureLeavesNoPreCacheAndTheReportDegradesExplicitly()
    {
        // A config file deno cannot load fails the esbuild eval fast, so the pre-cache stays empty.
        // The report still binds the pid, but registers Default with an explicit warning — the
        // probe observes deny-by-default, never a stale or fabricated policy.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker);
        var root = CreateProbeRoot();
        var modules = new DenoReplModule();
        try
        {
            var policy = await DenoReplPolicyCache.PrepareAsync(
                manager,
                "deno",
                modules.ModuleDirectory,
                root,
                Path.Combine(root, "missing-denon.json"),
                modules.LockFile,
                ReplControlHost.CreateControlAddress(),
                null,
                "failed-session",
                NullLogger.Instance,
                deadline.Token);
            policy.Should().BeNull();

            using var process = StartProbe(broker, root, deadline.Token);
            var outputTask = ReadProbeAsync(process, deadline.Token);
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "failed-session", 1, process.Id)));

            var output = await outputTask;
            output.Should().Contain("read denied");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ClearingTheSessionPolicyAfterCloseDegradesTheNextReport()
    {
        // Session lifecycle: a closed session's policy is cleared; a later report for the same
        // session id must register Default explicitly — never the stale pre-cached policy.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker);
        var root = CreateProbeRoot();
        try
        {
            DenoReplPolicyCache.Cache(manager, "closed-session", CreateBaselinePolicy(root));
            DenoReplPolicyCache.Clear(manager, "closed-session");

            using var process = StartProbe(broker, root, deadline.Token);
            var outputTask = ReadProbeAsync(process, deadline.Token);
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "closed-session", 1, process.Id)));

            var output = await outputTask;
            output.Should().Contain("read denied");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RestartReCachesTheNewGenerationPolicyBeforeTheNextReport()
    {
        // Generation restart: the new start re-caches the policy for the new working directory,
        // replacing the previous generation's. A report after the restart must observe the new
        // policy (read under the new working directory), not the old one.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);
        var registry = new ReplControlSessionRegistry();
        await using var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var manager = CreateManager(registry, broker);
        var root = CreateProbeRoot();
        var previousRoot = Path.Combine(root, "previous-workspace");
        var currentRoot = Path.Combine(root, "current-workspace");
        Directory.CreateDirectory(previousRoot);
        Directory.CreateDirectory(currentRoot);
        try
        {
            DenoReplPolicyCache.Cache(manager, "restarted-session", CreateBaselinePolicy(previousRoot));

            // Restart: the old policy is cleared and the new generation pre-caches for the current
            // working directory (the session-start path on restart).
            DenoReplPolicyCache.Clear(manager, "restarted-session");
            DenoReplPolicyCache.Cache(manager, "restarted-session", CreateBaselinePolicy(currentRoot));

            using var process = StartProbe(broker, currentRoot, deadline.Token);
            var outputTask = ReadProbeAsync(process, deadline.Token);
            manager.HandleHostMessage(Envelope(ReplMessageType.HostReplSpawned, new HostReplSpawnedPayload(
                "restarted-session", 2, process.Id)));

            var output = await outputTask;
            output.Should().Contain("read OK");
            output.Should().Contain("HOME denied");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static PluginHostManager CreateManager(
        ReplControlSessionRegistry registry,
        DenoPermissionBroker? broker)
    {
        return new PluginHostManager(
            Path.Combine(Path.GetTempPath(), $"mc-repl-cache-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions(),
            new PluginHostModule(),
            registry,
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            broker);
    }

    /// <summary>Builds the same baseline policy the kernel derives for a REPL child, from an
    /// already-resolved esbuild-wasm path (the synchronous builder entry; no deno eval).</summary>
    private static EffectivePolicy CreateBaselinePolicy(string workingDirectory)
    {
        return DenoReplPolicyBuilder.Build(
            Path.Combine(workingDirectory, "modules"),
            workingDirectory,
            Path.Combine(workingDirectory, "deno.json"),
            Path.Combine(workingDirectory, "deno.lock"),
            ReplControlHost.CreateControlAddress(),
            Path.Combine(workingDirectory, "esbuild.wasm"));
    }

    private static string CreateProbeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-repl-cache-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Envelope<T>(string type, T payload)
    {
        return JsonSerializer.Serialize(
            new { version = 1, type, payload },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    /// <summary>Starts a real Deno child that connects to the broker and probes read + env
    /// permissions. The child blocks on its first broker request until the policy for its pid is
    /// registered, so a report can register it after spawn (the broker's slot waits, ADR 0018).</summary>
    private static Process StartProbe(
        DenoPermissionBroker broker,
        string root,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(root, $"probe-{Guid.NewGuid():N}.ts");
        var targetPath = Path.Combine(root, "target.txt");
        File.WriteAllText(targetPath, "payload");
        var escapedTarget = targetPath.Replace("\\", "\\\\");
        File.WriteAllText(
            scriptPath,
            "try { await Deno.readTextFile(\"" + escapedTarget + "\"); console.log(\"read OK\"); }\n" +
            "catch (e) { console.log(\"read denied:\", String(e).split(\"\\n\")[0]); }\n" +
            "try { console.log(\"HOME visible:\", Deno.env.get(\"HOME\")); }\n" +
            "catch (e) { console.log(\"HOME denied:\", String(e).split(\"\\n\")[0]); }\n" +
            "try { console.log(\"PATH visible:\", Deno.env.get(\"PATH\")); }\n" +
            "catch (e) { console.log(\"PATH denied:\", String(e).split(\"\\n\")[0]); }\n");
        var startInfo = new ProcessStartInfo
        {
            FileName = "deno",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-prompt");
        startInfo.Environment["DENO_PERMISSION_BROKER_PATH"] = broker.Address;
        startInfo.ArgumentList.Add(scriptPath);
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("The deno probe could not be started.");
    }

    private static async Task<string> ReadProbeAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return stdout + stderr;
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }

            process.Dispose();
        }
    }
}
