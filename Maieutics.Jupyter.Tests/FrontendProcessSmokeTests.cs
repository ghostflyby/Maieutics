using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Providers.OpenAI;

namespace Maieutics.Jupyter.Tests;

/// <summary>
///     Process smoke coverage for the published (NativeAOT or managed) executable: the tests
///     spawn the real process with `--frontend-discovery`, drive the web protocol end to end
///     (capabilities, a `%status` command turn over the events socket, a real agent turn against
///     a fake model provider), and verify the discovery file is retired on shutdown.
///     Selected by the CI NativeAOT job via the `Category=Smoke` trait.
/// </summary>
[Trait("Category", "Smoke")]
public sealed class FrontendProcessSmokeTests
{
    [Fact(Timeout = 120_000)]
    public async Task PublishedExecutableServesTheFrontendProtocol()
    {
        using var deadline = CreateDeadline(
            TestContext.Current.CancellationToken,
            TimeSpan.FromSeconds(90));
        var provider = new FakeOpenAiServer(OpenAiApiFlavor.ChatCompletions, answer: "smoke answer");
        await using var process = StartHostProcess(provider.Endpoint, deadline.Token);
        var discovery = await process.WaitForDiscoveryAsync(deadline.Token);

        var baseUrl = discovery.GetProperty("url").GetString()!;
        var token = discovery.GetProperty("token").GetString()!;
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        try
        {
            var capabilities = await client.GetFromJsonAsync<JsonElement>(
                "/v1/agent/capabilities", deadline.Token);
            capabilities.GetProperty("protocolVersion").GetInt32().Should().Be(1);
            var session = await client.GetFromJsonAsync<JsonElement>(
                "/v1/agent/session", deadline.Token);
            var sessionId = session.GetProperty("id").GetString()!;

            using var response = await client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "%status" },
                deadline.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            body.GetProperty("markdown").GetString().Should().Contain("### Maieutics status");

            var turn = await client.PostAsJsonAsync(
                $"/v1/agent/sessions/{sessionId}/turns",
                new { text = "hello" },
                deadline.Token);
            turn.StatusCode.Should().Be(HttpStatusCode.Accepted);
            var runId = (await turn.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
                .GetProperty("runId").GetString()!;

            var transcript = await WaitForTranscriptTurnAsync(client, sessionId, deadline.Token);
            transcript.GetProperty("runId").GetString().Should().Be(runId);
            transcript.GetProperty("messages")[1].GetProperty("parts")[0]
                .GetProperty("text").GetString().Should().Be("smoke answer");
        }
        finally
        {
            client.Dispose();
        }

        await process.DisposeAsync();
        File.Exists(process.DiscoveryPath).Should().BeFalse();
    }

    private static CancellationTokenSource CreateDeadline(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private static async Task<JsonElement> WaitForTranscriptTurnAsync(
        HttpClient client,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var transcript = await client.GetFromJsonAsync<JsonElement>(
                $"/v1/agent/sessions/{sessionId}/transcript",
                wait.Token).ConfigureAwait(false);
            if (transcript.GetProperty("turns").GetArrayLength() > 0)
                return transcript.GetProperty("turns")[0];

            await Task.Delay(50, wait.Token).ConfigureAwait(false);
        }
    }

    private static SmokeHostProcess StartHostProcess(Uri endpoint, CancellationToken cancellationToken)
    {
        var nativeExecutable = Environment.GetEnvironmentVariable("MAIEUTICS_TEST_HOST_EXECUTABLE");
        var executablePath = string.IsNullOrWhiteSpace(nativeExecutable)
            ? null
            : Path.GetFullPath(nativeExecutable);
        if (executablePath is not null && !File.Exists(executablePath))
            throw new FileNotFoundException("The configured Maieutics test host executable does not exist.",
                executablePath);

        var assemblyPath = executablePath is null ? GetManagedHostAssemblyPath() : null;
        var configurationFile = Path.Combine(
            Path.GetTempPath(), $"maieutics-smoke-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(configurationFile, "{}");
        var discoveryPath = Path.Combine(
            Path.GetTempPath(), $"maieutics-smoke-discovery-{Guid.NewGuid():N}.json");

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
        startInfo.ArgumentList.Add("--frontend-discovery");
        startInfo.ArgumentList.Add(discoveryPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("test-model");
        startInfo.Environment["OPENAI_API_KEY"] = "test-key";
        // Env config uses the double-underscore section separator.
        startInfo.Environment["Maieutics__Providers__OpenAI__Endpoint"] = endpoint.ToString();
        startInfo.Environment["Maieutics__Providers__OpenAI__ApiFlavor"] =
            OpenAiApiFlavor.ChatCompletions.ToString();
        startInfo.Environment.Remove("Maieutics__Model__Provider");
        startInfo.Environment.Remove("MAIEUTICS_CONFIG");
        startInfo.Environment.Remove("MAIEUTICS_PROVIDER");
        startInfo.Environment.Remove("MAIEUTICS_WORKSPACE");
        startInfo.Environment.Remove("MAIEUTICS_OPENAI_API");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Maieutics process could not be started.");
        Console.WriteLine($"[smoke] host pid {process.Id} discovery {discoveryPath}");
        var stderrTail = TailStderr(process);

        return new SmokeHostProcess(
            process,
            discoveryPath,
            configurationFile,
            stderrTail);
    }

    private static string GetManagedHostAssemblyPath()
    {
        // The managed fallback (local runs without MAIEUTICS_TEST_HOST_EXECUTABLE) launches the
        // debug-built app dll through `dotnet`.
        return Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "Maieutics", "bin", "Debug", "net10.0", "Maieutics.dll");
    }

    private static Func<string> TailStderr(Process process)
    {
        var chunks = new List<string>();
        process.ErrorDataReceived += static (_, _) => { };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) return;
            lock (chunks) chunks.Add(eventArgs.Data);
            while (chunks.Count > 40) chunks.RemoveAt(0);
        };
        process.BeginErrorReadLine();
        return () => string.Join("\n", chunks);
    }

    private sealed class SmokeHostProcess(
        Process process,
        string discoveryPath,
        string configurationFile,
        Func<string> stderrTail) : IAsyncDisposable
    {
        public string DiscoveryPath { get; } = discoveryPath;

        public async Task<JsonElement> WaitForDiscoveryAsync(CancellationToken cancellationToken)
        {
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            while (true)
            {
                if (process.HasExited)
                {
                    var tail = stderrTail();
                    throw new InvalidOperationException(
                        $"The Maieutics process exited with code {process.ExitCode} before publishing discovery."
                        + (tail.Length > 0 ? $"\nstderr:\n{tail}" : ""));
                }

                try
                {
                    using var stream = File.OpenRead(DiscoveryPath);
                    using var document = await JsonDocument.ParseAsync(
                        stream, cancellationToken: deadline.Token).ConfigureAwait(false);
                    return document.RootElement.Clone();
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }

                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidOperationException)
            {
                // The process instance lost its association (already reaped by the OS); nothing to kill.
            }

            process.Dispose();
            foreach (var path in new[] { DiscoveryPath, configurationFile })
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
        }
    }
}
