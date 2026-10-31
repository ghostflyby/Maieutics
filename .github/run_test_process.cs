#:property AllowUnsafeBlocks=true
#:property TreatWarningsAsErrors=true

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

return await TestProcess.MainAsync(args);

internal static class TestProcess
{
    internal static async Task<int> MainAsync(string[] arguments)
    {
        try
        {
            if (arguments is ["--self-test"]) return await SelfTestAsync();
            if (arguments is ["--probe", var probe, var groupFile]) return await ProbeAsync(probe, groupFile);
            if (arguments is ["--child", .. var command])
            {
                // This child becomes the group leader before starting the command. Its
                // pid remains the group's identity after the command or leader exits.
                if (Unix.GetGroup() != Environment.ProcessId && Unix.CreateSession() < 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(),
                        $"setsid failed for pid {Environment.ProcessId}, group {Unix.GetGroup()}");
                using var target = Start(Command(command));
                await target.WaitForExitAsync();
                return target.ExitCode;
            }
            return await SuperviseAsync(arguments);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Test supervisor failed: {exception.Message}");
            return exception is ArgumentException ? 2 : 1;
        }
    }

    private static async Task<int> SuperviseAsync(string[] arguments)
    {
        var seconds = 1200d;
        if (arguments is ["--timeout-seconds", var value, .. var rest])
        {
            if (!double.TryParse(value, CultureInfo.InvariantCulture, out seconds))
                throw new ArgumentException("Invalid timeout.");
            arguments = rest;
        }
        if (arguments is ["--", .. var command]) arguments = command;
        if (arguments.Length == 0 || !double.IsFinite(seconds) || seconds <= 0 || seconds > uint.MaxValue / 1000d)
            throw new ArgumentException("Usage: --timeout-seconds <positive seconds> -- <command> [arguments]");

        if (OperatingSystem.IsWindows()) WindowsJob.OwnProcess();
        using var interrupted = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; interrupted.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var terminate = OperatingSystem.IsWindows() ? null :
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                interrupted.Cancel();
            });
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(interrupted.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(seconds));
        Process? child = null;
        try
        {
            child = Start(OperatingSystem.IsWindows() ? Command(arguments) : Self(["--child", .. arguments]));
            await child.WaitForExitAsync(budget.Token);
            return child.ExitCode;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            Console.Error.WriteLine(interrupted.IsCancellationRequested
                ? "Test supervisor interrupted; terminating owned processes."
                : $"Test process exceeded {seconds.ToString(CultureInfo.InvariantCulture)}s; terminating owned processes.");
            return interrupted.IsCancellationRequested ? 130 : 124;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (child is not null)
            {
                try
                {
                    var groupStopped = !OperatingSystem.IsWindows() && Unix.KillGroup(child.Id);
                    if (!child.HasExited && !groupStopped) child.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await child.WaitForExitAsync(cleanup.Token);
                    // A deadline can beat the child entering setsid. After reaping it,
                    // retry group cleanup: the dead leader can no longer create a group.
                    if (!OperatingSystem.IsWindows() && !groupStopped) Unix.KillGroup(child.Id);
                }
                finally
                {
                    child.Dispose();
                }
            }
            // On Windows the OS closes this process's non-inheritable Job handle at
            // exit, also reclaiming descendants whose intermediate parent already exited.
        }
    }

    private static ProcessStartInfo Command(string[] command)
    {
        if (command.Length == 0) throw new ArgumentException("A command is required.");
        var start = new ProcessStartInfo(command[0]) { UseShellExecute = false };
        foreach (var argument in command.AsSpan(1)) start.ArgumentList.Add(argument);
        return start;
    }

    private static ProcessStartInfo Self(string[] arguments)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static Process Start(ProcessStartInfo start) => Process.Start(start)
        ?? throw new InvalidOperationException($"Could not start '{start.FileName}'.");

    private static async Task<int> SelfTestAsync()
    {
        await CheckAsync("exit", 17, 10);
        Console.WriteLine("PASS: preserves failure exit code");
        if (!OperatingSystem.IsWindows())
        {
            await CheckAsync("signal", 143, 10);
            Console.WriteLine("PASS: preserves signal exit status");
        }
        else Console.WriteLine("SKIP: POSIX signal exit status");
        await CheckAsync("hang", 124, 2);
        Console.WriteLine("PASS: enforces deadline and reaps child");
        await CheckAsync("spawn", 0, 10);
        Console.WriteLine("PASS: reclaims descendants after parent exit");
        return 0;
    }

    private static async Task CheckAsync(string probe, int expected, double seconds)
    {
        var directory = Directory.CreateTempSubdirectory("maieutics supervisor-").FullName;
        var groupFile = Path.Combine(directory, "group.pid");
        var command = Self(["--probe", probe, groupFile]);
        var start = Self(["--timeout-seconds", seconds.ToString(CultureInfo.InvariantCulture), "--",
            command.FileName, .. command.ArgumentList]);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var runner = Start(start);
        var stdout = runner.StandardOutput.ReadToEndAsync(budget.Token);
        var stderr = runner.StandardError.ReadToEndAsync(budget.Token);
        var drains = Task.WhenAll(stdout, stderr);
        try
        {
            await runner.WaitForExitAsync(budget.Token);
            // The spawned descendant inherits stdout. EOF is the strict successor of
            // its death even after its original parent has exited.
            var output = await drains;
            if (runner.ExitCode != expected)
                throw new InvalidOperationException($"{probe}: expected {expected}, got {runner.ExitCode}: {output[1]}");
            if (probe == "spawn" && output[0].Trim() != "spawned")
                throw new InvalidOperationException("The descendant probe did not run.");
            if (probe == "hang" && (!File.Exists(groupFile) || !output[1].Contains("exceeded", StringComparison.Ordinal)))
                throw new InvalidOperationException("The blocking probe did not start or the deadline was not reported.");
        }
        finally
        {
            // A failing regression must reclaim its probes independently of the
            // supervisor under test. Group publication is atomic and test-owned.
            try
            {
                if (!OperatingSystem.IsWindows() && (!runner.HasExited || !drains.IsCompletedSuccessfully) && File.Exists(groupFile))
                    Unix.KillGroup(int.Parse(await File.ReadAllTextAsync(groupFile), CultureInfo.InvariantCulture));
                if (!runner.HasExited) runner.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await runner.WaitForExitAsync(cleanup.Token);
            }
            finally
            {
                await budget.CancelAsync();
                try { await drains; }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<int> ProbeAsync(string probe, string groupFile)
    {
        if (groupFile != "-")
        {
            var group = OperatingSystem.IsWindows() ? Environment.ProcessId : Unix.GetGroup();
            await File.WriteAllTextAsync(groupFile + ".tmp", group.ToString(CultureInfo.InvariantCulture));
            File.Move(groupFile + ".tmp", groupFile, overwrite: true);
        }
        switch (probe)
        {
            case "exit": return 17;
            case "signal":
                if (Unix.SendSignal(Environment.ProcessId, 15) < 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 1;
            case "hang":
                // Deliberately ignores cancellation; only the external owner can stop it.
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 1;
            case "spawn":
                // The supervisor's group/Job owns this intentionally surviving child.
                using (Start(Self(["--probe", "hang", "-"]))) { }
                Console.WriteLine("spawned");
                return 0;
            default: throw new ArgumentException("Unknown probe.");
        }
    }
}

internal static partial class Unix
{
    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    internal static partial int CreateSession();

    [LibraryImport("libc", EntryPoint = "getpgrp")]
    internal static partial int GetGroup();

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static partial int SendSignal(int pid, int signal);

    internal static bool KillGroup(int group)
    {
        if (group <= 0 || group == GetGroup()) throw new ArgumentException("Refusing to kill an unowned group.");
        if (SendSignal(-group, 9) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == 3) return false; // ESRCH: already gone
        throw new Win32Exception(error, $"Could not terminate process group {group} (errno {error})");
    }
}

internal static partial class WindowsJob
{
    internal static void OwnProcess()
    {
        var job = CreateJobObject(0, 0);
        if (job == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, in limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
            !AssignProcessToJobObject(job, new nint(-1))) // current process pseudo-handle
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            CloseHandle(job);
            throw error;
        }
        // The process owns this raw, non-inheritable handle until OS teardown. Closing
        // it earlier would kill this supervisor too, since it is itself in the Job.
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses;
        internal nuint Affinity;
        internal uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial nint CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int kind, in ExtendedLimits information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
