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
            null,
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
    public void EncodeDecodeRoundTripsCommMessageWithMultiMegabyteBuffer()
    {
        // Comm traffic carries native media buffers (ipywidgets Image/Audio/Video values travel
        // as raw bytes in the message buffers), so a single comm message can legitimately exceed
        // the control bus's 1 MiB ceiling. The dedicated comm ceiling (MaximumCommMessageBytes)
        // allows a few MB; this exercises a ~2 MiB media buffer end to end through the codec.
        var data = JsonSerializer.SerializeToElement(new { marker = "media" });
        var buffer = new byte[2 * 1024 * 1024];
        new Random(42).NextBytes(buffer);
        var message = new JupyterCommMessage(
            JupyterCommKind.Message,
            "comm-media",
            null,
            data,
            null,
            [buffer],
            JupyterWireMessage.Create(
                JupyterMessage.Create(
                    "comm_msg",
                    new JupyterCommMsgContent("comm-media", data),
                    JupyterJsonContext.Default.JupyterCommMsgContent,
                    JupyterSessionIdentity.Create("test"))));

        var encoded = ReplControlHost.CommCodec.Encode(message);
        encoded.Length.Should().BeLessThan(ReplControlLimits.MaximumCommMessageBytes);
        var decoded = ReplControlHost.CommCodec.Decode(encoded);

        decoded.Kind.Should().Be(JupyterCommKind.Message);
        decoded.CommId.Should().Be("comm-media");
        decoded.Buffers.Should().ContainSingle().Which.Should().Equal(buffer);
    }

    [Fact]
    public void EncodeDecodeRoundTripsCommMessageWithoutDataOrBuffers()
    {
        var message = new JupyterCommMessage(
            JupyterCommKind.Close,
            "comm-7",
            null,
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
