using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class JupyterMessageSerializerTests
{
    [Fact]
    public void RoundTripPreservesRoutingMessageAndBuffers()
    {
        var serializer = new JupyterMessageSerializer("secret");
        var created = JupyterMessage.Create(
            "kernel_info_request",
            new JupyterEmptyContent(),
            JupyterJsonContext.Default.JupyterEmptyContent,
            new JupyterSessionIdentity("session-1", "tester"));
        var message = created with
        {
            Header = new JupyterMessageHeader
            {
                MessageId = created.Header.MessageId,
                Username = created.Header.Username,
                Session = created.Header.Session,
                Date = created.Header.Date,
                MessageType = created.Header.MessageType,
                Version = created.Header.Version,
                SubshellId = "subshell-1",
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["future_header_field"] = JsonSerializer.SerializeToElement("preserved")
                }
            }
        };
        var wire = new JupyterWireMessage(
            [Encoding.UTF8.GetBytes("client-id")],
            message,
            [Encoding.UTF8.GetBytes("buffer")]);

        var roundTripped = serializer.Deserialize(serializer.Serialize(wire));

        roundTripped.Message.Header.MessageId.Should().Be(message.Header.MessageId);
        roundTripped.Message.Header.Version.Should().Be("5.5");
        roundTripped.Message.Header.SubshellId.Should().Be("subshell-1");
        roundTripped.Message.Header.ExtensionData!["future_header_field"].GetString().Should().Be("preserved");
        roundTripped.Identities.Single().Should().Equal(Encoding.UTF8.GetBytes("client-id"));
        roundTripped.Buffers.Single().Should().Equal(Encoding.UTF8.GetBytes("buffer"));
    }

    [Fact]
    public void EmptyKeyUsesEmptySignature()
    {
        var serializer = new JupyterMessageSerializer(string.Empty);
        var message = JupyterMessage.Create(
            "kernel_info_request",
            new JupyterEmptyContent(),
            JupyterJsonContext.Default.JupyterEmptyContent,
            JupyterSessionIdentity.Create());

        var frames = serializer.Serialize(JupyterWireMessage.Create(message));

        frames[1].Should().BeEmpty();
        serializer.Deserialize(frames).Message.Header.MessageId.Should().Be(message.Header.MessageId);
    }

    [Fact]
    public void DeserializeRejectsInvalidSignature()
    {
        var serializer = new JupyterMessageSerializer("secret");
        var message = JupyterMessage.Create(
            "kernel_info_request",
            new JupyterEmptyContent(),
            JupyterJsonContext.Default.JupyterEmptyContent,
            JupyterSessionIdentity.Create());
        var frames = serializer.Serialize(JupyterWireMessage.Create(message)).Select(frame => frame.ToArray())
            .ToArray();
        frames[^1] = Encoding.UTF8.GetBytes("{\"tampered\":true}");

        var act = () => serializer.Deserialize(frames);

        act.Should().Throw<JupyterProtocolException>().WithMessage("*signature*");
    }

    [Fact]
    public void UnsupportedSignatureSchemeFailsFast()
    {
        var act = () => new JupyterMessageSerializer("secret", "hmac-sha512");

        act.Should().Throw<NotSupportedException>().WithMessage("*hmac-sha512*");
    }

    [Fact]
    public void UnsupportedConnectionSignatureSchemeFailsFast()
    {
        var connection = JupyterConnectionInfo.CreateLocalTcp() with { SignatureScheme = "hmac-sha512" };

        var act = connection.ValidateSupported;

        act.Should().Throw<NotSupportedException>().WithMessage("*hmac-sha512*");
    }

    [Fact]
    public void CurveConnectionFailsFast()
    {
        var connection = JupyterConnectionInfo.CreateLocalTcp() with { ServerKey = "curve-server-key" };

        var act = connection.ValidateSupported;

        act.Should().Throw<NotSupportedException>().WithMessage("*CurveZMQ*");
    }

    [Fact]
    public void LanguageServiceDtoRoundTripUsesGeneratedMetadata()
    {
        var reply = new JupyterCompleteReply
        {
            Status = "ok",
            Matches = ["console"],
            CursorStart = 0,
            CursorEnd = 4,
            Metadata = new Dictionary<string, JsonElement>
            {
                ["source"] = JsonSerializer.SerializeToElement("test")
            }
        };

        var json = JsonSerializer.Serialize(reply, JupyterJsonContext.Default.JupyterCompleteReply);
        var roundTripped = JsonSerializer.Deserialize(json, JupyterJsonContext.Default.JupyterCompleteReply);

        roundTripped.Should().NotBeNull();
        roundTripped!.Status.Should().Be("ok");
        roundTripped.Matches.Should().Equal("console");
        roundTripped.CursorStart.Should().Be(0);
        roundTripped.CursorEnd.Should().Be(4);
        roundTripped.Metadata["source"].GetString().Should().Be("test");
    }

    [Fact]
    public void KernelInfoMissingNewFieldsUsesCompatibilityDefaults()
    {
        const string json = """
                            {
                              "protocol_version": "5.3",
                              "implementation": "legacy",
                              "implementation_version": "1.0",
                              "language_info": { "name": "test", "version": "1.0" }
                            }
                            """;

        var info = JsonSerializer.Deserialize(json, JupyterJsonContext.Default.JupyterKernelInfo);

        info.Should().NotBeNull();
        info!.Status.Should().Be("ok");
        info.Debugger.Should().BeFalse();
        info.SupportedFeatures.Should().BeNull();
    }
}