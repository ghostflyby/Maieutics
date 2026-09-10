using System.Text.Json;
using FluentAssertions;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

/// <summary>
///     Pins the connection-file round trip and its publication contract: the file is
///     the kernel's cross-process readiness signal, so writing is atomic (a concurrent
///     reader never observes a truncated file) and no temporary artifacts survive.
/// </summary>
public sealed class JupyterConnectionInfoTests
{
    [Fact(Timeout = 30_000)]
    public async Task WriteFileAsyncRoundTripsThroughReadFileAsync()
    {
        var info = JupyterConnectionInfo.CreateLocalTcp();
        var path = Path.Combine(Path.GetTempPath(), $"conn-{Guid.NewGuid():N}.json");
        try
        {
            await info.WriteFileAsync(path, TestContext.Current.CancellationToken);

            var read = await JupyterConnectionInfo.ReadFileAsync(path, TestContext.Current.CancellationToken);
            read.Transport.Should().Be(info.Transport);
            read.Ip.Should().Be(info.Ip);
            read.ShellPort.Should().Be(info.ShellPort);
            read.IopubPort.Should().Be(info.IopubPort);
            read.StdinPort.Should().Be(info.StdinPort);
            read.ControlPort.Should().Be(info.ControlPort);
            read.HeartbeatPort.Should().Be(info.HeartbeatPort);
            read.SignatureScheme.Should().Be(info.SignatureScheme);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task WriteFileAsyncPublishesAtomicallyWithoutTempArtifacts()
    {
        var info = JupyterConnectionInfo.CreateLocalTcp();
        var path = Path.Combine(Path.GetTempPath(), $"conn-{Guid.NewGuid():N}.json");
        try
        {
            await info.WriteFileAsync(path, TestContext.Current.CancellationToken);

            // The published file parses in one read and no temp sibling remains.
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(json);
            document.RootElement.GetProperty("transport").GetString().Should().Be(info.Transport);
            Directory.EnumerateFiles(
                    Path.GetTempPath(),
                    Path.GetFileName(path) + ".tmp-*")
                .Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
