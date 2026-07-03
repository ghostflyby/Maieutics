using System.Text.Json.Nodes;
using Maieutics.Jupyter.Shared;
using NetMQ;
using NetMQ.Sockets;

namespace Maieutics.Jupyter.Client;

public sealed class JupyterClient : IJupyterClient
{
    private readonly DealerSocket shell;
    private readonly SubscriberSocket iopub;
    private readonly IJupyterMessageSerializer serializer;
    private readonly JupyterSessionIdentity session;
    private bool disposed;

    public JupyterClient(JupyterConnectionInfo connectionInfo, JupyterSessionIdentity? session = null)
    {
        serializer = new JupyterMessageSerializer(connectionInfo.Key);
        this.session = session ?? JupyterSessionIdentity.Create();
        shell = new DealerSocket();
        iopub = new SubscriberSocket();

        shell.Options.Identity = Guid.NewGuid().ToByteArray();
        shell.Connect(connectionInfo.Endpoint(JupyterChannel.Shell));
        iopub.Connect(connectionInfo.Endpoint(JupyterChannel.Iopub));
        iopub.SubscribeToAnyTopic();
    }

    public async ValueTask<JupyterMessage> RequestKernelInfoAsync(CancellationToken cancellationToken = default)
    {
        return await SendShellAsync("kernel_info_request", new JsonObject(), cancellationToken);
    }

    public async ValueTask<ExecuteResult> ExecuteAsync(string code, CancellationToken cancellationToken = default)
    {
        var request = JupyterMessage.Create(
            "execute_request",
            new JsonObject
            {
                ["code"] = code,
                ["silent"] = false,
                ["store_history"] = true,
                ["user_expressions"] = new JsonObject(),
                ["allow_stdin"] = false,
                ["stop_on_error"] = true
            },
            session);

        Send(shell, request);

        var iopubMessages = new List<JupyterMessage>();
        JupyterMessage? reply = null;
        var idleSeen = false;

        while (reply is null || !idleSeen)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Receive(shell, TimeSpan.FromMilliseconds(50)) is { } shellMessage &&
                shellMessage.ParentHeader?.MessageId == request.Header.MessageId &&
                shellMessage.Header.MessageType == "execute_reply")
            {
                reply = shellMessage;
            }

            while (Receive(iopub, TimeSpan.Zero) is { } iopubMessage)
            {
                if (iopubMessage.ParentHeader?.MessageId == request.Header.MessageId)
                {
                    iopubMessages.Add(iopubMessage);
                    idleSeen = idleSeen || IsIdleStatus(iopubMessage);
                }
            }

            if (reply is null || !idleSeen)
            {
                await Task.Delay(10, cancellationToken);
            }
        }

        var status = reply.Content["status"]?.GetValue<string>() ?? "unknown";
        var executionCount = reply.Content["execution_count"]?.GetValue<int?>();
        return new ExecuteResult(status, executionCount, iopubMessages, reply);
    }

    public async ValueTask<JupyterMessage> SendShellAsync(string messageType, JsonObject content,
        CancellationToken cancellationToken = default)
    {
        var request = JupyterMessage.Create(messageType, content, session);
        Send(shell, request);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Receive(shell, TimeSpan.FromMilliseconds(50)) is { } response &&
                response.ParentHeader?.MessageId == request.Header.MessageId)
            {
                return response;
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        shell.Dispose();
        iopub.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Send(IOutgoingSocket socket, JupyterMessage message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var netMqMessage = new NetMQMessage();
        foreach (var frame in serializer.Serialize(message))
        {
            netMqMessage.Append(frame);
        }

        socket.SendMultipartMessage(netMqMessage);
    }

    private JupyterMessage? Receive(IReceivingSocket socket, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var message = new NetMQMessage();
        return !socket.TryReceiveMultipartMessage(timeout, ref message)
            ? null
            : serializer.Deserialize(message.Select(frame => frame.ToByteArray()).ToArray());
    }

    private static bool IsIdleStatus(JupyterMessage message)
    {
        return message.MessageType == "status" &&
               message.Content["execution_state"]?.GetValue<string>() == "idle";
    }
}