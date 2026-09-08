using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Maieutics.Frontend;

/// <summary>The discovery file a frontend reads to reach this executable.</summary>
internal sealed record FrontendDiscoveryFile(
    [property: System.Text.Json.Serialization.JsonPropertyName("version")] int Version,
    [property: System.Text.Json.Serialization.JsonPropertyName("url")] string Url,
    [property: System.Text.Json.Serialization.JsonPropertyName("token")] string Token,
    [property: System.Text.Json.Serialization.JsonPropertyName("pid")] int Pid)
{
    internal const int CurrentVersion = 1;
}

/// <summary>
///     Frontend listener options: the discovery file to publish, a loopback port reserved
///     before Kestrel binds (ephemeral ports cannot be attributed per-listener through the
///     server address features once two TCP listeners share one <c>WebApplication</c>), and
///     the bearer token gating every frontend request.
/// </summary>
internal sealed class FrontendOptions
{
    /// <summary>Gets the discovery file path, or null when the frontend API is disabled.</summary>
    public string? DiscoveryFile { get; private init; }

    /// <summary>Gets the reserved loopback port.</summary>
    public int Port { get; private init; }

    /// <summary>Gets the per-process bearer token.</summary>
    public string Token { get; private init; } = string.Empty;

    /// <summary>Gets whether the frontend API is enabled.</summary>
    public bool Enabled => DiscoveryFile is not null;

    /// <summary>Creates frontend options with a reserved port and a fresh token.</summary>
    public static FrontendOptions Create(string? discoveryFile)
    {
        if (discoveryFile is null)
            return new FrontendOptions { DiscoveryFile = null, Port = 0, Token = string.Empty };

        return new FrontendOptions
        {
            DiscoveryFile = discoveryFile,
            Port = ReserveLoopbackPort(),
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
        };
    }

    /// <summary>Publishes the discovery file. The file's appearance is the readiness signal:
    /// it is written only after Kestrel has bound the reserved port.</summary>
    public void WriteDiscoveryFile()
    {
        if (DiscoveryFile is null) return;

        var file = new FrontendDiscoveryFile(
            FrontendDiscoveryFile.CurrentVersion,
            $"http://127.0.0.1:{Port}",
            Token,
            Environment.ProcessId);
        var directory = Path.GetDirectoryName(DiscoveryFile);
        if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(file, FrontendJsonContext.Default.FrontendDiscoveryFile);
        var temporary = $"{DiscoveryFile}.tmp-{Environment.ProcessId}";
        File.WriteAllText(temporary, json);
        File.Move(temporary, DiscoveryFile, overwrite: true);
    }

    /// <summary>Removes the discovery file so a stopped process cannot be rediscovered.</summary>
    public void DeleteDiscoveryFile()
    {
        if (DiscoveryFile is null) return;

        try
        {
            File.Delete(DiscoveryFile);
        }
        catch (IOException)
        {
            // Best-effort cleanup must not mask shutdown.
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
