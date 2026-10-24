using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Maieutics.Providers.OpenAI;

namespace Maieutics.Product.Tests;

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
        // The scripted tool flow doubles as the published-process function
        // continuation check: the model calls write_text, the AOT process
        // marshals arguments through the source-generated tool contract,
        // writes the file, and returns the structured result to the model.
        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"maieutics-smoke-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var provider = new GatedModelProvider(
            ModelApiFlavor.ChatCompletions,
            ProviderGateMode.Scripted,
            toolFlow: true,
            toolName: "write_text",
            toolArgumentsJson: """{"uri":"workspace://local/smoke.txt","content":"smoke body\n"}""",
            // The tool result rides inside the tool message's JSON-escaped
            // content, so match a substring without quotes or colons.
            expectedToolResultText: "smoke.txt");
        var process = StartHostProcess(provider.Endpoint, deadline.Token, workspace);
        try
        {
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
                Assert.Equal(1, capabilities.GetProperty("protocolVersion").GetInt32());
                var session = await client.GetFromJsonAsync<JsonElement>(
                    "/v1/agent/session", deadline.Token);
                var sessionId = session.GetProperty("id").GetString()!;

                using var response = await client.PostAsJsonAsync(
                    $"/v1/agent/sessions/{sessionId}/turns",
                    new { text = "%status" },
                    deadline.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
                Assert.Contains("### Maieutics status", body.GetProperty("markdown").GetString());

                var turn = await client.PostAsJsonAsync(
                    $"/v1/agent/sessions/{sessionId}/turns",
                    new { text = "write the smoke file" },
                    deadline.Token);
                Assert.Equal(HttpStatusCode.Accepted, turn.StatusCode);
                var runId = (await turn.Content.ReadFromJsonAsync<JsonElement>(deadline.Token))
                    .GetProperty("runId").GetString()!;

                var transcript = await WaitForTranscriptTurnAsync(client, sessionId, deadline.Token);
                Assert.Equal(runId, transcript.GetProperty("runId").GetString());
                // The tool flow commits the intermediate tool-call messages, so
                // the final assistant answer is the transcript's last message.
                var messages = transcript.GetProperty("messages");
                Assert.Equal("tool-backed answer", messages[messages.GetArrayLength() - 1].GetProperty("parts")[0]
                    .GetProperty("text").GetString());

                // The write_text tool result reached the model (the fake provider
                // asserts "smoke.txt" rode in the follow-up request) and the
                // canonical transcript records the structured result envelope.
                Assert.Contains("smoke.txt", transcript.GetRawText());
            }
            finally
            {
                client.Dispose();
            }
        }
        finally
        {
            // The child must never outlive the test, including on failures: a leaked
            // host keeps running for the rest of the job and holds the temp files.
            await process.DisposeAsync();
            try
            {
                await TempArea.DeleteAsync(workspace, CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup on the same grounds as the discovery file.
            }
        }
    }

    private static CancellationTokenSource CreateDeadline(
        CancellationToken cancellationToken,
        TimeSpan timeout) => TestDeadlines.Create(cancellationToken, timeout);

    private static async Task<JsonElement> WaitForTranscriptTurnAsync(
        HttpClient client,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(45));
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

    private static SmokeHostProcess StartHostProcess(
        Uri endpoint,
        CancellationToken cancellationToken,
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
        if (workspaceRoot is not null) startInfo.Environment["Maieutics__Workspace__Root"] = workspaceRoot;
        startInfo.Environment.Remove("Maieutics__Model__Provider");
        startInfo.Environment.Remove("MAIEUTICS_CONFIG");
        startInfo.Environment.Remove("MAIEUTICS_PROVIDER");
        startInfo.Environment.Remove("MAIEUTICS_WORKSPACE");
        startInfo.Environment.Remove("MAIEUTICS_OPENAI_API");

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("The Maieutics process could not be started.");
        Console.WriteLine($"[smoke] host pid {process.Id} discovery {discoveryPath}");
        var stderrTail = TailStream(process, redirectError: true);
        // Stdout must be drained even though the protocol lives on the discovery file:
        // an undrained pipe fills on Windows and blocks the child mid-write.
        var stdoutTail = TailStream(process, redirectError: false);

        return new SmokeHostProcess(
            process,
            discoveryPath,
            configurationFile,
            () => $"{stdoutTail()}\n{stderrTail()}".Trim());
    }

    private static string GetManagedHostAssemblyPath()
    {
        // The managed fallback (runs without MAIEUTICS_TEST_HOST_EXECUTABLE) launches the
        // app dll through `dotnet`. The configuration folder matches the test assembly's own
        // (Debug/Release), and the assembly name is the csproj's lowercase `maieutics`.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "Maieutics", "bin", configuration, "net10.0", "maieutics.dll");
    }

    private static Func<string> TailStream(Process process, bool redirectError)
    {
        var chunks = new List<string>();
        if (redirectError)
        {
            process.ErrorDataReceived += static (_, _) => { };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null) return;
                lock (chunks) chunks.Add(eventArgs.Data);
                while (chunks.Count > 40) chunks.RemoveAt(0);
            };
            process.BeginErrorReadLine();
        }
        else
        {
            process.OutputDataReceived += static (_, _) => { };
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null) return;
                lock (chunks) chunks.Add(eventArgs.Data);
                while (chunks.Count > 40) chunks.RemoveAt(0);
            };
            process.BeginOutputReadLine();
        }

        return () => string.Join("\n", chunks);
    }

    private sealed class SmokeHostProcess(
        Process process,
        string discoveryPath,
        string configurationFile,
        Func<string> outputTail) : IAsyncDisposable
    {
        // Teardown must run at most once: the explicit finally dispose would
        // otherwise double-kill.
        private int disposeState;

        public string DiscoveryPath { get; } = discoveryPath;

        /// <summary>Waits for the discovery file's appearance within the remaining test
        /// budget. A dead child is reported immediately; otherwise the shared reader retries
        /// the transient open/parse failures (atomic rename, Windows scanner lock).</summary>
        public async Task<JsonElement> WaitForDiscoveryAsync(CancellationToken cancellationToken)
        {
            return await FrontendDiscoveryFile.ReadAsync(
                DiscoveryPath,
                TimeSpan.FromSeconds(60),
                cancellationToken,
                fault: () => process.HasExited
                    ? $"The Maieutics process exited with code {process.ExitCode} before publishing discovery."
                      + (outputTail() is { Length: > 0 } tail ? $"\noutput:\n{tail}" : string.Empty)
                    : null).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposeState, 1) != 0) return;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process instance lost its association (already reaped by the OS)
                // or the OS refused the kill because the child just exited.
            }

            // Bounded: a kill that has not reaped within this budget must not consume
            // the test's remaining xUnit timeout — a canceled teardown was exactly the
            // smoke-test flake signature.
            using var reaped = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(reaped.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or InvalidOperationException)
            {
            }

            process.Dispose();
            foreach (var path in new[] { DiscoveryPath, configurationFile })
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup; a Windows lock can outlive the kill briefly.
                }
        }
    }
}
