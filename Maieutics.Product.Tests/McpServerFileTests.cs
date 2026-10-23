using System.Text.Json;
using System.Text.Json.Nodes;
using Maieutics.Mcp;

namespace Maieutics.Product.Tests;

/// <summary>
///     Schema coverage for the plugin-scoped <c>mcp.json</c> data file (ADR 0033): the
///     conventional Claude Code/Cursor/VS Code server-block format, validated strictly by
///     the kernel-side parser. These are the cases the kernel-level file previously
///     exercised through the configuration pipeline; the plugin data file is its only
///     consumer now.
/// </summary>
public sealed class McpServerFileTests : IAsyncDisposable
{
    private static readonly string BaseDirectory =
        Path.Combine(Path.GetTempPath(), $"mcp-parser-base-{Guid.NewGuid():N}");

    public async ValueTask DisposeAsync()
    {
        await TempArea.DeleteAsync(BaseDirectory);
    }

    [Fact]
    public void ReadsStdioAndHttpServersWithTransportDefaults()
    {
        var path = WriteFile(new JsonObject
        {
            ["stdio"] = new JsonObject
            {
                ["command"] = "deno",
                ["args"] = new JsonArray("run", "--help"),
                ["env"] = new JsonObject { ["DENO_DIR"] = "cache" },
                ["workingDirectory"] = "./server"
            },
            ["remote"] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = "https://example.test/mcp",
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer t" }
            }
        });

        var definitions = McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::");

        Assert.Equal(2, definitions.Count());
        var stdio = Assert.Single(definitions, d => d.Id == "plugin:demo::stdio");
        var stdioTransport = Assert.IsType<StdioMcpTransportDefinition>(stdio.Transport);
        Assert.Equal("deno", stdioTransport.Command);
        Assert.Equal(["run", "--help"], stdioTransport.Arguments);
        Assert.True((stdioTransport.EnvironmentVariables ?? throw new InvalidOperationException("the assertion subject is null")).ContainsKey("DENO_DIR"));
        Assert.Equal(Path.GetFullPath("./server", BaseDirectory), stdioTransport.WorkingDirectory);
        Assert.True(stdio.RootsEnabled);
        Assert.True(stdio.ElicitationEnabled);

        var http = Assert.Single(definitions, d => d.Id == "plugin:demo::remote");
        var httpTransport = Assert.IsType<HttpMcpTransportDefinition>(http.Transport);
        Assert.Equal(new Uri("https://example.test/mcp"), httpTransport.Endpoint);
        var headers = httpTransport.Headers ?? throw new InvalidOperationException("the HTTP transport declares headers");
        Assert.Equal("Bearer t", headers["Authorization"]);
        Assert.False(http.RootsEnabled);
        Assert.False(http.ElicitationEnabled);
    }

    [Fact]
    public void AcceptsTheServersKeyAsAVsCodeAlias()
    {
        var path = WriteFileWithRoot(new JsonObject
        {
            ["servers"] = new JsonObject
            {
                ["one"] = new JsonObject { ["command"] = "deno" }
            }
        });

        Assert.Equal("plugin:demo::one", Assert.Single(McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::")).Id);
    }

    [Fact]
    public void ReadsCollectedJsonThroughTheSamePipeline()
    {
        // The string-form entrypoint path hands the interpreter already-collected
        // JSON (ADR 0033) — the identical schema pipeline as a file read.
        var collected = JsonSerializer.Deserialize<JsonElement>(
            """
            { "mcpServers": { "one": { "command": "deno" } } }
            """);

        Assert.Equal("plugin:demo::one", Assert.Single(McpServerFile.ReadJson(collected, BaseDirectory, "plugin:demo::")).Id);
    }

    [Fact]
    public void RejectsCombiningTheTopLevelKeys()
    {
        var path = WriteFileWithRoot(new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["one"] = StdioServer() },
            ["servers"] = new JsonObject { ["two"] = StdioServer() }
        });

        var assertion21 = Assert.ThrowsAny<InvalidOperationException>(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"));
        Assert.Matches("(?is)^.*must\\ not\\ combine.*$", assertion21.Message);
    }

    [Fact]
    public void RejectsInsecureRemoteHttp()
    {
        var path = WriteFile(new JsonObject
        {
            ["remote"] = new JsonObject { ["url"] = "http://example.test/mcp" }
        });

        var assertion22 = Assert.ThrowsAny<InvalidOperationException>(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"));
        Assert.Matches("(?is)^.*must\\ use\\ HTTPS.*$", assertion22.Message);
    }

    [Fact]
    public void RejectsTheSseTransport()
    {
        var path = WriteFile(new JsonObject
        {
            ["remote"] = new JsonObject { ["type"] = "sse", ["url"] = "https://example.test/sse" }
        });

        var assertion23 = Assert.ThrowsAny<InvalidOperationException>(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"));
        Assert.Matches("(?is)^.*unsupported\\ 'sse'.*$", assertion23.Message);
    }

    [Fact]
    public void RejectsUnknownKeys()
    {
        var server = StdioServer();
        server["Nope"] = true;
        var path = WriteFile(new JsonObject { ["stdio"] = server });

        var assertion24 = Assert.ThrowsAny<InvalidOperationException>(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"));
        Assert.Matches("(?is)^.*not\\ valid\\ for\\ MCP\\ server.*$", assertion24.Message);
    }

    [Fact]
    public void SkipsDisabledServersBeforeValidation()
    {
        var path = WriteFile(new JsonObject
        {
            ["disabled"] = new JsonObject
            {
                ["enabled"] = false,
                ["type"] = "http",
                ["url"] = "not-a-url"
            },
            ["live"] = StdioServer()
        });

        Assert.Equal("plugin:demo::live", Assert.Single(McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::")).Id);
    }

    [Fact]
    public void RejectsNonPositiveTimeouts()
    {
        var path = WriteFile(new JsonObject
        {
            ["slow"] = new JsonObject
            {
                ["command"] = "deno",
                ["requestTimeout"] = "00:00:00"
            }
        });

        var assertion25 = Assert.ThrowsAny<InvalidOperationException>(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"));
        Assert.Matches("(?is)^.*RequestTimeout\\ must\\ be\\ positive.*$", assertion25.Message);
    }

    [Fact]
    public void InvalidJsonFailsTheRead()
    {
        var path = Path.Combine(BaseDirectory, $"mcp-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(BaseDirectory);
        File.WriteAllText(path, "{");

        Assert.ThrowsAny<Exception>(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"));
    }

    private static JsonObject StdioServer()
    {
        return new JsonObject
        {
            ["command"] = "deno",
            ["args"] = new JsonArray(),
            ["env"] = new JsonObject()
        };
    }

    private static string WriteFile(JsonObject servers)
    {
        return WriteFileWithRoot(new JsonObject { ["mcpServers"] = servers });
    }

    private static string WriteFileWithRoot(JsonObject root)
    {
        Directory.CreateDirectory(BaseDirectory);
        var path = Path.Combine(BaseDirectory, $"mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }
}
