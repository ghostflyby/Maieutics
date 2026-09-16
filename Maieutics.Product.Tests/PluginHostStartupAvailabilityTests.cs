using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Plugins;

namespace Maieutics.Product.Tests;

/// <summary>Guards the plugin manager's control-plane availability during startup discovery.
/// Discovery shells out to <c>deno info</c> for registry imports and waits on those child
/// processes for up to a minute per entry. That work used to run while holding the manager's
/// state gate, so every control-plane reader — status, registrations, and each control-bus
/// frame — stalled for the whole scan.</summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class PluginHostStartupAvailabilityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>The state gate must be free while discovery is still parked in its child
    /// process. The fake <c>deno</c> holds the scan open until this test releases it, so the
    /// observation is about ordering, not about elapsed time: the scan provably has not
    /// finished, yet status must already answer.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ControlPlaneAnswersWhileRegistryDiscoveryIsStillRunning()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake deno executable is a shell script.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(Deadline);

        var root = CreatePluginsRootWithJsrImport();
        var marker = Path.Combine(Path.GetTempPath(), $"mc-scan-marker-{Guid.NewGuid():N}");
        var release = Path.Combine(Path.GetTempPath(), $"mc-scan-release-{Guid.NewGuid():N}");
        var manager = new PluginHostManager(
            root,
            Path.Combine(Path.GetTempPath(), $"mc-plugin-data-{Guid.NewGuid():N}"),
            ReplControlHost.CreateSocketPath(),
            new DenoReplOptions { Executable = CreateBlockingFakeDeno(marker, release) },
            new PluginHostModule(),
            new ReplControlSessionRegistry(),
            NullLogger<PluginHostManager>.Instance,
            NullLoggerFactory.Instance,
            TimeProvider.System);

        try
        {
            // StartAsync takes its lifecycle gate synchronously and returns a task; the scan
            // itself runs on the pool after the first yield.
            var start = manager.StartAsync(deadline.Token);
            await WaitForFileAsync(marker, deadline.Token);

            // The child is running, so discovery is in flight and cannot finish until this
            // test writes the release file below.
            File.Exists(release).Should().BeFalse("the scan is still parked in its child process");

            var status = Task.Run(manager.GetStatus, deadline.Token);
            var settled = await Task.WhenAny(status, Task.Delay(TimeSpan.FromSeconds(15), deadline.Token));

            settled.Should().BeSameAs(
                status,
                "the control plane must answer while registry discovery is still parked; "
                + "holding the state gate across the scan stalls it for the whole scan");
            File.Exists(release).Should().BeFalse(
                "the scan had not been released, so status was answered without waiting for it");

            var observed = await status;
            observed.Should().NotBeNull();

            // Let the scan finish so startup can proceed to its (irrelevant here) host launch.
            File.WriteAllText(release, string.Empty);
            await TolerateStartupFailureAsync(start);
        }
        finally
        {
            await manager.DisposeAsync();
            TryDelete(marker);
            TryDelete(release);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>The scan's later stages launch the plugin host with the same fake executable,
    /// which cannot serve the control bus. That failure is unrelated to the property under
    /// test (the gate is released during discovery), so it is observed here but not asserted.</summary>
    private static async Task TolerateStartupFailureAsync(Task start)
    {
        try
        {
            await start.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception exception) when
            (exception is TimeoutException or InvalidOperationException or IOException
                or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Writes a plugins root whose root <c>deno.json</c> declares an exact-version
    /// <c>jsr:</c> import, which is what makes discovery shell out to <c>deno info</c>.</summary>
    private static string CreatePluginsRootWithJsrImport()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mc-scan-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "deno.json"),
            """
            {
              "name": "@maieutics/scan-probe",
              "version": "0.1.0",
              "imports": { "@maieutics/scan-probe-pkg": "jsr:@maieutics/scan-probe-pkg@0.1.0" },
              "permissions": { "default": { "read": ["./"] } }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "maieutics.json"),
            """
            {
              "capabilities": ["tools.invoke"],
              "extensions": { "McpDiscover": [] }
            }
            """);
        return root;
    }

    /// <summary>A fake <c>deno</c> that announces itself, then blocks until the release file
    /// appears, so the test controls exactly when discovery completes. It answers
    /// <c>deno info --json</c> with an empty module list, which is discovery's ordinary
    /// "not found on the registry" path.</summary>
    private static string CreateBlockingFakeDeno(string marker, string release)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc-fake-deno-{Guid.NewGuid():N}.sh");
        File.WriteAllText(
            path,
            $$"""
              #!/bin/sh
              : > "{{marker}}"
              while [ ! -f "{{release}}" ]; do sleep 0.05; done
              printf '{"modules":[]}'
              exit 0
              """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
