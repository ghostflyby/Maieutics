using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;
using Maieutics.Providers.OpenAI;
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
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        var builder = MaieuticsHost.CreateApplicationBuilder(
            ["--connection-file", connectionFile, "--model", "test-model"]);
        builder.Configuration["Maieutics:OpenAI:ApiKey"] = "test-key";
        using var host = builder.Build();
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
        OpenAiApiFlavor apiFlavor = OpenAiApiFlavor.Responses)
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
            WorkingDirectory = Path.GetDirectoryName(executablePath ?? assemblyPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (assemblyPath is not null)
        {
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--connection-file");
        startInfo.ArgumentList.Add(connectionFile);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("test-model");
        if (apiFlavor is OpenAiApiFlavor.ChatCompletions)
        {
            startInfo.ArgumentList.Add("--openai-api");
            startInfo.ArgumentList.Add(nameof(OpenAiApiFlavor.ChatCompletions));
        }

        startInfo.Environment["OPENAI_API_KEY"] = "test-key";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment.Remove("MAIEUTICS_OPENAI_API");
        startInfo.Environment.Remove("Maieutics__OpenAI__ApiFlavor");
        if (endpoint is not null)
        {
            startInfo.Environment["OPENAI_BASE_URL"] = endpoint.ToString();
        }

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Could not start the Maieutics host process.");
        var standardOutput = new ConcurrentQueue<string>();
        var standardError = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                standardOutput.Enqueue(eventArgs.Data);
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
        return new StartedHostProcess(process, standardOutput, standardError);
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

    private sealed class StartedHostProcess(
        Process process,
        ConcurrentQueue<string> standardOutput,
        ConcurrentQueue<string> standardError) : IDisposable
    {
        public Process Process { get; } = process;

        public string FailureDetails()
        {
            var exit = Process.HasExited ? $" Exit code: {Process.ExitCode}." : string.Empty;
            return $"Maieutics host failed.{exit}\nstdout:\n{string.Join('\n', standardOutput)}" +
                   $"\nstderr:\n{string.Join('\n', standardError)}";
        }

        public void Dispose() => Process.Dispose();
    }

    private sealed class FakeOpenAiServer : IAsyncDisposable
    {
        private readonly OpenAiApiFlavor apiFlavor;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();

        public FakeOpenAiServer(OpenAiApiFlavor apiFlavor)
        {
            this.apiFlavor = apiFlavor;
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
            Completion = ServeOnceAsync(cancellation.Token);
        }

        public Uri Endpoint { get; }

        public Task Completion { get; }

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

        private async Task ServeOnceAsync(CancellationToken cancellationToken)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            AssertRequest(request);

            var data = apiFlavor switch
            {
                OpenAiApiFlavor.Responses => CreateResponsesStream(),
                OpenAiApiFlavor.ChatCompletions => CreateChatCompletionsStream(),
                _ => throw new InvalidOperationException($"Unsupported test API flavor '{apiFlavor}'.")
            };
            var body = Encoding.UTF8.GetBytes(data);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        private void AssertRequest(HttpRequest request)
        {
            request.Method.Should().Be("POST");
            request.Path.Should().Be(apiFlavor switch
            {
                OpenAiApiFlavor.Responses => "/v1/responses",
                OpenAiApiFlavor.ChatCompletions => "/v1/chat/completions",
                _ => throw new InvalidOperationException($"Unsupported test API flavor '{apiFlavor}'.")
            });
            request.Body.GetProperty("model").GetString().Should().Be("test-model");
            request.Body.GetProperty("stream").GetBoolean().Should().BeTrue();
            request.Body.GetProperty("store").GetBoolean().Should().BeFalse();
        }

        private static string CreateChatCompletionsStream() =>
            "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":0," +
            "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{" +
            "\"role\":\"assistant\",\"content\":\"native answer\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":0," +
            "\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        private static string CreateResponsesStream()
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
            const string completedItem =
                "{\"id\":\"msg-test\",\"type\":\"message\",\"status\":\"completed\"," +
                "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\"," +
                "\"text\":\"native answer\",\"annotations\":[],\"logprobs\":[]}]}";
            const string completedResponse =
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
                "\"content_index\":0,\"delta\":\"native answer\",\"logprobs\":[]}\n\n" +
                "event: response.output_text.done\ndata: {\"type\":\"response.output_text.done\"," +
                "\"sequence_number\":4,\"item_id\":\"msg-test\",\"output_index\":0," +
                "\"content_index\":0,\"text\":\"native answer\",\"logprobs\":[]}\n\n" +
                "event: response.content_part.done\ndata: {\"type\":\"response.content_part.done\"," +
                "\"sequence_number\":5,\"item_id\":\"msg-test\",\"output_index\":0," +
                "\"content_index\":0,\"part\":{\"type\":\"output_text\"," +
                "\"text\":\"native answer\",\"annotations\":[],\"logprobs\":[]}}\n\n" +
                "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\"," +
                "\"sequence_number\":6,\"output_index\":0,\"item\":" + completedItem + "}\n\n" +
                "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":7," +
                "\"response\":" + completedResponse + "}\n\n";
        }

        private static async Task<HttpRequest> ReadRequestAsync(
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

        private sealed record HttpRequest(string Method, string Path, JsonElement Body);
    }

    private static CancellationTokenSource CreateDeadline(TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }
}