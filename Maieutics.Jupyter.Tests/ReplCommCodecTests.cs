using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Control;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class ReplCommCodecTests
{
    [Fact]
    public void EncodeDecodeRoundTripsCommMessageWithBuffers()
    {
        var data = JsonSerializer.SerializeToElement(new { marker = "payload" });
        var buffers = new[]
        {
            new byte[] { 0x00, 0x01, 0x02 },
            "binary".Select(static c => (byte)c).ToArray()
        };
        var message = new JupyterCommMessage(
            JupyterCommKind.Open,
            "comm-42",
            "widget-test",
            data,
            buffers,
            JupyterWireMessage.Create(
                JupyterMessage.Create(
                    "comm_open",
                    new JupyterCommOpenContent("comm-42", "widget-test", data),
                    JupyterJsonContext.Default.JupyterCommOpenContent,
                    JupyterSessionIdentity.Create("test"))));

        var encoded = ReplControlHost.CommCodec.Encode(message);
        var decoded = ReplControlHost.CommCodec.Decode(encoded);

        decoded.Kind.Should().Be(JupyterCommKind.Open);
        decoded.CommId.Should().Be("comm-42");
        decoded.TargetName.Should().Be("widget-test");
        decoded.Data.Should().NotBeNull();
        decoded.Data!.Value.GetProperty("marker").GetString().Should().Be("payload");
        decoded.Buffers.Should().HaveCount(2);
        decoded.Buffers[0].Should().Equal(buffers[0]);
        decoded.Buffers[1].Should().Equal(buffers[1]);
    }

    [Fact]
    public void EncodeDecodeRoundTripsCommMessageWithoutDataOrBuffers()
    {
        var message = new JupyterCommMessage(
            JupyterCommKind.Close,
            "comm-7",
            null,
            null,
            [],
            JupyterWireMessage.Create(
                JupyterMessage.Create(
                    "comm_close",
                    new JupyterCommCloseContent("comm-7"),
                    JupyterJsonContext.Default.JupyterCommCloseContent,
                    JupyterSessionIdentity.Create("test"))));

        var encoded = ReplControlHost.CommCodec.Encode(message);
        var decoded = ReplControlHost.CommCodec.Decode(encoded);

        decoded.Kind.Should().Be(JupyterCommKind.Close);
        decoded.CommId.Should().Be("comm-7");
        decoded.TargetName.Should().BeNull();
        decoded.Data.Should().BeNull();
        decoded.Buffers.Should().BeEmpty();
    }
}
