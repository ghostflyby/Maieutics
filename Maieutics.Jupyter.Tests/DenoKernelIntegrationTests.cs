using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class DenoKernelIntegrationTests
{
    private static readonly string DenoKernelSpecPath = Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "kernels",
        "deno",
        "kernel.json");

    [Fact(Timeout = 30_000)]
    public async Task LocalManagerConnectsToRealDenoKernel()
    {
        using var deadline = CreateDeadline();
        var cancellationToken = deadline.Token;
        var spec = await JupyterKernelSpec.ReadAsync(
            DenoKernelSpecPath,
            cancellationToken);
        await using var manager = await LocalJupyterKernelManager.StartAsync(
            spec,
            cancellationToken: cancellationToken);
        var client = manager.Client;

        var latency = await client.PingAsync(cancellationToken);
        latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        var kernelInfo = await client.GetKernelInfoAsync(cancellationToken);
        kernelInfo.LanguageInfo.Name.Should().BeOneOf("typescript", "javascript");
        kernelInfo.Status.Should().Be("ok");

        var completion = await client.CompleteAsync(new JupyterCompleteRequest("cons", 4), cancellationToken);
        completion.Status.Should().Be("ok");
        completion.Matches.Should().Contain("console");
        completion.CursorStart.Should().Be(0);
        completion.CursorEnd.Should().Be(4);
        var completeness = await client.IsCompleteAsync(
            new JupyterIsCompleteRequest("if (true) {"),
            cancellationToken);
        completeness.Status.Should().Be("incomplete");
        var inspection = await client.InspectAsync(
            new JupyterInspectRequest("console", 7),
            cancellationToken);
        inspection.Status.Should().Be("ok");

        var execution = await client.ExecuteAsync(
            new JupyterExecuteRequest("1 + 2"),
            cancellationToken);
        var outputs = await ReadOutputsAsync(execution, cancellationToken);
        var executionCompletion = await execution.Completion.WaitAsync(cancellationToken);

        executionCompletion.Reply.Status.Should().Be("ok");
        outputs.OfType<JupyterExecuteInputOutput>().Should()
            .Contain(output => output.Code == "1 + 2" && output.ExecutionCount > 0);
        outputs.OfType<JupyterExecuteResultOutput>()
            .Should().Contain(output => output.Data.Data.Values.Any(value => value.ToString().Contains('3')));
    }

    [Fact(Timeout = 30_000)]
    public async Task FreshLocalManagerRoutesFirstDenoPrompt()
    {
        using var deadline = CreateDeadline();
        var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, deadline.Token);
        await using var manager = await LocalJupyterKernelManager.StartAsync(
            spec,
            cancellationToken: deadline.Token);

        var execution = await manager.Client.ExecuteAsync(
            new JupyterExecuteRequest("prompt('First prompt: ')", AllowStdin: true),
            deadline.Token);
        var outputs = new List<JupyterOutput>();
        await foreach (var output in execution.Outputs.WithCancellation(deadline.Token))
        {
            outputs.Add(output);
            if (output is JupyterInputRequest input)
                await execution.ReplyInputAsync(input, "ready", deadline.Token);
        }

        (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
        outputs.OfType<JupyterInputRequest>().Should().ContainSingle()
            .Which.Prompt.Should().Be("First prompt: ");
        outputs.OfType<JupyterExecuteResultOutput>().Should().ContainSingle(output =>
            output.Data.Data.Values.Any(value => value.ToString().Contains("ready", StringComparison.Ordinal)));
    }

    [Fact(Timeout = 30_000)]
    public async Task RealDenoSeparatesStreamsDisplayResultAndError()
    {
        using var deadline = CreateDeadline();
        var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, deadline.Token);
        await using var manager = await LocalJupyterKernelManager.StartAsync(
            spec,
            cancellationToken: deadline.Token);

        var execution = await manager.Client.ExecuteAsync(
            new JupyterExecuteRequest(
                "console.log('stdout-marker'); " +
                "console.error('stderr-marker'); " +
                "await Deno.jupyter.display({ 'text/plain': 'display-marker' }, { raw: true }); " +
                "40 + 2"),
            deadline.Token);
        var outputs = await ReadOutputsAsync(execution, deadline.Token);
        (await execution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");

        outputs.OfType<JupyterStdout>().Should().Contain(output => output.Text.Contains("stdout-marker"));
        outputs.OfType<JupyterStderr>().Should().Contain(output => output.Text.Contains("stderr-marker"));
        outputs.OfType<JupyterDisplayOutput>().Should().Contain(output =>
            output.Data.Data["text/plain"].GetString() == "display-marker");
        outputs.OfType<JupyterExecuteResultOutput>().Should().Contain(output =>
            output.Data.Data.Values.Any(value => value.ToString().Contains("42", StringComparison.Ordinal)));

        var errorExecution = await manager.Client.ExecuteAsync(
            new JupyterExecuteRequest("throw new TypeError('error-marker')"),
            deadline.Token);
        var errorOutputs = await ReadOutputsAsync(errorExecution, deadline.Token);
        (await errorExecution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("error");
        errorOutputs.OfType<JupyterExecutionError>().Should().Contain(output =>
            output.Name == "TypeError" && output.Value.Contains("error-marker", StringComparison.Ordinal));

        var inputExecution = await manager.Client.ExecuteAsync(
            new JupyterExecuteRequest("prompt('Name: ')", AllowStdin: true),
            deadline.Token);
        var inputOutputs = new List<JupyterOutput>();
        await foreach (var output in inputExecution.Outputs.WithCancellation(deadline.Token))
        {
            inputOutputs.Add(output);
            if (output is JupyterInputRequest input) await inputExecution.ReplyInputAsync(input, "Ada", deadline.Token);
        }

        (await inputExecution.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");
        inputOutputs.OfType<JupyterInputRequest>().Should().ContainSingle().Which.Prompt.Should().Be("Name: ");
        inputOutputs.OfType<JupyterExecuteResultOutput>().Should().Contain(output =>
            output.Data.Data.Values.Any(value => value.ToString().Contains("Ada", StringComparison.Ordinal)));
    }

    [Fact(Timeout = 30_000)]
    public async Task RealDenoDisplayUpdatesAreTypedAndMalformedUpdatesDoNotDisconnect()
    {
        using var deadline = CreateDeadline();
        var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, deadline.Token);
        await using var manager = await LocalJupyterKernelManager.StartAsync(
            spec,
            cancellationToken: deadline.Token);

        var tracked = await manager.Client.ExecuteAsync(
            new JupyterExecuteRequest(
                "const displayId = 'tracked-display'; " +
                "await Deno.jupyter.display(" +
                "{ 'text/html': '<b>initial</b>', 'text/plain': 'initial' }, " +
                "{ raw: true, display_id: displayId }); " +
                "await Deno.jupyter.display(" +
                "{ 'text/html': '<b>updated</b>', 'text/plain': 'updated' }, " +
                "{ raw: true, display_id: displayId, update: true });"),
            deadline.Token);
        var trackedOutputs = await ReadOutputsAsync(tracked, deadline.Token);
        (await tracked.Completion.WaitAsync(deadline.Token)).Reply.Status.Should().Be("ok");

        var display = trackedOutputs.OfType<JupyterDisplayOutput>().Single(output =>
            output.Data.Data["text/plain"].GetString() == "initial");
        var update = trackedOutputs.OfType<JupyterDisplayUpdateOutput>().Single(output =>
            output.Data.Data["text/plain"].GetString() == "updated");
        display.Data.Data["text/html"].GetString().Should().Be("<b>initial</b>");
        update.Data.Data["text/html"].GetString().Should().Be("<b>updated</b>");
        update.DisplayId.Should().Be(display.DisplayId);

        var malformed = await manager.Client.ExecuteAsync(
            new JupyterExecuteRequest(
                "await Deno.jupyter.display(" +
                "{ 'text/plain': 'orphan update' }, { raw: true, update: true });"),
            deadline.Token);
        await ReadOutputsAsync(malformed, deadline.Token);
        await malformed.Completion.WaitAsync(deadline.Token);

        (await ExecuteTextResultAsync(manager.Client, "6 * 7", deadline.Token)).Should().Contain("42");
    }

    [Fact(Timeout = 45_000)]
    public async Task IndependentDenoManagersUseDistinctProcessesAndRestartClearsState()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(35));
        var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, deadline.Token);
        await using var first = await LocalJupyterKernelManager.StartAsync(
            spec,
            cancellationToken: deadline.Token);
        await using var second = await LocalJupyterKernelManager.StartAsync(
            spec,
            cancellationToken: deadline.Token);

        var firstPid = await ExecuteTextResultAsync(first.Client, "Deno.pid", deadline.Token);
        var secondPid = await ExecuteTextResultAsync(second.Client, "Deno.pid", deadline.Token);
        firstPid.Should().NotBe(secondPid);

        await ExecuteTextResultAsync(first.Client, "var replValue = 41; replValue", deadline.Token);
        (await ExecuteTextResultAsync(first.Client, "replValue + 1", deadline.Token)).Should().Contain("42");
        await first.RestartAsync(deadline.Token);
        (await ExecuteTextResultAsync(first.Client, "typeof replValue", deadline.Token)).Should()
            .Contain("undefined");
    }

    [Fact(Timeout = 60_000)]
    public async Task LocalManagerLifecycleAndConnectionFileCleanupAreRepeatable()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(45));
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), $"maieutics-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeDirectory);
        try
        {
            var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, deadline.Token);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                await using var manager = await LocalJupyterKernelManager.StartAsync(
                    spec,
                    new LocalJupyterKernelManagerOptions
                    {
                        RuntimeDirectory = runtimeDirectory,
                        StartupTimeout = TimeSpan.FromSeconds(10),
                        ShutdownTimeout = TimeSpan.FromSeconds(5)
                    },
                    deadline.Token);
                await manager.Client.PingAsync(deadline.Token);
                await manager.ShutdownAsync(deadline.Token);
                Directory.GetFiles(runtimeDirectory, "maieutics-kernel-*.json").Should().BeEmpty();
            }
        }
        finally
        {
            Directory.Delete(runtimeDirectory, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task LocalManagerReportsBoundedDiagnosticsWhenKernelExitsDuringStartup()
    {
        using var deadline = CreateDeadline();
        var spec = new JupyterKernelSpec(
            ["deno", "eval", "console.error('startup-marker-' + 'x'.repeat(20000)); Deno.exit(17)"],
            "Failing Deno",
            "typescript",
            "signal",
            new Dictionary<string, string>());

        var assertion = await (Spec: spec, Token: deadline.Token)
            .Awaiting(static state => LocalJupyterKernelManager.StartAsync(
                state.Spec,
                cancellationToken: state.Token))
            .Should().ThrowAsync<JupyterKernelStartupException>();

        assertion.Which.DisplayName.Should().Be("Failing Deno");
        assertion.Which.ExitCode.Should().Be(17);
        assertion.Which.TimedOut.Should().BeFalse();
        assertion.Which.StandardError.Should().StartWith("startup-marker-");
        assertion.Which.StandardError.Length.Should().BeLessThanOrEqualTo(8 * 1024);
    }

    [Fact(Timeout = 30_000)]
    public async Task LocalManagerAppliesWorkingDirectoryAndExplicitEnvironment()
    {
        using var deadline = CreateDeadline();
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"maieutics-deno-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        var inheritedName = $"MAIEUTICS_DENIED_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(inheritedName, "denied");
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                ["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? workingDirectory,
                ["TMPDIR"] = Path.GetTempPath(),
                ["MAIEUTICS_DENO_ALLOWED"] = "allowed"
            };
            foreach (var name in new[] { "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "APPDATA" })
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                    environment[name] = value;

            var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, deadline.Token);
            await using var manager = await LocalJupyterKernelManager.StartAsync(
                spec,
                new LocalJupyterKernelManagerOptions
                {
                    WorkingDirectory = workingDirectory,
                    ClearInheritedEnvironment = true,
                    Environment = environment
                },
                deadline.Token);
            var execution = await manager.Client.ExecuteAsync(
                new JupyterExecuteRequest(
                    "console.log(JSON.stringify({ cwd: Deno.cwd(), " +
                    "allowed: Deno.env.get('MAIEUTICS_DENO_ALLOWED'), " +
                    $"denied: Deno.env.get('{inheritedName}') }}))"),
                deadline.Token);
            var outputs = await ReadOutputsAsync(execution, deadline.Token);
            await execution.Completion.WaitAsync(deadline.Token);

            using var document = JsonDocument.Parse(outputs.OfType<JupyterStdout>().Single().Text);
            var json = document.RootElement;
            var actualWorkingDirectory = json.GetProperty("cwd").GetString();
            actualWorkingDirectory.Should().EndWith(Path.GetFileName(workingDirectory));
            Directory.Exists(actualWorkingDirectory).Should().BeTrue();
            json.GetProperty("allowed").GetString().Should().Be("allowed");
            json.TryGetProperty("denied", out _).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(inheritedName, null);
            Directory.Delete(workingDirectory, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ShutdownTimeoutKillsUnresponsiveKernelAndDeletesConnectionFile()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(20));
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), $"maieutics-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeDirectory);
        try
        {
            var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, deadline.Token);
            await using var manager = await LocalJupyterKernelManager.StartAsync(
                spec,
                new LocalJupyterKernelManagerOptions
                {
                    RuntimeDirectory = runtimeDirectory,
                    StartupTimeout = TimeSpan.FromSeconds(10),
                    ShutdownTimeout = TimeSpan.FromSeconds(1)
                },
                deadline.Token);
            var forceTimeout = !OperatingSystem.IsWindows();
            var execution = await manager.Client.ExecuteAsync(
                new JupyterExecuteRequest(forceTimeout
                    ? "console.log('stopping'); Deno.kill(Deno.pid, 'SIGSTOP');"
                    : "while (true) {}"),
                deadline.Token);
            await using var outputs = execution.Outputs.GetAsyncEnumerator(deadline.Token);
            while (await outputs.MoveNextAsync())
            {
                if (forceTimeout && outputs.Current is JupyterStdout { Text: var text } &&
                    text.Contains("stopping", StringComparison.Ordinal))
                    break;

                if (!forceTimeout &&
                    outputs.Current is JupyterExecutionStatusChanged { State: JupyterKernelState.Busy })
                    break;
            }

            var elapsed = Stopwatch.StartNew();
            await manager.ShutdownAsync(deadline.Token);
            elapsed.Stop();

            if (forceTimeout) elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(800));

            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
            Directory.GetFiles(runtimeDirectory, "maieutics-kernel-*.json").Should().BeEmpty();
            await execution.Completion.Invoking(static task => task).Should().ThrowAsync<Exception>();
        }
        finally
        {
            Directory.Delete(runtimeDirectory, true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TerminateImmediatelyKillsBusyKernelAndDeletesConnectionFile()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(20));
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), $"maieutics-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeDirectory);
        try
        {
            var spec = await JupyterKernelSpec.ReadAsync(DenoKernelSpecPath, deadline.Token);
            await using var manager = await LocalJupyterKernelManager.StartAsync(
                spec,
                new LocalJupyterKernelManagerOptions
                {
                    RuntimeDirectory = runtimeDirectory,
                    StartupTimeout = TimeSpan.FromSeconds(10),
                    ShutdownTimeout = TimeSpan.FromSeconds(5)
                },
                deadline.Token);
            var execution = await manager.Client.ExecuteAsync(
                new JupyterExecuteRequest("while (true) {}"),
                deadline.Token);
            await using var outputs = execution.Outputs.GetAsyncEnumerator(deadline.Token);
            while (await outputs.MoveNextAsync())
                if (outputs.Current is JupyterExecutionStatusChanged { State: JupyterKernelState.Busy })
                    break;

            var elapsed = Stopwatch.StartNew();
            await manager.TerminateAsync(deadline.Token);
            elapsed.Stop();

            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
            Directory.GetFiles(runtimeDirectory, "maieutics-kernel-*.json").Should().BeEmpty();
            await execution.Completion.Invoking(static task => task).Should().ThrowAsync<Exception>();
        }
        finally
        {
            Directory.Delete(runtimeDirectory, true);
        }
    }

    private static async Task<IReadOnlyList<JupyterOutput>> ReadOutputsAsync(
        IJupyterExecution execution,
        CancellationToken cancellationToken)
    {
        var outputs = new List<JupyterOutput>();
        await foreach (var output in execution.Outputs.WithCancellation(cancellationToken)) outputs.Add(output);

        return outputs;
    }

    private static async Task<string> ExecuteTextResultAsync(
        IJupyterClient client,
        string code,
        CancellationToken cancellationToken)
    {
        var execution = await client.ExecuteAsync(new JupyterExecuteRequest(code), cancellationToken);
        var outputs = await ReadOutputsAsync(execution, cancellationToken);
        (await execution.Completion.WaitAsync(cancellationToken)).Reply.Status.Should().Be("ok");
        var result = outputs.OfType<JupyterExecuteResultOutput>().Single();
        return result.Data.Data["text/plain"].GetString() ?? result.Data.Data["text/plain"].GetRawText();
    }

    private static CancellationTokenSource CreateDeadline(TimeSpan? timeout = null)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        return deadline;
    }
}
