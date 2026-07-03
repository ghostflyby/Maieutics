using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public class JupyterMessageSerializerTests
{
    [Fact]
    public void RoundTripPreservesWireFieldsAndBuffers()
    {
        var serializer = new JupyterMessageSerializer("secret");
        var session = new JupyterSessionIdentity("session-1", "tester");
        var message = JupyterMessage.Create(
            "kernel_info_request",
            new JsonObject(),
            session,
            identities: ["client-id"u8.ToArray()],
            buffers: ["buffer"u8.ToArray()]);

        var frames = serializer.Serialize(message);
        var roundTripped = serializer.Deserialize(frames);

        roundTripped.Header.MessageId.Should().Be(message.Header.MessageId);
        roundTripped.Header.MessageType.Should().Be("kernel_info_request");
        roundTripped.Identities.Single().Should().Equal(Encoding.UTF8.GetBytes("client-id"));
        roundTripped.Buffers.Single().Should().Equal(Encoding.UTF8.GetBytes("buffer"));
    }

    [Fact]
    public void DeserializeRejectsInvalidSignature()
    {
        var serializer = new JupyterMessageSerializer("secret");
        var message = JupyterMessage.Create(
            "kernel_info_request",
            new JsonObject(),
            JupyterSessionIdentity.Create());
        var frames = serializer.Serialize(message).Select(frame => frame.ToArray()).ToArray();
        frames[^1] = Encoding.UTF8.GetBytes("{\"tampered\":true}");

        var act = () => serializer.Deserialize(frames);

        act.Should().Throw<JupyterProtocolException>()
            .WithMessage("*signature*");
    }
}