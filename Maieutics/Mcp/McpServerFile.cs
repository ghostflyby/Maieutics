using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Maieutics.Mcp;

// MCP server configuration follows the conventional lowercase mcpServers format used by Claude Code,
// Cursor, and JetBrains clients so existing server blocks can be copied directly (ADR 0013). This
// parser owns the schema: plugin-scoped mcp.json data files are its only consumer since ADR 0033
// removed the kernel-level mcp.json beside maieutics.json.
public sealed class MaieuticsMcpServerOptions
{
    public bool Enabled { get; set; } = true;

    public string? Command { get; set; }

    public string[] Arguments { get; set; } = [];

    // Lowercase Claude Code/Cursor spelling: the configuration binder matches keys to
    // properties whole-word (case-insensitively), so "args" only ever binds this alias
    // and "env" only Env — the long names never saw the lowercase payload.
    public string[]? Args { get; set; }

    public string? WorkingDirectory { get; set; }

    public Dictionary<string, string?> EnvironmentVariables { get; set; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, string?>? Env { get; set; }

    public string? Url { get; set; }

    public Dictionary<string, string> Headers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // Maieutics extension keys (unknown to Claude/Cursor-format files). Null resolves by
    // transport: stdio servers default to true, HTTP servers to false (ADR 0029 decision 1).
    public bool? Roots { get; set; }

    public bool? Elicitation { get; set; }
}

/// <summary>Reads MCP server definitions from an mcp.json-shaped configuration source
/// (the <c>mcpServers</c> or <c>servers</c> top-level key). Structural and semantic
/// validation is strict: the caller treats any thrown <see cref="InvalidOperationException" />
/// as a failed configuration load and keeps its previous snapshot.</summary>
internal static class McpServerFile
{
    /// <summary>Reads one mcp.json file and returns its enabled server definitions.
    /// Relative <c>workingDirectory</c> values resolve against
    /// <paramref name="workingDirectoryBase" /> (the owning plugin's root directory).</summary>
    public static IReadOnlyList<McpServerDefinition> ReadFile(
        string path,
        string workingDirectoryBase,
        string serverIdPrefix)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return ReadJson(document.RootElement.Clone(), workingDirectoryBase, serverIdPrefix);
    }

    /// <summary>Interprets an already-collected data entry (the parsed content of a
    /// string-valued entrypoint, ADR 0033) through the same schema pipeline as a file.
    /// The element is re-encoded with a raw writer — no reflection-based serialization,
    /// so the path is trimming- and AOT-safe.</summary>
    public static IReadOnlyList<McpServerDefinition> ReadJson(
        JsonElement root,
        string workingDirectoryBase,
        string serverIdPrefix)
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            root.WriteTo(writer);

        stream.Position = 0;
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
        return ReadServers(
            SelectServersSection(configuration),
            workingDirectoryBase,
            serverIdPrefix);
    }

    /// <summary>Selects the server map section: <c>mcpServers</c> (Claude Code/Cursor) or
    /// <c>servers</c> (VS Code); combining both is rejected.</summary>
    public static IConfigurationSection SelectServersSection(IConfiguration configuration)
    {
        var mcpServers = configuration.GetSection("mcpServers");
        var servers = configuration.GetSection("servers");
        if (mcpServers.GetChildren().Any() && servers.GetChildren().Any())
            throw new InvalidOperationException(
                "mcp.json must not combine the 'mcpServers' and 'servers' top-level keys.");

        return mcpServers.GetChildren().Any() ? mcpServers : servers;
    }

    /// <summary>Binds and validates every server entry under <paramref name="section" />.
    /// Disabled entries are skipped before key validation (VS Code convention), so a
    /// disabled server may carry otherwise-invalid keys.</summary>
    public static IReadOnlyList<McpServerDefinition> ReadServers(
        IConfigurationSection section,
        string workingDirectoryBase,
        string serverIdPrefix)
    {
        var result = new List<McpServerDefinition>();
        var serverIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serverSection in section.GetChildren())
        {
            var serverKey = serverSection.Key;
            if (string.IsNullOrWhiteSpace(serverKey))
                throw new InvalidOperationException("MCP server identifiers must be non-empty and unique.");
            var serverId = serverIdPrefix + serverKey;
            if (!serverIds.Add(serverId))
                throw new InvalidOperationException("MCP server identifiers must be non-empty and unique.");

            var serverOptions = new MaieuticsMcpServerOptions();
            serverSection.Bind(serverOptions);
            if (!serverOptions.Enabled) continue;

            var transport = ResolveMcpTransport(serverId, serverSection);
            var allowedKeys = transport == McpServerTransportKind.Stdio
                ? new[]
                {
                    "Enabled", "Type", "Transport", "Command", "Arguments", "Args", "WorkingDirectory",
                    "EnvironmentVariables", "Env", "InitializationTimeout", "RequestTimeout", "ShutdownTimeout",
                    "Roots", "Elicitation"
                }
                :
                [
                    "Enabled", "Type", "Transport", "Url", "Headers", "ConnectionTimeout",
                    "InitializationTimeout", "RequestTimeout", "Roots", "Elicitation"
                ];
            ValidateConfigurationKeys(serverSection, $"MCP server '{serverId}'", allowedKeys);

            ValidatePositiveTimeout(serverOptions.InitializationTimeout, serverId, "InitializationTimeout");
            ValidatePositiveTimeout(serverOptions.RequestTimeout, serverId, "RequestTimeout");

            // Maieutics extension keys default by transport: a stdio server is launched through the
            // permission module (same trust line), a remote HTTP server gets nothing until opted in
            // (ADR 0029 decision 1).
            var rootsEnabled = serverOptions.Roots ?? transport == McpServerTransportKind.Stdio;
            var elicitationEnabled = serverOptions.Elicitation ?? transport == McpServerTransportKind.Stdio;

            McpTransportDefinition transportDefinition;
            var shutdownTimeout = TimeSpan.Zero;
            var connectionTimeout = TimeSpan.Zero;

            if (transport == McpServerTransportKind.Stdio)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(serverOptions.Command);
                ArgumentNullException.ThrowIfNull(serverOptions.Arguments);
                ArgumentNullException.ThrowIfNull(serverOptions.EnvironmentVariables);
                var command = serverOptions.Command;
                var arguments = (serverOptions.Args is { Length: > 0 } lowerCase
                    ? lowerCase
                    : serverOptions.Arguments).ToArray();
                var boundEnvironment = serverOptions.Env is { Count: > 0 } envAlias
                    ? envAlias
                    : serverOptions.EnvironmentVariables;
                var environmentVariables = new Dictionary<string, string?>(
                    boundEnvironment,
                    StringComparer.Ordinal);
                if (serverSection.GetSection("WorkingDirectory").Value is not null &&
                    string.IsNullOrWhiteSpace(serverOptions.WorkingDirectory))
                    throw new InvalidOperationException(
                        $"MCP server '{serverId}' WorkingDirectory cannot be empty when configured.");

                string? workingDirectory = null;
                if (!string.IsNullOrWhiteSpace(serverOptions.WorkingDirectory))
                    workingDirectory = Path.GetFullPath(serverOptions.WorkingDirectory, workingDirectoryBase);

                ValidatePositiveTimeout(serverOptions.ShutdownTimeout, serverId, "ShutdownTimeout");
                shutdownTimeout = serverOptions.ShutdownTimeout;
                transportDefinition = new StdioMcpTransportDefinition(
                    command,
                    arguments,
                    workingDirectory,
                    environmentVariables);
            }
            else
            {
                if (!Uri.TryCreate(serverOptions.Url, UriKind.Absolute, out var endpoint) ||
                    (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        $"MCP server '{serverId}' Url must be an absolute HTTP or HTTPS URI.");

                if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                    !endpoint.IsLoopback)
                    throw new InvalidOperationException(
                        $"MCP server '{serverId}' must use HTTPS unless its endpoint is loopback.");

                ArgumentNullException.ThrowIfNull(serverOptions.Headers);
                foreach (var pair in serverOptions.Headers)
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                        throw new InvalidOperationException(
                            $"MCP server '{serverId}' contains an invalid HTTP header.");

                var headers = new Dictionary<string, string>(serverOptions.Headers, StringComparer.OrdinalIgnoreCase);
                ValidatePositiveTimeout(serverOptions.ConnectionTimeout, serverId, "ConnectionTimeout");
                connectionTimeout = serverOptions.ConnectionTimeout;
                transportDefinition = new HttpMcpTransportDefinition(endpoint, headers);
            }

            var generationKey = McpServerDefinition.CreateGenerationKey(
                transportDefinition,
                serverOptions.InitializationTimeout,
                serverOptions.RequestTimeout,
                shutdownTimeout,
                connectionTimeout,
                rootsEnabled,
                elicitationEnabled);
            result.Add(new McpServerDefinition(
                serverId,
                transportDefinition,
                serverOptions.InitializationTimeout,
                serverOptions.RequestTimeout,
                shutdownTimeout,
                connectionTimeout,
                rootsEnabled,
                elicitationEnabled,
                generationKey));
        }

        return result;
    }

    private static McpServerTransportKind ResolveMcpTransport(
        string serverId,
        IConfigurationSection serverSection)
    {
        var configured = serverSection["Transport"] ?? serverSection["Type"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Enum.TryParse<McpServerTransportKind>(configured, true, out var transport) &&
                Enum.IsDefined(transport))
                return transport;

            if (string.Equals(configured, "sse", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"MCP server '{serverId}' uses the unsupported 'sse' transport.");

            throw new InvalidOperationException(
                $"MCP server '{serverId}' must configure Transport as 'stdio' or 'http'.");
        }

        return !string.IsNullOrWhiteSpace(serverSection["Url"])
            ? McpServerTransportKind.Http
            : McpServerTransportKind.Stdio;
    }

    private static void ValidatePositiveTimeout(TimeSpan value, string serverId, string field)
    {
        if (value <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"MCP server '{serverId}' {field} must be positive.");
    }

    private static void ValidateConfigurationKeys(
        IConfigurationSection section,
        string description,
        params string[] allowed)
    {
        var allowedKeys = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = section.GetChildren().FirstOrDefault(child => !allowedKeys.Contains(child.Key));
        if (unknown is not null)
            throw new InvalidOperationException(
                $"Configuration field '{unknown.Path}' is not valid for {description}.");
    }
}
