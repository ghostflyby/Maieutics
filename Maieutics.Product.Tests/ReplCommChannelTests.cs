using FluentAssertions;
using Maieutics.Control;
using Maieutics.DenoRepl;
using System.Text.Json;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class ReplCommChannelTests
{
    [Fact(Timeout = 30_000)]
    public async Task CommEndpointHandshakesAndReceivesPush()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The comm endpoints are exercised over the control host's Unix socket.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            using var socket = await CommChildSocketStub.ConnectAsync(host.SocketPath, "test-session", timeout.Token);
            await CommChildSocketStub.ExpectReadyAsync(socket, timeout.Token);

            var data = JsonSerializer.SerializeToElement(new { marker = "relay" });
            var message = new ReplCommMessage(
                ReplCommKind.Message,
                "comm-9",
                null,
                data,
                null,
                [new byte[] { 0xAB, 0xCD }]);
            await host.PushCommMessageAsync("test-session", message, timeout.Token);

            var (opcode, payload) = await CommChildSocketStub.ReceiveFrameAsync(socket, timeout.Token);
            opcode.Should().Be(0x2);
            var decoded = ReplCommCodec.Decode(payload);
            decoded.Kind.Should().Be(ReplCommKind.Message);
            decoded.CommId.Should().Be("comm-9");
            decoded.Data.Should().NotBeNull();
            decoded.Data!.Value.GetProperty("marker").GetString().Should().Be("relay");
            decoded.Buffers.Should().ContainSingle().Which.Should().Equal(new byte[] { 0xAB, 0xCD });
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task CommEndpointRejectsUnknownSession()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The comm endpoints are exercised over the control host's Unix socket.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var registry = new ReplControlSessionRegistry();
        registry.Register(Environment.ProcessId, "test-session");
        var (application, host) = await ReplControlTestHost.StartAsync(registry, timeout.Token);
        await using (application)
        {
            using var socket = await CommChildSocketStub.ConnectAsync(host.SocketPath, "unknown-session", timeout.Token);

            // The host closes the socket with a policy violation when the hello session is not owned.
            var (opcode, _) = await CommChildSocketStub.ReceiveFrameAsync(socket, timeout.Token);
            opcode.Should().Be(0x8);
        }
    }
}
