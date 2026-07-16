using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Shared;
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

    [Fact(Timeout = 45_000)]
    public async Task GenericHostStartsRealKernelAndStopsAfterShutdown()
    {
        using var deadline = CreateDeadline(TimeSpan.FromSeconds(35));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var connectionFile = Path.Combine(Path.GetTempPath(), $"maieutics-host-{Guid.NewGuid():N}.json");
        await connection.WriteFileAsync(connectionFile, deadline.Token);
        using var started = StartHostProcess(connectionFile);
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

    private static StartedHostProcess StartHostProcess(string connectionFile)
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
            WorkingDirectory = Path.GetDirectoryName(executablePath ?? assemblyPath!)!,
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
        startInfo.Environment["OPENAI_API_KEY"] = "test-key";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";

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

    private static CancellationTokenSource CreateDeadline(TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }
}