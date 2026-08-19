using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.DenoExecution;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoPermissionBrokerTests
{
    [Fact(Timeout = 30_000)]
    public async Task RealDenoChildGetsAllowAndDenyDecisionsFromTheBroker()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var root = Path.Combine(Path.GetTempPath(), $"mc-broker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "probe.ts");
            await File.WriteAllTextAsync(
                scriptPath,
                """
                try { await Deno.readTextFile("/tmp/maieutics-perm-eval/net_probe.ts"); console.log("read OK"); }
                catch (e) { console.log("read DENIED:", String(e).split("\n")[0]); }
                try { console.log("PATH:", Deno.env.get("PATH")); }
                catch (e) { console.log("env DENIED:", String(e).split("\n")[0]); }
                """,
                deadline.Token);
            var broker = DenoPermissionBroker.Create(new CollectingLogger<DenoPermissionBroker>());

            var output = await RunChildAsync(broker, CreatePolicy(0), scriptPath, deadline.Token);

            output.Should().Contain("read OK");
            output.Should().Contain("env DENIED: NotCapable");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task UnmatchedRequestsDenyByDefault()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var root = Path.Combine(Path.GetTempPath(), $"mc-broker-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "probe.ts");
            await File.WriteAllTextAsync(
                scriptPath,
                """
                try { await Deno.readTextFile("/tmp/maieutics-perm-eval/net_probe.ts"); console.log("read OK"); }
                catch (e) { console.log("read DENIED:", String(e).split("\n")[0]); }
                """,
                deadline.Token);
            var broker = DenoPermissionBroker.Create(new CollectingLogger<DenoPermissionBroker>());

            var output = await RunChildAsync(broker, EffectivePolicy.Default, scriptPath, deadline.Token);

            output.Should().Contain("read DENIED: NotCapable");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ResolverMatchesExactDenyOverAllow()
    {
        var policy = Build(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["/tmp"], Deny = ["/tmp/secret"] }));

        var decision = DenoPermissionResolver.Resolve(policy, "read", "/tmp/secret/file.txt");

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("read access");
    }

    [Fact(Timeout = 30_000)]
    public async Task ResolverAllowsExactGrantAndDeniesUnknownKind()
    {
        var policy = Build(
            (PermissionKind.Env, new PermissionKindRules { Allow = ["HOME"] }));

        DenoPermissionResolver.Resolve(policy, "env", "HOME").IsAllowed.Should().BeTrue();
        DenoPermissionResolver.Resolve(policy, "env", "PATH").IsAllowed.Should().BeFalse();
        DenoPermissionResolver.Resolve(policy, "unknown", "x").IsAllowed.Should().BeFalse();
    }

    private static EffectivePolicy CreatePolicy(int _)
    {
        return Build(
            (PermissionKind.Read, new PermissionKindRules { Allow = ["/tmp/maieutics-perm-eval"] }),
            (PermissionKind.Env, new PermissionKindRules { Allow = ["HOME"] }));
    }

    private static EffectivePolicy Build(params (PermissionKind Kind, PermissionKindRules Rules)[] kinds)
    {
        return PermissionLayerStore.Build(
            [new PermissionLayer { Kinds = kinds.ToDictionary(static entry => entry.Kind, static entry => entry.Rules) }],
            new VariableTable(new FakeVariableSource()));
    }

    private static async Task<string> RunChildAsync(
        DenoPermissionBroker broker,
        EffectivePolicy policy,
        string scriptPath,
        CancellationToken cancellationToken)
    {
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
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("The deno probe could not be started.");
        broker.RegisterProcess(process.Id, policy);
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return stdout + stderr;
        }
        finally
        {
            broker.UnregisterProcess(process.Id);
        }
    }

    private sealed class FakeVariableSource : Execution.IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        internal ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
        }
    }
}
