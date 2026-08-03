using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;
using Maieutics.Providers.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class MaieuticsHostIntegrationTests
{
    private static string KernelSpecPath => Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "kernels",
        "maieutics",
        "kernel.json");

    [Fact(Timeout = 45_000)]
    public async Task CompositionRootStartsKernelInProcess()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(20));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-in-process-{Guid.NewGuid():N}.json");
        var configurationFile = CreateEmptyConfigurationFile("in-process");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        var builder = MaieuticsHost.CreateApplicationBuilder(
            ["--config", configurationFile, "--connection-file", connectionFile, "--model", "test-model"]);
        builder.Configuration["Maieutics:Providers:OpenAI:ApiKey"] = "test-key";
        using var host = builder.Build();
        var functionNames = host.Services.GetRequiredService<IReadOnlyList<AIFunction>>()
            .Select(static function => function.Name)
            .ToArray();
        functionNames.Should().Contain(
            "repl_execute",
            "repl_create",
            "repl_list",
            "repl_restart",
            "repl_close");
        var phase = "host start";

        try
        {
            await host.StartAsync(deadline.Token);
            phase = "client connect";
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            phase = "client ready";
            await client.WaitForReadyAsync(deadline.Token);
            phase = "kernel info";
            var info = await client.GetKernelInfoAsync(deadline.Token);
            info.Implementation.Should().Be("maieutics");
            phase = "kernel shutdown";
            await client.ShutdownAsync(false, deadline.Token);
            phase = "host shutdown";
            await host.WaitForShutdownAsync(deadline.Token);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Maieutics composition root failed during {phase}.", exception);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await host.StopAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
            }

            File.Delete(connectionFile);
            File.Delete(configurationFile);
        }
    }

    [Theory(Timeout = 45_000)]
    [InlineData(OpenAiApiFlavor.Responses)]
    [InlineData(OpenAiApiFlavor.ChatCompletions)]
    public async Task InProcessHostCompletesToolLoopWithoutPublishingToolLifecycle(OpenAiApiFlavor apiFlavor)
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(30));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-tool-{Guid.NewGuid():N}.json");
        var configurationFile = CreateEmptyConfigurationFile("tool");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await using var provider = new FakeOpenAiServer(apiFlavor, toolFlow: true);
        var builder = MaieuticsHost.CreateApplicationBuilder(
            ["--config", configurationFile, "--connection-file", connectionFile, "--model", "test-model"]);
        builder.Configuration["Maieutics:Providers:OpenAI:ApiKey"] = "test-key";
        builder.Configuration["Maieutics:Providers:OpenAI:Endpoint"] = provider.Endpoint.ToString();
        builder.Configuration["Maieutics:Providers:OpenAI:ApiFlavor"] = apiFlavor.ToString();
        builder.Services.RemoveAll<IReadOnlyList<AIFunction>>();
        builder.Services.AddSingleton<IReadOnlyList<AIFunction>>([CreateEchoFunction()]);
        using var host = builder.Build();

        try
        {
            await host.StartAsync(deadline.Token);
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            await client.WaitForReadyAsync(deadline.Token);

            var execution = await client.ExecuteAsync(new JupyterExecuteRequest("use echo"), deadline.Token);
            var outputs = new List<JupyterOutput>();
            await foreach (var output in execution.Outputs.WithCancellation(deadline.Token))
            {
                outputs.Add(output);
            }

            (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
            outputs.OfType<JupyterDisplayOutput>().Should().ContainSingle()
                .Which.Data.Data["text/markdown"].GetString().Should().Be("tool-backed answer");
            outputs.Where(output =>
                    output is not JupyterExecuteInputOutput and
                        not JupyterDisplayOutput and
                        not JupyterDisplayUpdateOutput and
                        not JupyterExecutionStatusChanged)
                .Should().BeEmpty();
            await provider.Completion.WaitAsync(deadline.Token);

            await client.ShutdownAsync(false, deadline.Token);
            await host.WaitForShutdownAsync(deadline.Token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await host.StopAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
            }

            File.Delete(connectionFile);
            File.Delete(configurationFile);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task InProcessHostRoutesDenoReplOutputsByJupyterMessageType()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(45));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-repl-{Guid.NewGuid():N}.json");
        var configurationFile = CreateEmptyConfigurationFile("repl");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        const string code =
            "const name = prompt('Name: '); " +
            "console.log('private-name=' + name); " +
            "console.log('private-stdout'); " +
            "console.error('shared-stderr'); " +
            "console.log('provider-secret=' + String(Deno.env.get('OPENAI_API_KEY'))); " +
            "const displayId = 'host-display'; " +
            "await Deno.jupyter.display(" +
            "{ 'text/html': '<b>visible-display</b>', 'text/plain': 'visible-display' }, " +
            "{ raw: true, display_id: displayId }); " +
            "await Deno.jupyter.display(" +
            "{ 'text/html': '<b>visible-update</b>', 'text/plain': 'visible-update' }, " +
            "{ raw: true, display_id: displayId, update: true }); " +
            "await Deno.jupyter.display({ 'text/plain': 'invalid-update' }, { raw: true, update: true }); " +
            "40 + 2";
        await using var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.Responses,
            toolFlow: true,
            toolName: "repl_execute",
            toolArgumentsJson: JsonSerializer.Serialize(new { code }));
        var builder = MaieuticsHost.CreateApplicationBuilder(
            ["--config", configurationFile, "--connection-file", connectionFile, "--model", "test-model"]);
        builder.Configuration["Maieutics:Providers:OpenAI:ApiKey"] = "test-key";
        builder.Configuration["Maieutics:Providers:OpenAI:Endpoint"] = provider.Endpoint.ToString();
        using var host = builder.Build();

        try
        {
            await host.StartAsync(deadline.Token);
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            await client.WaitForReadyAsync(deadline.Token);

            var execution = await client.ExecuteAsync(
                new JupyterExecuteRequest("use the Deno REPL", AllowStdin: true),
                deadline.Token);
            var outputs = new List<JupyterOutput>();
            await foreach (var output in execution.Outputs.WithCancellation(deadline.Token))
            {
                outputs.Add(output);
                if (output is JupyterInputRequest input)
                {
                    await execution.ReplyInputAsync(input, "Ada", deadline.Token);
                }
            }

            (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
            outputs.OfType<JupyterStdout>().Should().BeEmpty();
            outputs.OfType<JupyterExecuteResultOutput>().Should().BeEmpty();
            outputs.OfType<JupyterInputRequest>().Should().ContainSingle().Which.Prompt.Should().Be("Name: ");
            outputs.OfType<JupyterStderr>().Should().Contain(output =>
                output.Text.Contains("shared-stderr", StringComparison.Ordinal));
            var replDisplay = outputs.OfType<JupyterDisplayOutput>().Single(output =>
                output.Data.Data.TryGetValue("text/plain", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() == "visible-display");
            replDisplay.Data.Data["text/html"].GetString().Should().Be("<b>visible-display</b>");
            var replUpdate = outputs.OfType<JupyterDisplayUpdateOutput>().Single(output =>
                output.Data.Data.TryGetValue("text/plain", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() == "visible-update");
            replUpdate.Data.Data["text/html"].GetString().Should().Be("<b>visible-update</b>");
            replUpdate.DisplayId.Should().Be(replDisplay.DisplayId);
            outputs.OfType<JupyterDisplayOutput>().Any(output =>
                    output.Data.Data.TryGetValue("text/markdown", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString() == "tool-backed answer")
                .Should().BeTrue();

            await provider.Completion.WaitAsync(deadline.Token);
            var toolOutput = provider.RequestBodies.Last()
                .GetProperty("input")
                .EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "function_call_output")
                .GetProperty("output")
                .GetString();
            using var toolResult = JsonDocument.Parse(toolOutput!);
            var toolValue = toolResult.RootElement.GetProperty("value");
            var modelOutputs = toolValue.GetProperty("outputs");
            modelOutputs.EnumerateArray().Select(item => item.GetProperty("kind").GetString()).Should()
                .Contain("stdout").And.Contain("stderr");
            toolValue.GetProperty("executionStatus").GetString().Should().Be("ok");
            var presentation = toolValue.GetProperty("presentation");
            presentation.GetProperty("displayCount").GetInt32().Should().Be(1);
            presentation.GetProperty("updateCount").GetInt32().Should().Be(1);
            presentation.GetProperty("skippedCount").GetInt32().Should().Be(1);
            toolOutput.Should().Contain("private-name=Ada")
                .And.Contain("shared-stderr")
                .And.Contain("provider-secret=undefined")
                .And.NotContain("visible-display")
                .And.NotContain("visible-update")
                .And.NotContain("invalid-update");

            await client.ShutdownAsync(false, deadline.Token);
            await host.WaitForShutdownAsync(deadline.Token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await host.StopAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
            }

            File.Delete(connectionFile);
            File.Delete(configurationFile);
        }
    }

    [Fact(Timeout = 45_000)]
    public async Task ChatCompletionsReasoningIsPrivateAcrossTheProviderAndJupyterBoundaries()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(30));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-reasoning-{Guid.NewGuid():N}.json");
        var configurationFile = Path.Combine(
            Path.GetTempPath(),
            $"maieutics-reasoning-config-{Guid.NewGuid():N}.json");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await File.WriteAllTextAsync(configurationFile, "{}", deadline.Token);
        await using var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            answer: "public answer",
            reasoning: "private reasoning");
        var builder = MaieuticsHost.CreateApplicationBuilder(
            ["--config", configurationFile, "--connection-file", connectionFile, "--model", "test-model"]);
        builder.Configuration["Maieutics:Providers:OpenAI:ApiKey"] = "test-key";
        builder.Configuration["Maieutics:Providers:OpenAI:Endpoint"] = provider.Endpoint.ToString();
        builder.Configuration["Maieutics:Providers:OpenAI:ApiFlavor"] =
            nameof(OpenAiApiFlavor.ChatCompletions);
        using var host = builder.Build();
        var hostStarted = false;

        try
        {
            await host.StartAsync(deadline.Token);
            hostStarted = true;
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            await client.WaitForReadyAsync(deadline.Token);

            var execution = await client.ExecuteAsync(new JupyterExecuteRequest("think"), deadline.Token);
            var outputs = new List<JupyterOutput>();
            await foreach (var output in execution.Outputs.WithCancellation(deadline.Token))
            {
                outputs.Add(output);
            }

            (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
            outputs.OfType<JupyterDisplayOutput>().Should().ContainSingle()
                .Which.Data.Data["text/markdown"].GetString().Should().Be("public answer");
            outputs.Select(static output => output.ToString()).Should()
                .NotContain(text => text.Contains("private reasoning", StringComparison.Ordinal));

            var transcript = host.Services.GetRequiredService<IAgentSession>().GetTranscriptSnapshot();
            transcript.Turns.Should().ContainSingle();
            transcript.Turns.SelectMany(static turn => turn.Messages)
                .SelectMany(static message => message.Contents)
                .OfType<TextReasoningContent>()
                .Should().BeEmpty();
            transcript.Turns[0].Messages[^1].Text.Should().Be("public answer");
            await provider.Completion.WaitAsync(deadline.Token);

            await client.ShutdownAsync(false, deadline.Token);
            await host.WaitForShutdownAsync(deadline.Token);
        }
        finally
        {
            if (hostStarted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await host.StopAsync(cleanup.Token);
                }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                }
            }

            File.Delete(connectionFile);
            File.Delete(configurationFile);
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task OpenAiChatCompletionsMapsReasoningContent()
    {
        await using var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.ChatCompletions,
            answer: "public answer",
            reasoning: "private reasoning");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Source:Provider"] = "OpenAI",
                ["Source:ApiFlavor"] = nameof(OpenAiApiFlavor.ChatCompletions),
                ["Source:ApiKey"] = "test-key",
                ["Source:Endpoint"] = provider.Endpoint.ToString()
            })
            .Build();
        var source = new OpenAiChatClientFactory().BindSource("openai", configuration.GetSection("Source"));
        using var client = source.Create("test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           "think",
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        updates.SelectMany(static update => update.Contents)
            .OfType<TextReasoningContent>()
            .Select(static content => content.Text)
            .Should().Contain("private reasoning");
        string.Concat(updates.Select(static update => update.Text)).Should().Be("public answer");
        await provider.Completion.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 45_000)]
    public async Task InProcessHostCompletesAnthropicToolLoopWithoutPublishingToolLifecycle()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(30));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-anthropic-tool-{Guid.NewGuid():N}.json");
        var configurationFile = CreateEmptyConfigurationFile("anthropic-tool");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await using var provider = new FakeAnthropicServer(
            "claude-test",
            "tool-backed answer",
            toolFlow: true);
        var builder = MaieuticsHost.CreateApplicationBuilder(
            ["--config", configurationFile, "--connection-file", connectionFile]);
        builder.Configuration["Maieutics:DefaultProfile"] = "claude";
        builder.Configuration["Maieutics:Sources:anthropic:Provider"] = "Anthropic";
        builder.Configuration["Maieutics:Sources:anthropic:ApiKey"] = "anthropic-key";
        builder.Configuration["Maieutics:Sources:anthropic:Endpoint"] = provider.Endpoint.ToString();
        builder.Configuration["Maieutics:Profiles:claude:Source"] = "anthropic";
        builder.Configuration["Maieutics:Profiles:claude:Model"] = "claude-test";
        builder.Services.RemoveAll<IReadOnlyList<AIFunction>>();
        builder.Services.AddSingleton<IReadOnlyList<AIFunction>>([CreateEchoFunction()]);
        using var host = builder.Build();

        try
        {
            await host.StartAsync(deadline.Token);
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            await client.WaitForReadyAsync(deadline.Token);

            var execution = await client.ExecuteAsync(new JupyterExecuteRequest("use echo"), deadline.Token);
            var outputs = new List<JupyterOutput>();
            await foreach (var output in execution.Outputs.WithCancellation(deadline.Token))
            {
                outputs.Add(output);
            }

            (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
            outputs.OfType<JupyterDisplayOutput>().Should().ContainSingle()
                .Which.Data.Data["text/markdown"].GetString().Should().Be("tool-backed answer");
            outputs.Where(output =>
                    output is not JupyterExecuteInputOutput and
                        not JupyterDisplayOutput and
                        not JupyterDisplayUpdateOutput and
                        not JupyterExecutionStatusChanged)
                .Should().BeEmpty();
            await provider.Completion.WaitAsync(deadline.Token);
            provider.RequestBodies.Should().HaveCount(2);
            provider.RequestBodies.Last().GetRawText().Should()
                .Contain("tool_result").And.Contain("status").And.Contain("ok");

            await client.ShutdownAsync(false, deadline.Token);
            await host.WaitForShutdownAsync(deadline.Token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await host.StopAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
            }

            File.Delete(connectionFile);
            File.Delete(configurationFile);
        }
    }

    [Theory(Timeout = 45_000)]
    [InlineData(OpenAiApiFlavor.Responses)]
    [InlineData(OpenAiApiFlavor.ChatCompletions)]
    public async Task GenericHostStartsRealKernelAndStopsAfterShutdown(OpenAiApiFlavor apiFlavor)
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(35));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-host-{Guid.NewGuid():N}.json");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await using var provider = new FakeOpenAiServer(apiFlavor);
        using var started = StartHostProcess(connectionFile, provider.Endpoint, apiFlavor);
        var process = started.Process;

        try
        {
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            var ready = client.WaitForReadyAsync(deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            var startup = await Task.WhenAny(ready, exited);
            startup.Should().BeSameAs(ready, started.FailureDetails());
            try
            {
                await ready;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(started.FailureDetails(), exception);
            }

            (await client.PingAsync(deadline.Token)).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
            var info = await client.GetKernelInfoAsync(deadline.Token);
            info.Implementation.Should().Be("maieutics");
            info.ProtocolVersion.Should().Be("5.5");

            (await ExecuteAndGetMarkdownAsync(
                    client,
                    "%maieutics workspace current",
                    deadline.Token))
                .Should().Contain("Current workspace").And.Contain("startup root");

            var execution = await client.ExecuteAsync(new JupyterExecuteRequest("hello"), deadline.Token);
            var outputs = new List<JupyterOutput>();
            await foreach (var output in execution.Outputs.WithCancellation(deadline.Token))
            {
                outputs.Add(output);
            }

            (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
            outputs.OfType<JupyterDisplayOutput>().Single().Data.Data["text/markdown"].GetString().Should()
                .Be("native answer");
            await provider.Completion.WaitAsync(deadline.Token);

            var shutdown = await client.ShutdownAsync(false, deadline.Token);
            shutdown.Status.Should().Be("ok");
            await process.WaitForExitAsync(deadline.Token);
            process.ExitCode.Should().Be(0, started.FailureDetails());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            File.Delete(connectionFile);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(OpenAiApiFlavor.Responses)]
    [InlineData(OpenAiApiFlavor.ChatCompletions)]
    public async Task ExternalHostCompletesWorkspaceFunctionLoop(OpenAiApiFlavor apiFlavor)
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(45));
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-native-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(root, "connection.json");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await File.WriteAllTextAsync(
            Path.Combine(root, "note.txt"),
            "native workspace body",
            deadline.Token);
        await using var provider = new FakeOpenAiServer(
            apiFlavor,
            toolFlow: true,
            toolName: "read_text",
            toolArgumentsJson: "{\"uri\":\"workspace://local/note.txt\"}",
            expectedToolResultText: "native workspace body");
        using var started = StartHostProcess(connectionFile, provider.Endpoint, apiFlavor, root);
        var process = started.Process;

        try
        {
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            var ready = client.WaitForReadyAsync(deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            (await Task.WhenAny(ready, exited)).Should().BeSameAs(ready, started.FailureDetails());
            await ready;

            (await ExecuteAndGetMarkdownAsync(client, "read the note", deadline.Token)).Should()
                .Be("tool-backed answer");
            await provider.Completion.WaitAsync(deadline.Token);
            provider.RequestBodies.Should().HaveCount(2);
            provider.RequestBodies.Last().GetRawText().Should()
                .Contain("status").And.Contain("ok").And.Contain("native workspace body");

            await client.ShutdownAsync(false, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            process.ExitCode.Should().Be(0, started.FailureDetails());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ExternalHostCompletesMcpFunctionLoopWhenTestServerIsConfigured()
    {
        var mcpServer = Environment.GetEnvironmentVariable("MAIEUTICS_TEST_MCP_SERVER_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(mcpServer))
        {
            return;
        }

        using var deadline = CreateDeadline(TimeSpan.FromSeconds(45));
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-native-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(root, "connection.json");
        var configurationFile = Path.Combine(root, "maieutics.json");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await using var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.Responses,
            toolFlow: true,
            toolName: "mcp_echo",
            toolArgumentsJson: "{\"value\":\"native mcp value\"}",
            expectedToolResultText: "native mcp value");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateMcpHostConfiguration(connectionFile, provider.Endpoint, Path.GetFullPath(mcpServer)),
            deadline.Token);
        using var started = StartConfiguredHostProcess(configurationFile);
        var process = started.Process;

        try
        {
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            var ready = client.WaitForReadyAsync(deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            (await Task.WhenAny(ready, exited)).Should().BeSameAs(ready, started.FailureDetails());
            await ready;

            (await ExecuteAndGetMarkdownAsync(client, "%mcp list", deadline.Token)).Should()
                .Contain("`echo` → `mcp_echo`").And.Contain("Connected");
            (await ExecuteAndGetMarkdownAsync(client, "call the MCP echo tool", deadline.Token)).Should()
                .Be("tool-backed answer");
            await provider.Completion.WaitAsync(deadline.Token);
            provider.RequestBodies.Last().GetRawText().Should()
                .Contain("status").And.Contain("ok").And.Contain("native mcp value");

            await client.ShutdownAsync(false, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            process.ExitCode.Should().Be(0, started.FailureDetails());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(OpenAiApiFlavor.Responses)]
    [InlineData(OpenAiApiFlavor.ChatCompletions)]
    public async Task ReloadedProviderConfigurationIsUsedByTheNextNotebookCell(OpenAiApiFlavor apiFlavor)
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(45));
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-hot-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(root, "connection.json");
        var configurationFile = Path.Combine(root, "maieutics.json");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await using var firstProvider = new FakeOpenAiServer(
            apiFlavor,
            model: "first-model",
            answer: "first answer");
        await using var secondProvider = new FakeOpenAiServer(
            apiFlavor,
            model: "second-model",
            answer: "second answer");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateHostConfiguration(connectionFile, firstProvider.Endpoint, apiFlavor, "first-model", "first prompt"),
            deadline.Token);
        using var started = StartConfiguredHostProcess(configurationFile);
        var process = started.Process;

        try
        {
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            var ready = client.WaitForReadyAsync(deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            var startup = await Task.WhenAny(ready, exited);
            startup.Should().BeSameAs(ready, started.FailureDetails());
            await ready;

            (await client.PingAsync(deadline.Token)).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);

            (await ExecuteAndGetMarkdownAsync(client, "first cell", deadline.Token)).Should().Be("first answer");
            await firstProvider.Completion.WaitAsync(deadline.Token);

            await File.WriteAllTextAsync(
                configurationFile,
                CreateHostConfiguration(
                    connectionFile,
                    secondProvider.Endpoint,
                    apiFlavor,
                    "second-model",
                    "second prompt"),
                deadline.Token);
            await started.WaitForOutputAsync(
                "Applied Maieutics configuration version 2.",
                deadline.Token);

            (await ExecuteAndGetMarkdownAsync(client, "second cell", deadline.Token)).Should().Be("second answer");
            await secondProvider.Completion.WaitAsync(deadline.Token);
            secondProvider.RequestBodies.Should().ContainSingle();
            secondProvider.RequestBodies.Single().GetRawText().Should()
                .Contain("first cell").And.Contain("first answer").And.Contain("second cell");

            await client.ShutdownAsync(false, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            process.ExitCode.Should().Be(0, started.FailureDetails());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(OpenAiApiFlavor.Responses)]
    [InlineData(OpenAiApiFlavor.ChatCompletions)]
    public async Task NotebookSwitchesBetweenOpenAiAndAnthropicWithCanonicalHistory(OpenAiApiFlavor apiFlavor)
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(45));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-switch-{Guid.NewGuid():N}.json");
        var configurationFile = CreateEmptyConfigurationFile("switch");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await using var openAi = new FakeOpenAiServer(
            apiFlavor,
            model: "gpt-test",
            answer: "openai answer",
            requestCount: 2);
        await using var anthropic = new FakeAnthropicServer("claude-test", "anthropic answer");
        var builder = MaieuticsHost.CreateApplicationBuilder(
            ["--config", configurationFile, "--connection-file", connectionFile]);
        builder.Configuration["Maieutics:DefaultProfile"] = "gpt";
        builder.Configuration["Maieutics:Sources:openai:Provider"] = "OpenAI";
        builder.Configuration["Maieutics:Sources:openai:ApiFlavor"] = apiFlavor.ToString();
        builder.Configuration["Maieutics:Sources:openai:ApiKey"] = "openai-key";
        builder.Configuration["Maieutics:Sources:openai:Endpoint"] = openAi.Endpoint.ToString();
        builder.Configuration["Maieutics:Sources:anthropic:Provider"] = "Anthropic";
        builder.Configuration["Maieutics:Sources:anthropic:ApiKey"] = "anthropic-key";
        builder.Configuration["Maieutics:Sources:anthropic:Endpoint"] = anthropic.Endpoint.ToString();
        builder.Configuration["Maieutics:Profiles:gpt:Source"] = "openai";
        builder.Configuration["Maieutics:Profiles:gpt:Model"] = "gpt-test";
        builder.Configuration["Maieutics:Profiles:claude:Source"] = "anthropic";
        builder.Configuration["Maieutics:Profiles:claude:Model"] = "claude-test";
        using var host = builder.Build();

        try
        {
            await host.StartAsync(deadline.Token);
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            await client.WaitForReadyAsync(deadline.Token);

            (await ExecuteAndGetMarkdownAsync(client, "first cell", deadline.Token)).Should().Be("openai answer");
            (await ExecuteAndGetMarkdownAsync(client, "%maieutics model use claude", deadline.Token)).Should()
                .Contain("Profile: `claude`");
            (await ExecuteAndGetMarkdownAsync(client, "second cell", deadline.Token)).Should().Be("anthropic answer");
            (await ExecuteAndGetMarkdownAsync(client, "%maieutics model use gpt", deadline.Token)).Should()
                .Contain("Profile: `gpt`");
            (await ExecuteAndGetMarkdownAsync(client, "third cell", deadline.Token)).Should().Be("openai answer");

            await openAi.Completion.WaitAsync(deadline.Token);
            await anthropic.Completion.WaitAsync(deadline.Token);
            anthropic.RequestBody.GetRawText().Should()
                .Contain("first cell").And.Contain("openai answer").And.Contain("second cell");
            openAi.RequestBodies.Should().HaveCount(2);
            openAi.RequestBodies.Last().GetRawText().Should()
                .Contain("first cell").And.Contain("openai answer")
                .And.Contain("second cell").And.Contain("anthropic answer")
                .And.Contain("third cell");

            await client.ShutdownAsync(false, deadline.Token);
            await host.WaitForShutdownAsync(deadline.Token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await host.StopAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
            }

            File.Delete(connectionFile);
            File.Delete(configurationFile);
        }
    }

    [Theory(Timeout = 75_000)]
    [InlineData(OpenAiApiFlavor.Responses)]
    [InlineData(OpenAiApiFlavor.ChatCompletions)]
    public async Task ExternalHostSwitchesBetweenOpenAiAndAnthropic(OpenAiApiFlavor apiFlavor)
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(60));
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-process-switch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(root, "connection.json");
        var configurationFile = Path.Combine(root, "maieutics.json");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await using var openAi = new FakeOpenAiServer(
            apiFlavor,
            model: "gpt-test",
            answer: "openai process answer",
            requestCount: 2);
        await using var anthropic = new FakeAnthropicServer("claude-test", "anthropic process answer");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateMultiProviderHostConfiguration(
                connectionFile,
                openAi.Endpoint,
                apiFlavor,
                anthropic.Endpoint),
            deadline.Token);
        using var started = StartConfiguredHostProcess(configurationFile);
        var process = started.Process;

        var phase = "client readiness";
        try
        {
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            var ready = client.WaitForReadyAsync(deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            (await Task.WhenAny(ready, exited)).Should().BeSameAs(ready, started.FailureDetails());
            await ready;

            phase = "heartbeat";
            (await client.PingAsync(deadline.Token)).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
            phase = "first OpenAI execution";
            (await ExecuteAndGetMarkdownAsync(client, "first process cell", deadline.Token)).Should()
                .Be("openai process answer");
            phase = "select Anthropic";
            await ExecuteAndGetMarkdownAsync(client, "%maieutics model use claude", deadline.Token);
            phase = "Anthropic execution";
            (await ExecuteAndGetMarkdownAsync(client, "second process cell", deadline.Token)).Should()
                .Be("anthropic process answer");
            phase = "select OpenAI";
            await ExecuteAndGetMarkdownAsync(client, "%maieutics model use gpt", deadline.Token);
            phase = "second OpenAI execution";
            (await ExecuteAndGetMarkdownAsync(client, "third process cell", deadline.Token)).Should()
                .Be("openai process answer");

            await openAi.Completion.WaitAsync(deadline.Token);
            await anthropic.Completion.WaitAsync(deadline.Token);
            openAi.RequestBodies.Last().GetRawText().Should().Contain("anthropic process answer");

            await client.ShutdownAsync(false, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            process.ExitCode.Should().Be(0, started.FailureDetails());
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"External multi-provider host failed during {phase}.\n{started.FailureDetails()}",
                exception);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedKernelSpecUsesPortableExecutableCommand()
    {
        var spec = await JupyterKernelSpec.ReadAsync(KernelSpecPath, TestContext.Current.CancellationToken);

        spec.Argv.Should().Equal("maieutics", "--connection-file", "{connection_file}");
        spec.DisplayName.Should().Be("Maieutics Agent");
        spec.Language.Should().Be("markdown");
        spec.InterruptMode.Should().Be("message");
    }

    private static StartedHostProcess StartHostProcess(
        string connectionFile,
        Uri? endpoint = null,
        OpenAiApiFlavor apiFlavor = OpenAiApiFlavor.Responses,
        string? workspaceRoot = null)
    {
        var nativeExecutable = Environment.GetEnvironmentVariable("MAIEUTICS_TEST_HOST_EXECUTABLE");
        var executablePath = string.IsNullOrWhiteSpace(nativeExecutable)
            ? null
            : Path.GetFullPath(nativeExecutable);
        if (executablePath is not null && !File.Exists(executablePath))
        {
            throw new FileNotFoundException("The configured Maieutics test host executable does not exist.",
                executablePath);
        }

        var assemblyPath = executablePath is null ? GetManagedHostAssemblyPath() : null;
        var configurationFile = CreateEmptyConfigurationFile("process");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath ?? "dotnet",
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (assemblyPath is not null)
        {
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configurationFile);
        startInfo.ArgumentList.Add("--connection-file");
        startInfo.ArgumentList.Add(connectionFile);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("test-model");
        if (workspaceRoot is not null)
        {
            startInfo.ArgumentList.Add("--workspace");
            startInfo.ArgumentList.Add(workspaceRoot);
        }

        if (apiFlavor is OpenAiApiFlavor.ChatCompletions)
        {
            startInfo.ArgumentList.Add("--openai-api");
            startInfo.ArgumentList.Add(nameof(OpenAiApiFlavor.ChatCompletions));
        }

        startInfo.Environment["OPENAI_API_KEY"] = "test-key";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment.Remove("MAIEUTICS_CONFIG");
        startInfo.Environment.Remove("MAIEUTICS_PROVIDER");
        startInfo.Environment.Remove("MAIEUTICS_OPENAI_API");
        startInfo.Environment.Remove("MAIEUTICS_WORKSPACE");
        startInfo.Environment.Remove("Maieutics__Model__Provider");
        startInfo.Environment.Remove("Maieutics__Providers__OpenAI__ApiFlavor");
        startInfo.Environment.Remove("Maieutics__Workspace__Root");
        if (endpoint is not null)
        {
            startInfo.Environment["OPENAI_BASE_URL"] = endpoint.ToString();
        }

        return StartProcess(startInfo, configurationFile);
    }

    private static StartedHostProcess StartConfiguredHostProcess(string configurationFile)
    {
        var nativeExecutable = Environment.GetEnvironmentVariable("MAIEUTICS_TEST_HOST_EXECUTABLE");
        var executablePath = string.IsNullOrWhiteSpace(nativeExecutable)
            ? null
            : Path.GetFullPath(nativeExecutable);
        if (executablePath is not null && !File.Exists(executablePath))
        {
            throw new FileNotFoundException("The configured Maieutics test host executable does not exist.",
                executablePath);
        }

        var assemblyPath = executablePath is null ? GetManagedHostAssemblyPath() : null;
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath ?? "dotnet",
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (assemblyPath is not null)
        {
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configurationFile);
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment.Remove("MAIEUTICS_CONFIG");
        startInfo.Environment.Remove("MAIEUTICS_PROVIDER");
        startInfo.Environment.Remove("MAIEUTICS_MODEL");
        startInfo.Environment.Remove("MAIEUTICS_OPENAI_API");
        startInfo.Environment.Remove("MAIEUTICS_WORKSPACE");
        startInfo.Environment.Remove("OPENAI_API_KEY");
        startInfo.Environment.Remove("OPENAI_BASE_URL");
        startInfo.Environment.Remove("Maieutics__Model__Provider");
        startInfo.Environment.Remove("Maieutics__Model__Name");
        startInfo.Environment.Remove("Maieutics__Providers__OpenAI__ApiFlavor");
        startInfo.Environment.Remove("Maieutics__Providers__OpenAI__ApiKey");
        startInfo.Environment.Remove("Maieutics__Providers__OpenAI__Endpoint");
        startInfo.Environment.Remove("Maieutics__Workspace__Root");
        return StartProcess(startInfo);
    }

    private static StartedHostProcess StartProcess(
        ProcessStartInfo startInfo,
        string? temporaryConfigurationFile = null)
    {
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Could not start the Maieutics host process.");
        var standardOutput = new ConcurrentQueue<string>();
        var standardError = new ConcurrentQueue<string>();
        var outputChanged = new SemaphoreSlim(0);
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                standardOutput.Enqueue(eventArgs.Data);
                outputChanged.Release();
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                standardError.Enqueue(eventArgs.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new StartedHostProcess(
            process,
            standardOutput,
            standardError,
            outputChanged,
            temporaryConfigurationFile);
    }

    private static string CreateEmptyConfigurationFile(string scenario)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"maieutics-{scenario}-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        return path;
    }

    private static async Task<string> ExecuteAndGetMarkdownAsync(
        IJupyterClient client,
        string code,
        CancellationToken cancellationToken)
    {
        var execution = await client.ExecuteAsync(new JupyterExecuteRequest(code), cancellationToken);
        var outputs = new List<JupyterOutput>();
        await foreach (var output in execution.Outputs.WithCancellation(cancellationToken))
        {
            outputs.Add(output);
        }

        (await execution.Completion.WaitAsync(cancellationToken)).Reply.Status.Should().Be("ok");
        return outputs.OfType<JupyterDisplayOutput>().Single().Data.Data["text/markdown"].GetString()
               ?? throw new InvalidOperationException("The Agent display did not contain Markdown text.");
    }

    private static string CreateHostConfiguration(
        string connectionFile,
        Uri endpoint,
        OpenAiApiFlavor apiFlavor,
        string model,
        string systemPrompt)
    {
        var root = new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["SystemPrompt"] = systemPrompt,
                ["Model"] = new JsonObject
                {
                    ["Provider"] = "OpenAI",
                    ["Name"] = model
                },
                ["Providers"] = new JsonObject
                {
                    ["OpenAI"] = new JsonObject
                    {
                        ["ApiFlavor"] = apiFlavor.ToString(),
                        ["ApiKey"] = "test-key",
                        ["Endpoint"] = endpoint.ToString()
                    }
                },
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile
                }
            }
        };
        return root.ToJsonString();
    }

    private static string CreateMcpHostConfiguration(string connectionFile, Uri endpoint, string mcpServer) =>
        new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["Model"] = new JsonObject
                {
                    ["Provider"] = "OpenAI",
                    ["Name"] = "test-model"
                },
                ["Providers"] = new JsonObject
                {
                    ["OpenAI"] = new JsonObject
                    {
                        ["ApiFlavor"] = OpenAiApiFlavor.Responses.ToString(),
                        ["ApiKey"] = "test-key",
                        ["Endpoint"] = endpoint.ToString()
                    }
                },
                ["Mcp"] = new JsonObject
                {
                    ["Servers"] = new JsonObject
                    {
                        ["test"] = new JsonObject
                        {
                            ["Enabled"] = true,
                            ["Transport"] = "Stdio",
                            ["Command"] = mcpServer,
                            ["Arguments"] = new JsonArray(),
                            ["EnvironmentVariables"] = new JsonObject(),
                            ["Tools"] = new JsonObject { ["echo"] = "mcp_echo" }
                        }
                    }
                },
                ["Jupyter"] = new JsonObject { ["ConnectionFile"] = connectionFile }
            }
        }.ToJsonString();

    private static string CreateMultiProviderHostConfiguration(
        string connectionFile,
        Uri openAiEndpoint,
        OpenAiApiFlavor apiFlavor,
        Uri anthropicEndpoint) =>
        new JsonObject
        {
            ["Maieutics"] = new JsonObject
            {
                ["DefaultProfile"] = "gpt",
                ["Sources"] = new JsonObject
                {
                    ["openai"] = new JsonObject
                    {
                        ["Provider"] = "OpenAI",
                        ["ApiFlavor"] = apiFlavor.ToString(),
                        ["ApiKey"] = "openai-key",
                        ["Endpoint"] = openAiEndpoint.ToString()
                    },
                    ["anthropic"] = new JsonObject
                    {
                        ["Provider"] = "Anthropic",
                        ["ApiKey"] = "anthropic-key",
                        ["Endpoint"] = anthropicEndpoint.ToString()
                    }
                },
                ["Profiles"] = new JsonObject
                {
                    ["gpt"] = new JsonObject
                    {
                        ["Source"] = "openai",
                        ["Model"] = "gpt-test"
                    },
                    ["claude"] = new JsonObject
                    {
                        ["Source"] = "anthropic",
                        ["Model"] = "claude-test"
                    }
                },
                ["Jupyter"] = new JsonObject
                {
                    ["ConnectionFile"] = connectionFile
                }
            }
        }.ToJsonString();

    private static string GetManagedHostAssemblyPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Maieutics",
            "bin",
            configuration,
            "net10.0",
            "maieutics.dll"));
    }

    private sealed class StartedHostProcess(
        Process process,
        ConcurrentQueue<string> standardOutput,
        ConcurrentQueue<string> standardError,
        SemaphoreSlim outputChanged,
        string? temporaryConfigurationFile) : IDisposable
    {
        public Process Process { get; } = process;

        public string FailureDetails()
        {
            var exit = Process.HasExited ? $" Exit code: {Process.ExitCode}." : string.Empty;
            return $"Maieutics host failed.{exit}\nstdout:\n{string.Join('\n', standardOutput)}" +
                   $"\nstderr:\n{string.Join('\n', standardError)}";
        }

        public async Task WaitForOutputAsync(string text, CancellationToken cancellationToken)
        {
            while (!standardOutput.Any(line => line.Contains(text, StringComparison.Ordinal)))
            {
                await outputChanged.WaitAsync(cancellationToken);
            }
        }

        public void Dispose()
        {
            outputChanged.Dispose();
            Process.Dispose();
            if (temporaryConfigurationFile is not null)
            {
                File.Delete(temporaryConfigurationFile);
            }
        }
    }

    private sealed class FakeOpenAiServer : IAsyncDisposable
    {
        private readonly OpenAiApiFlavor apiFlavor;
        private readonly string model;
        private readonly string answer;
        private readonly string? reasoning;
        private readonly string toolArgumentsJson;
        private readonly string toolName;
        private readonly string? expectedToolResultText;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();

        private readonly bool toolFlow;
        private readonly int requestCount;

        public FakeOpenAiServer(
            OpenAiApiFlavor apiFlavor,
            bool toolFlow = false,
            string model = "test-model",
            string answer = "native answer",
            string? reasoning = null,
            int? requestCount = null,
            string toolName = "echo",
            string toolArgumentsJson = "{\"text\":\"hello\"}",
            string? expectedToolResultText = null)
        {
            this.apiFlavor = apiFlavor;
            this.toolFlow = toolFlow;
            this.model = model;
            this.answer = answer;
            this.reasoning = reasoning;
            this.toolName = toolName;
            this.toolArgumentsJson = toolArgumentsJson;
            this.expectedToolResultText = expectedToolResultText;
            this.requestCount = requestCount ?? (toolFlow ? 2 : 1);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
            Completion = ServeAsync(cancellation.Token);
        }

        public Uri Endpoint { get; }

        public Task Completion { get; }

        public ConcurrentQueue<JsonElement> RequestBodies { get; } = new();

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            listener.Stop();
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (SocketException) when (cancellation.IsCancellationRequested)
            {
            }

            cancellation.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            for (var requestIndex = 0; requestIndex < requestCount; requestIndex++)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                RequestBodies.Enqueue(request.Body.Clone());
                AssertRequest(request, requestIndex);

                var data = (apiFlavor, toolFlow, requestIndex) switch
                {
                    (OpenAiApiFlavor.Responses, true, 0) => CreateResponsesToolStream(
                        toolName,
                        toolArgumentsJson),
                    (OpenAiApiFlavor.ChatCompletions, true, 0) => CreateChatCompletionsToolStream(
                        toolName,
                        toolArgumentsJson),
                    (OpenAiApiFlavor.Responses, _, _) => CreateResponsesStream(
                        toolFlow ? "tool-backed answer" : answer),
                    (OpenAiApiFlavor.ChatCompletions, _, _) => CreateChatCompletionsStream(
                        toolFlow ? "tool-backed answer" : answer,
                        reasoning),
                    _ => throw new InvalidOperationException($"Unsupported test API flavor '{apiFlavor}'.")
                };
                var body = Encoding.UTF8.GetBytes(data);
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }
        }

        private void AssertRequest(HttpRequest request, int requestIndex)
        {
            request.Method.Should().Be("POST");
            request.Path.Should().Be(apiFlavor switch
            {
                OpenAiApiFlavor.Responses => "/v1/responses",
                OpenAiApiFlavor.ChatCompletions => "/v1/chat/completions",
                _ => throw new InvalidOperationException($"Unsupported test API flavor '{apiFlavor}'.")
            });
            request.Body.GetProperty("model").GetString().Should().Be(model);
            request.Body.GetProperty("stream").GetBoolean().Should().BeTrue();
            request.Body.GetProperty("store").GetBoolean().Should().BeFalse();
            if (!toolFlow)
            {
                return;
            }

            request.Body.GetProperty("tools").GetArrayLength().Should().BeGreaterThan(0);
            request.Body.GetRawText().Should().Contain(toolName);
            if (requestIndex > 0)
            {
                request.Body.GetRawText().Should().Contain("status").And.Contain("ok");
                if (expectedToolResultText is not null)
                {
                    request.Body.GetRawText().Should().Contain(expectedToolResultText);
                }
            }
        }

        private static string CreateChatCompletionsStream(string text, string? reasoning = null) =>
            (reasoning is null
                ? string.Empty
                : "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":0," +
                  "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{" +
                  "\"role\":\"assistant\",\"reasoning_content\":" + JsonSerializer.Serialize(reasoning) +
                  "},\"finish_reason\":null}]}\n\n") +
            "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":0," +
            "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{" +
            "\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) +
            "},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":0," +
            "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        private static string CreateChatCompletionsToolStream(string toolName, string argumentsJson) =>
            "data: {\"id\":\"chatcmpl-tool\",\"object\":\"chat.completion.chunk\",\"created\":0," +
            "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{" +
            "\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call-test\"," +
            "\"type\":\"function\",\"function\":{\"name\":" + JsonSerializer.Serialize(toolName) + "," +
            "\"arguments\":" + JsonSerializer.Serialize(argumentsJson) + "}}]},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"chatcmpl-tool\",\"object\":\"chat.completion.chunk\",\"created\":0," +
            "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{}," +
            "\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";

        private static string CreateResponsesStream(string text)
        {
            const string inProgressResponse =
                "{\"id\":\"resp-test\",\"object\":\"response\",\"created_at\":0," +
                "\"status\":\"in_progress\",\"error\":null,\"incomplete_details\":null," +
                "\"instructions\":null,\"max_output_tokens\":null,\"model\":\"test-model\"," +
                "\"output\":[],\"parallel_tool_calls\":true,\"previous_response_id\":null," +
                "\"reasoning\":null,\"store\":false,\"temperature\":null," +
                "\"text\":{\"format\":{\"type\":\"text\"}},\"tool_choice\":\"auto\"," +
                "\"tools\":[],\"top_p\":null,\"truncation\":\"disabled\",\"usage\":null," +
                "\"metadata\":{}}";
            var completedItem =
                "{\"id\":\"msg-test\",\"type\":\"message\",\"status\":\"completed\"," +
                "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\"," +
                "\"text\":" + JsonSerializer.Serialize(text) +
                ",\"annotations\":[],\"logprobs\":[]}]}";
            var completedResponse =
                "{\"id\":\"resp-test\",\"object\":\"response\",\"created_at\":0," +
                "\"status\":\"completed\",\"error\":null,\"incomplete_details\":null," +
                "\"instructions\":null,\"max_output_tokens\":null,\"model\":\"test-model\"," +
                "\"output\":[" + completedItem + "],\"parallel_tool_calls\":true," +
                "\"previous_response_id\":null,\"reasoning\":null,\"store\":false," +
                "\"temperature\":null,\"text\":{\"format\":{\"type\":\"text\"}}," +
                "\"tool_choice\":\"auto\",\"tools\":[],\"top_p\":null," +
                "\"truncation\":\"disabled\",\"usage\":{\"input_tokens\":1," +
                "\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":1," +
                "\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":2}," +
                "\"metadata\":{}}";

            return
                "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0," +
                "\"response\":" + inProgressResponse + "}\n\n" +
                "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\"," +
                "\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"msg-test\"," +
                "\"type\":\"message\",\"status\":\"in_progress\",\"role\":\"assistant\"," +
                "\"content\":[]}}\n\n" +
                "event: response.content_part.added\ndata: {\"type\":\"response.content_part.added\"," +
                "\"sequence_number\":2,\"item_id\":\"msg-test\",\"output_index\":0," +
                "\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\"," +
                "\"annotations\":[],\"logprobs\":[]}}\n\n" +
                "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\"," +
                "\"sequence_number\":3,\"item_id\":\"msg-test\",\"output_index\":0," +
                "\"content_index\":0,\"delta\":" + JsonSerializer.Serialize(text) +
                ",\"logprobs\":[]}\n\n" +
                "event: response.output_text.done\ndata: {\"type\":\"response.output_text.done\"," +
                "\"sequence_number\":4,\"item_id\":\"msg-test\",\"output_index\":0," +
                "\"content_index\":0,\"text\":" + JsonSerializer.Serialize(text) +
                ",\"logprobs\":[]}\n\n" +
                "event: response.content_part.done\ndata: {\"type\":\"response.content_part.done\"," +
                "\"sequence_number\":5,\"item_id\":\"msg-test\",\"output_index\":0," +
                "\"content_index\":0,\"part\":{\"type\":\"output_text\"," +
                "\"text\":" + JsonSerializer.Serialize(text) +
                ",\"annotations\":[],\"logprobs\":[]}}\n\n" +
                "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\"," +
                "\"sequence_number\":6,\"output_index\":0,\"item\":" + completedItem + "}\n\n" +
                "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":7," +
                "\"response\":" + completedResponse + "}\n\n";
        }

        private static string CreateResponsesToolStream(string toolName, string argumentsJson)
        {
            const string inProgressResponse =
                "{\"id\":\"resp-tool\",\"object\":\"response\",\"created_at\":0," +
                "\"status\":\"in_progress\",\"error\":null,\"incomplete_details\":null," +
                "\"instructions\":null,\"max_output_tokens\":null,\"model\":\"test-model\"," +
                "\"output\":[],\"parallel_tool_calls\":true,\"previous_response_id\":null," +
                "\"reasoning\":null,\"store\":false,\"temperature\":null," +
                "\"text\":{\"format\":{\"type\":\"text\"}},\"tool_choice\":\"auto\"," +
                "\"tools\":[],\"top_p\":null,\"truncation\":\"disabled\",\"usage\":null," +
                "\"metadata\":{}}";
            var completedItem =
                "{\"id\":\"fc-test\",\"type\":\"function_call\",\"status\":\"completed\"," +
                "\"arguments\":" + JsonSerializer.Serialize(argumentsJson) + ",\"call_id\":\"call-test\"," +
                "\"name\":" + JsonSerializer.Serialize(toolName) + "}";
            var completedResponse =
                "{\"id\":\"resp-tool\",\"object\":\"response\",\"created_at\":0," +
                "\"status\":\"completed\",\"error\":null,\"incomplete_details\":null," +
                "\"instructions\":null,\"max_output_tokens\":null,\"model\":\"test-model\"," +
                "\"output\":[" + completedItem + "],\"parallel_tool_calls\":true," +
                "\"previous_response_id\":null,\"reasoning\":null,\"store\":false," +
                "\"temperature\":null,\"text\":{\"format\":{\"type\":\"text\"}}," +
                "\"tool_choice\":\"auto\",\"tools\":[],\"top_p\":null," +
                "\"truncation\":\"disabled\",\"usage\":{\"input_tokens\":1," +
                "\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":1," +
                "\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":2}," +
                "\"metadata\":{}}";

            return
                "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0," +
                "\"response\":" + inProgressResponse + "}\n\n" +
                "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\"," +
                "\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"fc-test\"," +
                "\"type\":\"function_call\",\"status\":\"in_progress\",\"arguments\":\"\"," +
                "\"call_id\":\"call-test\",\"name\":" + JsonSerializer.Serialize(toolName) + "}}\n\n" +
                "event: response.function_call_arguments.delta\ndata: {" +
                "\"type\":\"response.function_call_arguments.delta\",\"sequence_number\":2," +
                "\"item_id\":\"fc-test\",\"output_index\":0," +
                "\"delta\":" + JsonSerializer.Serialize(argumentsJson) + "}\n\n" +
                "event: response.function_call_arguments.done\ndata: {" +
                "\"type\":\"response.function_call_arguments.done\",\"sequence_number\":3," +
                "\"item_id\":\"fc-test\",\"output_index\":0," +
                "\"arguments\":" + JsonSerializer.Serialize(argumentsJson) + "}\n\n" +
                "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\"," +
                "\"sequence_number\":4,\"output_index\":0,\"item\":" + completedItem + "}\n\n" +
                "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":5," +
                "\"response\":" + completedResponse + "}\n\n";
        }

        internal static async Task<HttpRequest> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var request = new MemoryStream();
            var headerLength = -1;
            var contentLength = 0;

            while (headerLength < 0 || request.Length < headerLength + contentLength)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new EndOfStreamException("The OpenAI-compatible request ended before its body was complete.");
                }

                request.Write(buffer, 0, count);
                if (headerLength >= 0)
                {
                    continue;
                }

                var bytes = request.GetBuffer().AsSpan(0, checked((int)request.Length));
                var delimiter = "\r\n\r\n"u8;
                var delimiterIndex = bytes.IndexOf(delimiter);
                if (delimiterIndex < 0)
                {
                    continue;
                }

                headerLength = delimiterIndex + delimiter.Length;
                var headers = Encoding.ASCII.GetString(bytes[..delimiterIndex]);
                foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                        break;
                    }
                }
            }

            var requestBytes = request.GetBuffer().AsSpan(0, checked((int)request.Length));
            var requestLineEnd = requestBytes.IndexOf("\r\n"u8);
            if (requestLineEnd < 0)
            {
                throw new InvalidDataException("The OpenAI-compatible request did not contain a request line.");
            }

            var requestLine = Encoding.ASCII.GetString(requestBytes[..requestLineEnd]);
            var requestLineParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLineParts.Length != 3)
            {
                throw new InvalidDataException($"Invalid OpenAI-compatible request line: '{requestLine}'.");
            }

            var bodyBytes = request.GetBuffer().AsMemory(headerLength, contentLength);
            var body = JsonDocument.Parse(bodyBytes).RootElement.Clone();
            return new HttpRequest(requestLineParts[0], requestLineParts[1], body);
        }

        internal sealed record HttpRequest(string Method, string Path, JsonElement Body);
    }

    private sealed class FakeAnthropicServer : IAsyncDisposable
    {
        private readonly string model;
        private readonly string answer;
        private readonly bool toolFlow;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();

        public FakeAnthropicServer(string model, string answer, bool toolFlow = false)
        {
            this.model = model;
            this.answer = answer;
            this.toolFlow = toolFlow;
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}");
            Completion = ServeAsync(cancellation.Token);
        }

        public Uri Endpoint { get; }

        public Task Completion { get; }

        public ConcurrentQueue<JsonElement> RequestBodies { get; } = new();

        public JsonElement RequestBody => RequestBodies.Single();

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            listener.Stop();
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (SocketException) when (cancellation.IsCancellationRequested)
            {
            }

            cancellation.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            var requestCount = toolFlow ? 2 : 1;
            for (var requestIndex = 0; requestIndex < requestCount; requestIndex++)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = client.GetStream();
                var request = await FakeOpenAiServer.ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                request.Method.Should().Be("POST");
                request.Path.Should().Be("/v1/messages");
                request.Body.GetProperty("model").GetString().Should().Be(model);
                request.Body.GetProperty("stream").GetBoolean().Should().BeTrue();
                RequestBodies.Enqueue(request.Body);
                if (toolFlow)
                {
                    request.Body.GetRawText().Should().Contain("echo");
                }

                var data = toolFlow && requestIndex == 0 ? CreateToolStream() : CreateTextStream(answer);
                var body = Encoding.UTF8.GetBytes(data);
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }
        }

        private static string CreateTextStream(string text) => """
                                                               event: message_start
                                                               data: {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

                                                               event: content_block_start
                                                               data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                                                               event: content_block_delta
                                                               data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":TEXT_PLACEHOLDER}}

                                                               event: content_block_stop
                                                               data: {"type":"content_block_stop","index":0}

                                                               event: message_delta
                                                               data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

                                                               event: message_stop
                                                               data: {"type":"message_stop"}

                                                               """.Replace("TEXT_PLACEHOLDER",
            JsonSerializer.Serialize(text), StringComparison.Ordinal);

        private static string CreateToolStream() => """
                                                    event: message_start
                                                    data: {"type":"message_start","message":{"id":"msg_tool","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

                                                    event: content_block_start
                                                    data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_test","name":"echo","input":{}}}

                                                    event: content_block_delta
                                                    data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"text\":"}}

                                                    event: content_block_delta
                                                    data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"hello\"}"}}

                                                    event: content_block_stop
                                                    data: {"type":"content_block_stop","index":0}

                                                    event: message_delta
                                                    data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":1}}

                                                    event: message_stop
                                                    data: {"type":"message_stop"}

                                                    """;
    }

    private static AIFunction CreateEchoFunction() =>
        AIFunctionFactory.Create(
            (string text) => JsonSerializer.SerializeToElement(
                text,
                HostIntegrationJsonContext.Default.String),
            new AIFunctionFactoryOptions
            {
                Name = "echo",
                Description = "Returns the supplied text.",
                SerializerOptions = HostIntegrationJsonContext.Default.Options
            });

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CancellationTokenSource CreateDeadline(TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class HostIntegrationJsonContext : JsonSerializerContext;
