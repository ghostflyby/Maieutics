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
using Maieutics.Jupyter.Client.Transport;
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
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(20));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-in-process-{Guid.NewGuid():N}.json");
        var configurationFile = CreateEmptyConfigurationFile("in-process");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        var builder = MaieuticsHost.CreateApplicationBuilder(
            ["--config", configurationFile, "--connection-file", connectionFile, "--model", "test-model"]);
        builder.Configuration["Maieutics:Providers:OpenAI:ApiKey"] = "test-key";
        await using var host = builder.Build();
        var functionNames = host.Services.GetRequiredService<IReadOnlyList<AIFunction>>()
            .Select(static function => function.Name)
            .ToArray();
        var session = host.Services.GetRequiredService<IAgentSession>();
        functionNames.Should().Contain(
            [
                "repl_execute",
                "repl_create",
                "repl_list",
                "repl_restart",
                "repl_close"
            ]
        );
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
            var status = await ExecuteAndGetMarkdownAsync(client, "%status", deadline.Token);
            status.Should()
                .Contain("### Maieutics status")
                .And.Contain("Configuration: version")
                .And.Contain("profile `default`")
                .And.Contain("Workspace: startup root")
                .And.Contain("path redacted")
                .And.Contain("Plugins: `Ready`")
                .And.Contain("MCP: no servers enabled")
                .And.Contain("Deno REPLs: no sessions")
                .And.NotContain(Path.GetFullPath(Directory.GetCurrentDirectory()))
                .And.NotContain(connectionFile);
            session.GetTranscriptSnapshot().Turns.Should().BeEmpty();
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
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-tool-{Guid.NewGuid():N}.json");
        var configurationFile = CreateEmptyConfigurationFile("tool");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await using var provider = new FakeOpenAiServer(apiFlavor, true);
        var builder = MaieuticsHost.CreateApplicationBuilder(
            ["--config", configurationFile, "--connection-file", connectionFile, "--model", "test-model"]);
        builder.Configuration["Maieutics:Providers:OpenAI:ApiKey"] = "test-key";
        builder.Configuration["Maieutics:Providers:OpenAI:Endpoint"] = provider.Endpoint.ToString();
        builder.Configuration["Maieutics:Providers:OpenAI:ApiFlavor"] = apiFlavor.ToString();
        builder.Services.RemoveAll<IReadOnlyList<AIFunction>>();
        builder.Services.AddSingleton<IReadOnlyList<AIFunction>>([CreateEchoFunction()]);
        await using var host = builder.Build();

        try
        {
            await host.StartAsync(deadline.Token);
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            await client.WaitForReadyAsync(deadline.Token);

            var execution = await client.ExecuteAsync(new JupyterExecuteRequest("use echo"), deadline.Token);
            var outputs = new List<JupyterOutput>();
            await foreach (var output in execution.Outputs.WithCancellation(deadline.Token)) outputs.Add(output);

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
    public async Task InProcessHostKeepsEvalControlPlaneWhileOutputMovesToTheDedicatedEndpoint()
    {
        // Phase 2 split: the REPL's console/display/updateDisplay/clearOutput events travel over
        // the dedicated binary output endpoint (/v1/repl/output/ws). The C# host receives them but
        // the execution collector is not yet wired to the output connection (phase 3), so those
        // outputs are not displayed. The eval channel keeps only the control plane: the input
        // request and the execution result still round-trip through it.
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-repl-{Guid.NewGuid():N}.json");
        var configurationFile = CreateEmptyConfigurationFile("repl");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        const string code =
            "const name = await prompt('Name: '); " +
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
            "40 + 2";
        await using var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.Responses,
            true,
            toolName: "repl_execute",
            toolArgumentsJson: JsonSerializer.Serialize(new { code }));
        var endpoint = provider.Endpoint.ToString();
        await using var host = MaieuticsHost.CreateApplication(
            ["--config", configurationFile, "--connection-file", connectionFile, "--model", "test-model"],
            builder =>
            {
                builder.Configuration["Maieutics:Providers:OpenAI:ApiKey"] = "test-key";
                builder.Configuration["Maieutics:Providers:OpenAI:Endpoint"] = endpoint;
            });

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
                if (output is JupyterInputRequest input) await execution.ReplyInputAsync(input, "Ada", deadline.Token);
            }

            (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
            outputs.OfType<JupyterStdout>().Should().BeEmpty();
            outputs.OfType<JupyterExecuteResultOutput>().Should().BeEmpty();
            outputs.OfType<JupyterInputRequest>().Should().ContainSingle().Which.Prompt.Should().Be("Name: ");
            // Output frames are received on the dedicated output endpoint but not displayed until
            // the collector consumes them (phase 3): no REPL stderr or display output reaches
            // Jupyter yet, while the agent's own markdown answer still does.
            outputs.OfType<JupyterStderr>().Should().BeEmpty();
            outputs.OfType<JupyterDisplayOutput>().Where(output =>
                    output.Data.Data.TryGetValue("text/markdown", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString() == "tool-backed answer")
                .Should().ContainSingle();
            outputs.OfType<JupyterDisplayOutput>().Should().NotContain(output =>
                output.Data.Data.ContainsKey("text/html"));
            outputs.OfType<JupyterDisplayUpdateOutput>().Should().BeEmpty();

            await provider.Completion.WaitAsync(deadline.Token);
            var toolOutput = provider.RequestBodies.Last()
                .GetProperty("input")
                .EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "function_call_output")
                .GetProperty("output")
                .GetString();
            toolOutput.Should().NotBeNull();
            using var toolResult = JsonDocument.Parse(toolOutput);
            var toolValue = toolResult.RootElement.GetProperty("value");
            var modelOutputs = toolValue.GetProperty("outputs");
            modelOutputs.EnumerateArray().Select(item => item.GetProperty("kind").GetString()).Should()
                .Equal("result");
            modelOutputs.EnumerateArray().Single().GetProperty("value").GetInt32().Should().Be(42);
            toolValue.GetProperty("executionStatus").GetString().Should().Be("ok");
            // The console/display text went to the output endpoint, not the model; the provider
            // secret stays out of both the child environment and the model output.
            toolOutput.Should().NotContain("private-name=Ada")
                .And.NotContain("shared-stderr")
                .And.NotContain("provider-secret")
                .And.NotContain("visible-display")
                .And.NotContain("visible-update");

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

    [Fact(Timeout = 90_000)]
    public async Task DenoReplRelaysAnywidgetStyleCommWithNativeBuffers()
    {
        // Simulates the exact API sequence of @anywidget/deno (0.2.x): broadcast
        // comm_open/comm_msg with native buffers, then surface a $display object as
        // the cell result (see docs/deno-jupyter-compat.md). The real package is not
        // imported because the REPL child has no general network permission.
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(70));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-anywidget-{Guid.NewGuid():N}.json");
        var configurationFile = CreateEmptyConfigurationFile("anywidget");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        const string code =
            "const commId = 'anywidget-model'; " +
            "await Deno.jupyter.broadcast('comm_open', " +
            "{ comm_id: commId, target_name: 'jupyter.widget', " +
            "data: { state: { _model_module: 'anywidget', _model_name: 'AnyModel' } } }, " +
            "{ buffers: [new Uint8Array([1, 2, 3])] }); " +
            "await Deno.jupyter.broadcast('comm_msg', " +
            "{ comm_id: commId, data: { method: 'update', state: { value: 42 }, buffer_paths: [] } }, " +
            "{ buffers: [new Uint8Array([4, 5])] }); " +
            "const widget = { " +
            "[Deno.jupyter.$display]: async () => " +
            "({ 'application/vnd.jupyter.widget-view+json': " +
            "{ version_major: 2, version_minor: 0, model_id: commId } }) " +
            "}; widget";
        await using var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.Responses,
            true,
            toolName: "repl_execute",
            toolArgumentsJson: JsonSerializer.Serialize(new { code }));
        var endpoint = provider.Endpoint.ToString();
        await using var host = MaieuticsHost.CreateApplication(
            ["--config", configurationFile, "--connection-file", connectionFile, "--model", "test-model"],
            builder =>
            {
                builder.Configuration["Maieutics:Providers:OpenAI:ApiKey"] = "test-key";
                builder.Configuration["Maieutics:Providers:OpenAI:Endpoint"] = endpoint;
            });

        try
        {
            await host.StartAsync(deadline.Token);
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            await client.WaitForReadyAsync(deadline.Token);
            await using var events = client.WatchEventsAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
            (await events.MoveNextAsync()).Should().BeTrue();

            var execution = await client.ExecuteAsync(
                new JupyterExecuteRequest("render the widget"),
                deadline.Token);
            var outputs = new List<JupyterOutput>();
            await foreach (var output in execution.Outputs.WithCancellation(deadline.Token)) outputs.Add(output);
            (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
            outputs.OfType<JupyterExecutionError>().Should().BeEmpty();

            // Phase 2 split: the $display widget object renders as a widget-view display event that
            // now travels over the dedicated binary output endpoint; the collector consumes the
            // output connection in phase 3, so the widget display is not yet routed to Jupyter. The
            // comm channel is independent of the eval/output split and still relays the widget
            // comm_open/comm_msg with native buffers.
            outputs.OfType<JupyterDisplayOutput>().Should().NotContain(output =>
                output.Data.Data.ContainsKey("application/vnd.jupyter.widget-view+json"));

            // comm_open and comm_msg reach the frontend over iopub as unhandled messages.
            var commTypes = new List<(string Type, string CommId, string? TargetName)>();
            while (commTypes.Count < 2)
            {
                if (!await events.MoveNextAsync())
                    throw new InvalidOperationException("The event stream ended before comm messages arrived.");

                if (events.Current is JupyterUnhandledMessage
                    {
                        Channel: JupyterTransportChannel.Iopub
                    } unhandled)
                {
                    var content = unhandled.Message.Content;
                    var commId = content.TryGetProperty("comm_id", out var id)
                        ? id.GetString()
                        : null;
                    var targetName = content.TryGetProperty("target_name", out var target)
                        ? target.GetString()
                        : null;
                    if (commId == "anywidget-model")
                        commTypes.Add((unhandled.Message.MessageType, commId!, targetName));
                }
            }

            commTypes.Select(static item => item.Type).Should().Contain("comm_open").And.Contain("comm_msg");
            commTypes.Single(item => item.Type == "comm_open").TargetName.Should().Be("jupyter.widget");
            commTypes.Should().OnlyContain(item => item.CommId == "anywidget-model");

            await provider.Completion.WaitAsync(deadline.Token);
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
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
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
        await using var host = builder.Build();
        var hostStarted = false;

        try
        {
            await host.StartAsync(deadline.Token);
            hostStarted = true;
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            await client.WaitForReadyAsync(deadline.Token);

            var execution = await client.ExecuteAsync(new JupyterExecuteRequest("think"), deadline.Token);
            var outputs = new List<JupyterOutput>();
            await foreach (var output in execution.Outputs.WithCancellation(deadline.Token)) outputs.Add(output);

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
            updates.Add(update);

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
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-anthropic-tool-{Guid.NewGuid():N}.json");
        var configurationFile = CreateEmptyConfigurationFile("anthropic-tool");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await using var provider = new FakeAnthropicServer(
            "claude-test",
            "tool-backed answer",
            true);
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
        await using var host = builder.Build();

        try
        {
            await host.StartAsync(deadline.Token);
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            await client.WaitForReadyAsync(deadline.Token);

            var execution = await client.ExecuteAsync(new JupyterExecuteRequest("use echo"), deadline.Token);
            var outputs = new List<JupyterOutput>();
            await foreach (var output in execution.Outputs.WithCancellation(deadline.Token)) outputs.Add(output);

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
    [Trait("Category", "Smoke")]
    [InlineData(OpenAiApiFlavor.Responses)]
    [InlineData(OpenAiApiFlavor.ChatCompletions)]
    public async Task GenericHostStartsRealKernelAndStopsAfterShutdown(OpenAiApiFlavor apiFlavor)
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(35));
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

            (await ExecuteAndGetMarkdownAsync(client, "%status", deadline.Token)).Should()
                .Contain("### Maieutics status")
                .And.Contain("profile `default`")
                .And.Contain("Plugins: `Ready`")
                .And.Contain("path redacted");

            (await ExecuteAndGetMarkdownAsync(
                    client,
                    "%maieutics workspace current",
                    deadline.Token))
                .Should().Contain("Current workspace").And.Contain("startup root");

            var execution = await client.ExecuteAsync(new JupyterExecuteRequest("hello"), deadline.Token);
            var outputs = new List<JupyterOutput>();
            await foreach (var output in execution.Outputs.WithCancellation(deadline.Token)) outputs.Add(output);

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
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            File.Delete(connectionFile);
        }
    }

    [Theory(Timeout = 60_000)]
    [Trait("Category", "Smoke")]
    [InlineData(OpenAiApiFlavor.Responses)]
    [InlineData(OpenAiApiFlavor.ChatCompletions)]
    public async Task ExternalHostCompletesWorkspaceFunctionLoop(OpenAiApiFlavor apiFlavor)
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
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
            true,
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
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Smoke")]
    public async Task ExternalHostCompletesDenoReplEvalBridge()
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(90));
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-native-repl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(root, "connection.json");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        const string code = "40 + 2";
        await using var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.Responses,
            true,
            toolName: "repl_execute",
            toolArgumentsJson: JsonSerializer.Serialize(new { code }));
        using var started = StartHostProcess(
            connectionFile,
            provider.Endpoint,
            OpenAiApiFlavor.Responses,
            root);
        var process = started.Process;
        var phase = "client readiness";

        try
        {
            await using var client = await JupyterClient.ConnectAsync(connection, cancellationToken: deadline.Token);
            var ready = client.WaitForReadyAsync(deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            (await Task.WhenAny(ready, exited)).Should().BeSameAs(ready, started.FailureDetails());
            await ready;

            phase = "Deno REPL eval bridge function continuation";
            (await ExecuteAndGetMarkdownAsync(client, "use the Deno REPL", deadline.Token)).Should()
                .Be("tool-backed answer");
            await provider.Completion.WaitAsync(deadline.Token);
            var toolOutput = provider.RequestBodies.Last()
                .GetProperty("input")
                .EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "function_call_output")
                .GetProperty("output")
                .GetString();
            toolOutput.Should().NotBeNull();
            using var toolResult = JsonDocument.Parse(toolOutput);
            var toolValue = toolResult.RootElement.GetProperty("value");
            toolValue.GetProperty("executionStatus").GetString().Should().Be("ok");
            toolValue.GetProperty("outputs").EnumerateArray().Single().GetProperty("value").GetInt32().Should().Be(42);

            phase = "kernel shutdown";
            await client.ShutdownAsync(false, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            process.ExitCode.Should().Be(0, started.FailureDetails());
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"External Deno REPL eval bridge failed during {phase}.\n{started.FailureDetails()}",
                exception);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ExternalHostCompletesMcpFunctionLoopWhenTestServerIsConfigured()
    {
        var mcpServer = Environment.GetEnvironmentVariable("MAIEUTICS_TEST_MCP_SERVER_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(mcpServer)) return;

        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
        var root = Path.Combine(Path.GetTempPath(), $"maieutics-native-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(root, "connection.json");
        var configurationFile = Path.Combine(root, "maieutics.json");
        var mcpFile = Path.Combine(root, "mcp.json");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        await using var provider = new FakeOpenAiServer(
            OpenAiApiFlavor.Responses,
            true,
            toolName: "echo",
            toolArgumentsJson: "{\"value\":\"native mcp value\"}",
            expectedToolResultText: "native mcp value");
        await File.WriteAllTextAsync(
            configurationFile,
            CreateMcpHostConfiguration(connectionFile, provider.Endpoint),
            deadline.Token);
        await File.WriteAllTextAsync(
            mcpFile,
            CreateMcpFile(Path.GetFullPath(mcpServer)),
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
                .Contain("`echo` → `echo`").And.Contain("Connected");
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
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(root, true);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(OpenAiApiFlavor.Responses)]
    [InlineData(OpenAiApiFlavor.ChatCompletions)]
    public async Task ReloadedProviderConfigurationIsUsedByTheNextNotebookCell(OpenAiApiFlavor apiFlavor)
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
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
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(root, true);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(OpenAiApiFlavor.Responses)]
    [InlineData(OpenAiApiFlavor.ChatCompletions)]
    public async Task NotebookSwitchesBetweenOpenAiAndAnthropicWithCanonicalHistory(OpenAiApiFlavor apiFlavor)
    {
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(45));
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
        await using var host = builder.Build();

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
        using var deadline = CreateDeadline(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(60));
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
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(root, true);
        }
    }

    [Fact(Timeout = 30_000)]
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
            throw new FileNotFoundException("The configured Maieutics test host executable does not exist.",
                executablePath);

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
        if (assemblyPath is not null) startInfo.ArgumentList.Add(assemblyPath);

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
        if (endpoint is not null) startInfo.Environment["OPENAI_BASE_URL"] = endpoint.ToString();

        return StartProcess(startInfo, configurationFile);
    }

    private static StartedHostProcess StartConfiguredHostProcess(string configurationFile)
    {
        var nativeExecutable = Environment.GetEnvironmentVariable("MAIEUTICS_TEST_HOST_EXECUTABLE");
        var executablePath = string.IsNullOrWhiteSpace(nativeExecutable)
            ? null
            : Path.GetFullPath(nativeExecutable);
        if (executablePath is not null && !File.Exists(executablePath))
            throw new FileNotFoundException("The configured Maieutics test host executable does not exist.",
                executablePath);

        var assemblyPath = executablePath is null ? GetManagedHostAssemblyPath() : null;
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath ?? "dotnet",
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (assemblyPath is not null) startInfo.ArgumentList.Add(assemblyPath);

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
            if (eventArgs.Data is not null) standardError.Enqueue(eventArgs.Data);
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
        await foreach (var output in execution.Outputs.WithCancellation(cancellationToken)) outputs.Add(output);

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

    private static string CreateMcpFile(string mcpServer)
    {
        return new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["test"] = new JsonObject
                {
                    ["command"] = mcpServer,
                    ["args"] = new JsonArray(),
                    ["env"] = new JsonObject()
                }
            }
        }.ToJsonString();
    }

    private static string CreateMcpHostConfiguration(string connectionFile, Uri endpoint)
    {
        return new JsonObject
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
                ["Jupyter"] = new JsonObject { ["ConnectionFile"] = connectionFile }
            }
        }.ToJsonString();
    }

    private static string CreateMultiProviderHostConfiguration(
        string connectionFile,
        Uri openAiEndpoint,
        OpenAiApiFlavor apiFlavor,
        Uri anthropicEndpoint)
    {
        return new JsonObject
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
    }

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

    private static AIFunction CreateEchoFunction()
    {
        return AIFunctionFactory.Create(
            (string text) => JsonSerializer.SerializeToElement(
                text,
                HostIntegrationJsonContext.Default.String),
            new AIFunctionFactoryOptions
            {
                Name = "echo",
                Description = "Returns the supplied text.",
                SerializerOptions = HostIntegrationJsonContext.Default.Options
            });
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private sealed class StartedHostProcess(
        Process process,
        ConcurrentQueue<string> standardOutput,
        ConcurrentQueue<string> standardError,
        SemaphoreSlim outputChanged,
        string? temporaryConfigurationFile) : IDisposable
    {
        public Process Process { get; } = process;

        public void Dispose()
        {
            outputChanged.Dispose();
            Process.Dispose();
            if (temporaryConfigurationFile is not null) File.Delete(temporaryConfigurationFile);
        }

        public string FailureDetails()
        {
            var exit = Process.HasExited
                ? $" Exit code: {Process.ExitCode}."
                : string.Empty;
            return $"Maieutics host failed.{exit}\nstdout:\n{string.Join('\n', standardOutput)}" +
                   $"\nstderr:\n{string.Join('\n', standardError)}";
        }

        public async Task WaitForOutputAsync(string text, CancellationToken cancellationToken)
        {
            while (!standardOutput.Any(line => line.Contains(text, StringComparison.Ordinal)))
                await outputChanged.WaitAsync(cancellationToken);
        }
    }

    private sealed class FakeOpenAiServer : IAsyncDisposable
    {
        private readonly string answer;
        private readonly OpenAiApiFlavor apiFlavor;
        private readonly CancellationTokenSource cancellation = new();
        private readonly string? expectedToolResultText;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly string model;
        private readonly string? reasoning;
        private readonly int requestCount;
        private readonly string toolArgumentsJson;

        private readonly bool toolFlow;
        private readonly string toolName;

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
            await cancellation.CancelAsync();
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
            // Handle the expected requests across connections. The OpenAI SDK's
            // HttpClient pools connections (keep-alive by default), so a client
            // may reuse the same TCP connection for consecutive requests or open
            // a fresh one. Loop on a per-connection basis: read one request,
            // answer it, and keep the connection open for the next request. A
            // "Connection: close" response per request would race the client's
            // connection reuse (the pooled connection may already be closed when
            // the next request arrives), surfacing as an EndOfStreamException on
            // slow CI.
            var served = 0;
            while (served < requestCount)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var stream = client.GetStream();
                while (served < requestCount)
                {
                    HttpRequest request;
                    try
                    {
                        request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                    }
                    catch (EndOfStreamException)
                    {
                        // The client closed this connection (dispose or a fresh
                        // connection for the next request); accept the next one.
                        break;
                    }
                    RequestBodies.Enqueue(request.Body.Clone());
                    AssertRequest(request, served);

                    var data = (apiFlavor, toolFlow, served) switch
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
                        _ => throw new InvalidOperationException(
                            $"Unsupported test API flavor '{apiFlavor}'.")
                    };
                    var body = Encoding.UTF8.GetBytes(data);
                    // Keep the connection open (no "Connection: close") so the
                    // client's pooled connection can carry the next request; the
                    // outer loop accepts a fresh connection when this one ends.
                    var headers = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
                        $"Content-Length: {body.Length}\r\n\r\n");
                    await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                    served++;
                }
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
            if (!toolFlow) return;

            request.Body.GetProperty("tools").GetArrayLength().Should().BeGreaterThan(0);
            request.Body.GetRawText().Should().Contain(toolName);
            if (requestIndex > 0)
            {
                request.Body.GetRawText().Should().Contain("status").And.Contain("ok");
                if (expectedToolResultText is not null)
                    request.Body.GetRawText().Should().Contain(expectedToolResultText);
            }
        }

        private static string CreateChatCompletionsStream(string text, string? reasoning = null)
        {
            return (reasoning is null
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
        }

        private static string CreateChatCompletionsToolStream(string toolName, string argumentsJson)
        {
            return "data: {\"id\":\"chatcmpl-tool\",\"object\":\"chat.completion.chunk\",\"created\":0," +
                   "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{" +
                   "\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call-test\"," +
                   "\"type\":\"function\",\"function\":{\"name\":" + JsonSerializer.Serialize(toolName) + "," +
                   "\"arguments\":" + JsonSerializer.Serialize(argumentsJson) + "}}]},\"finish_reason\":null}]}\n\n" +
                   "data: {\"id\":\"chatcmpl-tool\",\"object\":\"chat.completion.chunk\",\"created\":0," +
                   "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{}," +
                   "\"finish_reason\":\"tool_calls\"}]}\n\n" +
                   "data: [DONE]\n\n";
        }

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
                    throw new EndOfStreamException("The OpenAI-compatible request ended before its body was complete.");

                request.Write(buffer, 0, count);
                if (headerLength >= 0) continue;

                var bytes = request.GetBuffer().AsSpan(0, checked((int)request.Length));
                var delimiter = "\r\n\r\n"u8;
                var delimiterIndex = bytes.IndexOf(delimiter);
                if (delimiterIndex < 0) continue;

                headerLength = delimiterIndex + delimiter.Length;
                var headers = Encoding.ASCII.GetString(bytes[..delimiterIndex]);
                foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                        break;
                    }
            }

            var requestBytes = request.GetBuffer().AsSpan(0, checked((int)request.Length));
            var requestLineEnd = requestBytes.IndexOf("\r\n"u8);
            if (requestLineEnd < 0)
                throw new InvalidDataException("The OpenAI-compatible request did not contain a request line.");

            var requestLine = Encoding.ASCII.GetString(requestBytes[..requestLineEnd]);
            var requestLineParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLineParts.Length != 3)
                throw new InvalidDataException($"Invalid OpenAI-compatible request line: '{requestLine}'.");

            var bodyBytes = request.GetBuffer().AsMemory(headerLength, contentLength);
            var body = JsonDocument.Parse(bodyBytes).RootElement.Clone();
            return new HttpRequest(requestLineParts[0], requestLineParts[1], body);
        }

        internal sealed record HttpRequest(string Method, string Path, JsonElement Body);
    }

    private sealed class FakeAnthropicServer : IAsyncDisposable
    {
        private readonly string answer;
        private readonly CancellationTokenSource cancellation = new();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly string model;
        private readonly bool toolFlow;

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
            await cancellation.CancelAsync();
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
            // Same keep-alive handling as FakeOpenAiServer: the Anthropic SDK's
            // HttpClient pools connections, so keep each connection open for the
            // next request instead of racing the client's connection reuse with a
            // per-request "Connection: close".
            var requestCount = toolFlow ? 2 : 1;
            var served = 0;
            while (served < requestCount)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = client.GetStream();
                while (served < requestCount)
                {
                    FakeOpenAiServer.HttpRequest request;
                    try
                    {
                        request = await FakeOpenAiServer.ReadRequestAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (EndOfStreamException)
                    {
                        break; // client closed this connection; accept the next one
                    }
                    request.Method.Should().Be("POST");
                    request.Path.Should().Be("/v1/messages");
                    request.Body.GetProperty("model").GetString().Should().Be(model);
                    request.Body.GetProperty("stream").GetBoolean().Should().BeTrue();
                    RequestBodies.Enqueue(request.Body);
                    if (toolFlow) request.Body.GetRawText().Should().Contain("echo");

                    var data = toolFlow && served == 0 ? CreateToolStream() : CreateTextStream(answer);
                    var body = Encoding.UTF8.GetBytes(data);
                    var headers = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n\r\n");
                    await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                    served++;
                }
            }
        }

        private static string CreateTextStream(string text)
        {
            return """
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
        }

        private static string CreateToolStream()
        {
            return """
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
    }
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class HostIntegrationJsonContext : JsonSerializerContext;
