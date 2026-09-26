using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Maieutics.Mcp;

namespace Maieutics.Product.Tests;

/// <summary>
///     Schema coverage for the plugin-scoped <c>mcp.json</c> data file (ADR 0033): the
///     conventional Claude Code/Cursor/VS Code server-block format, validated strictly by
///     the kernel-side parser. These are the cases the kernel-level file previously
///     exercised through the configuration pipeline; the plugin data file is its only
///     consumer now.
/// </summary>
public sealed class McpServerFileTests : IDisposable
{
    private static readonly string BaseDirectory =
        Path.Combine(Path.GetTempPath(), $"mcp-parser-base-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(BaseDirectory)) Directory.Delete(BaseDirectory, true);
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

        definitions.Should().HaveCount(2);
        var stdio = definitions.Should().ContainSingle(d => d.Id == "plugin:demo::stdio").Subject;
        var stdioTransport = stdio.Transport.Should().BeOfType<StdioMcpTransportDefinition>().Subject;
        stdioTransport.Command.Should().Be("deno");
        stdioTransport.Arguments.Should().Equal("run", "--help");
        stdioTransport.EnvironmentVariables.Should().ContainKey("DENO_DIR");
        stdioTransport.WorkingDirectory.Should().Be(Path.GetFullPath("./server", BaseDirectory));
        stdio.RootsEnabled.Should().BeTrue();
        stdio.ElicitationEnabled.Should().BeTrue();

        var http = definitions.Should().ContainSingle(d => d.Id == "plugin:demo::remote").Subject;
        var httpTransport = http.Transport.Should().BeOfType<HttpMcpTransportDefinition>().Subject;
        httpTransport.Endpoint.Should().Be(new Uri("https://example.test/mcp"));
        httpTransport.Headers.Should().Contain("Authorization", "Bearer t");
        http.RootsEnabled.Should().BeFalse();
        http.ElicitationEnabled.Should().BeFalse();
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

        McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::")
            .Should().ContainSingle().Which.Id.Should().Be("plugin:demo::one");
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

        McpServerFile.ReadJson(collected, BaseDirectory, "plugin:demo::")
            .Should().ContainSingle().Which.Id.Should().Be("plugin:demo::one");
    }

    [Fact]
    public void RejectsCombiningTheTopLevelKeys()
    {
        var path = WriteFileWithRoot(new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["one"] = StdioServer() },
            ["servers"] = new JsonObject { ["two"] = StdioServer() }
        });

        FluentActions.Invoking(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"))
            .Should().Throw<InvalidOperationException>().WithMessage("*must not combine*");
    }

    [Fact]
    public void RejectsInsecureRemoteHttp()
    {
        var path = WriteFile(new JsonObject
        {
            ["remote"] = new JsonObject { ["url"] = "http://example.test/mcp" }
        });

        FluentActions.Invoking(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"))
            .Should().Throw<InvalidOperationException>().WithMessage("*must use HTTPS*");
    }

    [Fact]
    public void RejectsTheSseTransport()
    {
        var path = WriteFile(new JsonObject
        {
            ["remote"] = new JsonObject { ["type"] = "sse", ["url"] = "https://example.test/sse" }
        });

        FluentActions.Invoking(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"))
            .Should().Throw<InvalidOperationException>().WithMessage("*unsupported 'sse'*");
    }

    [Fact]
    public void RejectsUnknownKeys()
    {
        var server = StdioServer();
        server["Nope"] = true;
        var path = WriteFile(new JsonObject { ["stdio"] = server });

        FluentActions.Invoking(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"))
            .Should().Throw<InvalidOperationException>().WithMessage("*not valid for MCP server*");
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

        McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::")
            .Should().ContainSingle().Which.Id.Should().Be("plugin:demo::live");
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

        FluentActions.Invoking(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"))
            .Should().Throw<InvalidOperationException>().WithMessage("*RequestTimeout must be positive*");
    }

    [Fact]
    public void InvalidJsonFailsTheRead()
    {
        var path = Path.Combine(BaseDirectory, $"mcp-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(BaseDirectory);
        File.WriteAllText(path, "{");

        FluentActions.Invoking(() => McpServerFile.ReadFile(path, BaseDirectory, "plugin:demo::"))
            .Should().Throw<Exception>();
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
