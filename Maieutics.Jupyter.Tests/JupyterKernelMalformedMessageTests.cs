using System.Buffers;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;
using ZmqSharp;

namespace Maieutics.Jupyter.Tests;

/// <summary>
///     Pins the malformed-message contract of the kernel host: one poisoned message must
///     never silently kill the shell/control loops or the router lifetime. Known request
///     types with unparseable content still honor their pending reply contracts, a
///     malformed input_reply fails only its pending input, and genuine transport-level
///     fatal faults (bad HMAC) surface on <see cref="IJupyterKernel.Completion" /> instead
///     of being swallowed.
/// </summary>
[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class JupyterKernelMalformedMessageTests
{
    [Fact(Timeout = 30_000)]
    public async Task MalformedExecuteRequestYieldsErrorReplyAndKernelKeepsServing()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var cancellationToken = deadline.Token;
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            new TestKernelApplication(),
            cancellationToken: cancellationToken);
        var serializer = new JupyterMessageSerializer(connection.Key, connection.SignatureScheme);
        await using var shell = new ZDealerSocket();
        await shell.ConnectAsync(connection.Endpoint(JupyterChannel.Shell), cancellationToken);

        // "code" carries a number where the protocol requires a string: the content is
        // well-formed JSON but fails to bind to JupyterExecuteRequest.
        var malformed = RawMessage(
            "execute_request",
            JsonSerializer.SerializeToElement(new { code = 42 }));
        await SendAsync(shell, serializer, malformed, cancellationToken);
        var errorReply = await ReceiveAsync(shell, serializer, cancellationToken);
        errorReply.Message.MessageType.Should().Be("execute_reply");
        errorReply.Message.ParentHeader?.MessageId.Should().Be(malformed.Header.MessageId);
        errorReply.Message.GetContent(JupyterJsonContext.Default.JupyterExecuteReply)
            .Status.Should().Be("error");

        var valid = RawMessage(
            "execute_request",
            JsonSerializer.SerializeToElement(new
            {
                code = "1 + 1",
                silent = false,
                store_history = true,
                allow_stdin = false
            }));
        await SendAsync(shell, serializer, valid, cancellationToken);
        var okReply = await ReceiveAsync(shell, serializer, cancellationToken);
        okReply.Message.MessageType.Should().Be("execute_reply");
        okReply.Message.ParentHeader?.MessageId.Should().Be(valid.Header.MessageId);
        okReply.Message.GetContent(JupyterJsonContext.Default.JupyterExecuteReply)
            .Status.Should().Be("ok");

        host.Completion.IsCompleted.Should().BeFalse();
        await host.StopAsync(cancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task MalformedInputReplyFailsOnlyItsPendingInputAndKeepsHostHealthy()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var cancellationToken = deadline.Token;
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            new TestKernelApplication(),
            cancellationToken: cancellationToken);
        await using var client = await JupyterClient.ConnectAsync(
            connection,
            cancellationToken: cancellationToken);
        var serializer = new JupyterMessageSerializer(connection.Key, connection.SignatureScheme);

        var execution = await client.ExecuteAsync(
            new JupyterExecuteRequest("input", AllowStdin: true),
            cancellationToken);
        JupyterInputRequest? input = null;
        await foreach (var output in execution.Outputs.WithCancellation(cancellationToken))
            if (output is JupyterInputRequest request)
            {
                input = request;
                break;
            }

        input.Should().NotBeNull();

        // "value" carries a number where the protocol requires a string. The reply must
        // fail only the pending input, not the router lifetime.
        await using var stdin = new ZDealerSocket();
        await stdin.ConnectAsync(connection.Endpoint(JupyterChannel.Stdin), cancellationToken);
        var malformedReply = RawMessage(
            "input_reply",
            JsonSerializer.SerializeToElement(new { value = 42 }),
            input!.Header);
        await SendAsync(stdin, serializer, malformedReply, cancellationToken);

        var completion = await execution.Completion.WaitAsync(cancellationToken);
        completion.Reply.Status.Should().Be("error");

        host.Completion.IsCompleted.Should().BeFalse();
        var followUp = await client.ExecuteAsync(
            new JupyterExecuteRequest("sum"),
            cancellationToken);
        (await followUp.Completion.WaitAsync(cancellationToken)).Reply.Status.Should().Be("ok");

        var shutdown = await client.ShutdownAsync(false, cancellationToken);
        shutdown.Status.Should().Be("ok");
        await host.Completion.WaitAsync(cancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task SignatureFailureFaultsHostCompletionWithTheRealCause()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var cancellationToken = deadline.Token;
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var host = await JupyterKernelHost.StartAsync(
            connection,
            new TestKernelApplication(),
            cancellationToken: cancellationToken);
        var serializer = new JupyterMessageSerializer(connection.Key, connection.SignatureScheme);
        await using var shell = new ZDealerSocket();
        await shell.ConnectAsync(connection.Endpoint(JupyterChannel.Shell), cancellationToken);

        // Corrupt the signature frame (delimiter is frame 0, signature is frame 1): the
        // kernel transport must terminate and the host must observe the fatal cause
        // instead of completing successfully.
        var frames = serializer.Serialize(new JupyterWireMessage([], RawMessage(
            "kernel_info_request",
            JsonSerializer.SerializeToElement(new { })), [])).ToArray();
        frames[1] = [0x00, 0x01, 0x02];
        await shell.SendAsync(ZMessage.FromOwned(frames), cancellationToken);

        (await host.Completion.Invoking(static task => task).Should()
                .ThrowAsync<JupyterProtocolException>())
            .Which.Should().BeOfType<JupyterSignatureException>();

        // Disposal of a fatally-faulted host surfaces the same cause to its caller.
        (await host.DisposeAsync().AsTask().Invoking(static task => task).Should()
                .ThrowAsync<JupyterProtocolException>())
            .Which.Should().BeOfType<JupyterSignatureException>();
    }

    private static JupyterMessage RawMessage(string messageType, JsonElement content, JupyterMessageHeader? parent = null)
    {
        // JupyterMessage.Create serializes the JsonElement back to raw content, so the
        // wire message carries exactly the (possibly wrong-typed) payload below.
        return JupyterMessage.Create(
            messageType,
            content,
            JupyterJsonContext.Default.JsonElement,
            JupyterSessionIdentity.Create("raw-client"),
            parent);
    }

    private static ValueTask SendAsync(
        ZDealerSocket dealer,
        JupyterMessageSerializer serializer,
        JupyterMessage message,
        CancellationToken cancellationToken)
    {
        return dealer.SendAsync(
            ZMessage.FromOwned(serializer.Serialize(new JupyterWireMessage([], message, [])).ToArray()),
            cancellationToken);
    }

    private static async Task<JupyterWireMessage> ReceiveAsync(
        ZDealerSocket dealer,
        JupyterMessageSerializer serializer,
        CancellationToken cancellationToken)
    {
        using var message = await dealer.Messages.ReadAsync(cancellationToken);
        var frames = new byte[message.Count][];
        for (var index = 0; index < message.Count; index++)
            frames[index] = message[index].ToSequence().ToArray();

        return serializer.Deserialize(frames);
    }

    private sealed class TestKernelApplication : IJupyterKernelApplication
    {
        public JupyterKernelInfo KernelInfo { get; } = new(
            "5.5",
            "maieutics-test",
            "1.0",
            new JupyterLanguageInfo("test", "1.0"));

        public async ValueTask<JupyterExecuteResult> ExecuteAsync(
            JupyterExecutionContext context,
            JupyterExecuteRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Code == "input")
            {
                var name = await context.RequestInputAsync("Name: ", cancellationToken: cancellationToken);
                await context.WriteStdoutAsync($"Hello {name}", cancellationToken);
            }

            return JupyterExecuteResult.Ok;
        }
    }
}
