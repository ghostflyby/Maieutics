using Maieutics.DenoRepl;
using System.Text.Json;
using Maieutics.Control;

namespace Maieutics.Product.Tests;

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
        var message = new ReplCommMessage(
            ReplCommKind.Open,
            "comm-42",
            "widget-test",
            data,
            null,
            buffers);

        var encoded = ReplCommCodec.Encode(message);
        var decoded = ReplCommCodec.Decode(encoded);

        Assert.Equal(ReplCommKind.Open, decoded.Kind);
        Assert.Equal("comm-42", decoded.CommId);
        Assert.Equal("widget-test", decoded.TargetName);
        Assert.NotNull(decoded.Data);
        Assert.Equal("payload", decoded.Data!.Value.GetProperty("marker").GetString());
        Assert.Equal(2, decoded.Buffers.Count());
        Assert.Equal(buffers[0], decoded.Buffers[0]);
        Assert.Equal(buffers[1], decoded.Buffers[1]);
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
        var message = new ReplCommMessage(
            ReplCommKind.Message,
            "comm-media",
            null,
            data,
            null,
            [buffer]);

        var encoded = ReplCommCodec.Encode(message);
        Assert.True(encoded.Length < ReplControlLimits.MaximumCommMessageBytes);
        var decoded = ReplCommCodec.Decode(encoded);

        Assert.Equal(ReplCommKind.Message, decoded.Kind);
        Assert.Equal("comm-media", decoded.CommId);
        Assert.Equal(buffer, Assert.Single(decoded.Buffers));
    }

    [Fact]
    public void EncodeDecodeRoundTripsCommMessageWithoutDataOrBuffers()
    {
        var message = new ReplCommMessage(
            ReplCommKind.Close,
            "comm-7",
            null,
            null,
            null,
            []);

        var encoded = ReplCommCodec.Encode(message);
        var decoded = ReplCommCodec.Decode(encoded);

        Assert.Equal(ReplCommKind.Close, decoded.Kind);
        Assert.Equal("comm-7", decoded.CommId);
        Assert.Null(decoded.TargetName);
        Assert.Null(decoded.Data);
        Assert.Empty(decoded.Buffers);
    }
}
