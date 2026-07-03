using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maieutics.Jupyter.Shared;

public sealed record JupyterConnectionInfo(
    string Transport,
    string Ip,
    int ShellPort,
    int IopubPort,
    int StdinPort,
    int ControlPort,
    int HeartbeatPort,
    string SignatureScheme,
    string Key)
{
    public static JupyterConnectionInfo CreateLocalTcp()
    {
        var ports = ReserveTcpPorts(5);

        return new JupyterConnectionInfo(
            "tcp",
            "127.0.0.1",
            ports[0],
            ports[1],
            ports[2],
            ports[3],
            ports[4],
            "hmac-sha256",
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
    }

    public static async Task<JupyterConnectionInfo> ReadFileAsync(string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var file = await JsonSerializer.DeserializeAsync<JupyterConnectionFile>(stream, Json.Options, cancellationToken)
                   ?? throw new JupyterProtocolException($"Connection file '{path}' did not contain valid JSON.");

        return FromConnectionFile(file);
    }

    public async Task WriteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, ToConnectionFile(), Json.Options, cancellationToken);
    }

    public string Endpoint(JupyterChannel channel)
    {
        var port = channel switch
        {
            JupyterChannel.Shell => ShellPort,
            JupyterChannel.Control => ControlPort,
            JupyterChannel.Iopub => IopubPort,
            JupyterChannel.Stdin => StdinPort,
            JupyterChannel.Heartbeat => HeartbeatPort,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
        };

        return $"{Transport}://{Ip}:{port}";
    }

    internal JupyterConnectionFile ToConnectionFile() => new()
    {
        Transport = Transport,
        Ip = Ip,
        ShellPort = ShellPort,
        IopubPort = IopubPort,
        StdinPort = StdinPort,
        ControlPort = ControlPort,
        HeartbeatPort = HeartbeatPort,
        SignatureScheme = SignatureScheme,
        Key = Key
    };

    private static JupyterConnectionInfo FromConnectionFile(JupyterConnectionFile file)
    {
        return new JupyterConnectionInfo(
            file.Transport,
            file.Ip,
            file.ShellPort,
            file.IopubPort,
            file.StdinPort,
            file.ControlPort,
            file.HeartbeatPort,
            file.SignatureScheme,
            file.Key);
    }

    private static int[] ReserveTcpPorts(int count)
    {
        var listeners = new TcpListener[count];

        try
        {
            for (var i = 0; i < count; i++)
            {
                listeners[i] = new TcpListener(IPAddress.Loopback, 0);
                listeners[i].Start();
            }

            return listeners
                .Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port)
                .ToArray();
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }
        }
    }

    private static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
    }
}

internal sealed class JupyterConnectionFile
{
    [JsonPropertyName("transport")] public string Transport { get; set; } = "tcp";

    [JsonPropertyName("ip")] public string Ip { get; set; } = "127.0.0.1";

    [JsonPropertyName("shell_port")] public int ShellPort { get; set; }

    [JsonPropertyName("iopub_port")] public int IopubPort { get; set; }

    [JsonPropertyName("stdin_port")] public int StdinPort { get; set; }

    [JsonPropertyName("control_port")] public int ControlPort { get; set; }

    [JsonPropertyName("hb_port")] public int HeartbeatPort { get; set; }

    [JsonPropertyName("signature_scheme")] public string SignatureScheme { get; set; } = "hmac-sha256";

    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
}