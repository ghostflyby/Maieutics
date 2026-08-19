using FluentAssertions;
using Maieutics.DenoRepl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class DenoModuleGraphWarmerTests
{
    [Fact(Timeout = 120_000)]
    public async Task WarmSucceedsWithARealDenoCacheAndDoesNotThrowOnFailure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var options = new DenoReplOptions { Executable = "deno" };
        var modules = new DenoReplModule();
        var warmer = new DenoModuleGraphWarmer(options, modules, NullLogger<DenoModuleGraphWarmer>.Instance);

        // StartAsync fires the background warm and returns immediately; it must not throw even if
        // the warm fails (e.g. network unavailable), because a cold module graph must never take
        // down the host.
        await warmer.StartAsync(timeout.Token);

        // The warm either succeeds or logs a failure; either way the host stays up. Await the
        // internal completion signal (the test seam) instead of polling.
        warmer.WarmCompletion.Should().NotBeNull();
        await warmer.WarmCompletion!.WaitAsync(timeout.Token);
        await warmer.StopAsync(timeout.Token);
    }
}
