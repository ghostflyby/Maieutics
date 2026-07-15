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
    public string? PublicKey { get; init; }

    public string? PrivateKey { get; init; }

    public string? ServerKey { get; init; }

    public string? Keychain { get; init; }

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
        var file = await JsonSerializer.DeserializeAsync(
                       stream,
                       JupyterConnectionJsonContext.Default.JupyterConnectionFile,
                       cancellationToken)
                   ?? throw new JupyterProtocolException($"Connection file '{path}' did not contain valid JSON.");

        var connectionInfo = FromConnectionFile(file);
        connectionInfo.ValidateSupported();
        return connectionInfo;
    }

    public async Task WriteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidateSupported();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            ToConnectionFile(),
            JupyterConnectionJsonContext.Default.JupyterConnectionFile,
            cancellationToken);
    }

    public string Endpoint(JupyterChannel channel)
    {
        ValidateSupported();
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
        Key = Key,
        PublicKey = PublicKey,
        PrivateKey = PrivateKey,
        ServerKey = ServerKey,
        Keychain = Keychain
    };

    public void ValidateSupported()
    {
        if (!string.Equals(Transport, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Jupyter transport '{Transport}' is not supported.");
        }

        if (!string.Equals(SignatureScheme, "hmac-sha256", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Jupyter signature scheme '{SignatureScheme}' is not supported.");
        }

        if (PublicKey is not null || PrivateKey is not null || ServerKey is not null || Keychain is not null)
        {
            throw new NotSupportedException("CurveZMQ Jupyter connections are not supported by the NetMQ transport.");
        }
    }

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
            file.Key)
        {
            PublicKey = file.PublicKey,
            PrivateKey = file.PrivateKey,
            ServerKey = file.ServerKey,
            Keychain = file.Keychain
        };
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
                listener?.Stop();
            }
        }
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

    [JsonPropertyName("public_key")] public string? PublicKey { get; set; }

    [JsonPropertyName("private_key")] public string? PrivateKey { get; set; }

    [JsonPropertyName("server_key")] public string? ServerKey { get; set; }

    [JsonPropertyName("keychain")] public string? Keychain { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JupyterConnectionFile))]
internal partial class JupyterConnectionJsonContext : JsonSerializerContext;