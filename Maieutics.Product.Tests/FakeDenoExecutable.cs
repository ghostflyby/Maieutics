namespace Maieutics.Product.Tests;

/// <summary>A fake <c>deno</c> executable for manager tests that must not launch a real
/// host: on Unix an immediate-exit shell script, on Windows <c>cmd.exe</c> (whose
/// <c>run --config=...</c> invocation prints an error and exits at once; it is resolved
/// through PATH and is always present, unlike <c>color.exe</c>/<c>where.exe</c>).
///
/// One immutable script is shared by every test in the process and removed on process
/// exit, so the former per-test copies no longer leak a temp file per manager and no test
/// observes a different executable than its neighbour.</summary>
internal static class FakeDenoExecutable
{
    private static readonly Lazy<string> SharedPath = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The executable path to pass as <c>DenoReplOptions.Executable</c>.</summary>
    internal static string Path => SharedPath.Value;

    private static string Create()
    {
        if (OperatingSystem.IsWindows()) return "cmd.exe";

        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"mc-fake-deno-{Environment.ProcessId}.sh");
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Process teardown: a leftover script is harmless and must not throw.
            }
        };
        return path;
    }
}
