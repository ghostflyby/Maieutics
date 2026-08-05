using System.Collections.Concurrent;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Maieutics.Configuration;

internal sealed record MaieuticsConfigurationFile(string? Path, bool Required, string Source)
{
    internal static MaieuticsConfigurationFile Resolve(
        IReadOnlyList<string> args,
        Func<string, string?> getEnvironmentVariable,
        string applicationBaseDirectory,
        string currentDirectory,
        string applicationDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var commandLinePath = GetCommandLinePath(args);
        if (commandLinePath is not null)
        {
            return new MaieuticsConfigurationFile(
                Normalize(commandLinePath, currentDirectory),
                Required: true,
                Source: "command line");
        }

        var environmentPath = getEnvironmentVariable("MAIEUTICS_CONFIG");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return new MaieuticsConfigurationFile(
                Normalize(environmentPath, currentDirectory),
                Required: true,
                Source: "MAIEUTICS_CONFIG");
        }

        var portablePath = System.IO.Path.Combine(applicationBaseDirectory, "maieutics.json");
        if (File.Exists(portablePath))
        {
            return new MaieuticsConfigurationFile(
                System.IO.Path.GetFullPath(portablePath),
                Required: true,
                Source: "portable");
        }

        if (string.IsNullOrWhiteSpace(applicationDataDirectory))
        {
            return new MaieuticsConfigurationFile(null, Required: false, Source: "none");
        }

        return new MaieuticsConfigurationFile(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(
                applicationDataDirectory,
                "Maieutics",
                "maieutics.json")),
            Required: false,
            Source: "user");
    }

    private static string? GetCommandLinePath(IReadOnlyList<string> args)
    {
        string? result = null;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            string? value = null;
            if (string.Equals(argument, "--config", StringComparison.Ordinal))
            {
                if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("The --config option requires a path.", nameof(args));
                }

                value = args[index];
            }
            else if (argument.StartsWith("--config=", StringComparison.Ordinal))
            {
                value = argument["--config=".Length..];
            }

            if (value is null)
            {
                continue;
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (result is not null)
            {
                throw new ArgumentException("The --config option may be specified only once.", nameof(args));
            }

            result = value;
        }

        return result;
    }

    private static string Normalize(string path, string currentDirectory) =>
        System.IO.Path.IsPathFullyQualified(path)
            ? System.IO.Path.GetFullPath(path)
            : System.IO.Path.GetFullPath(path, currentDirectory);
}

internal sealed class MaieuticsConfigurationFileProvider : IDisposable
{
    private readonly IDisposable? owner;
    private int disposed;

    private MaieuticsConfigurationFileProvider(IFileProvider provider, string relativePath, IDisposable? owner)
    {
        Provider = provider;
        this.owner = owner;
        RelativePath = relativePath;
    }

    internal IFileProvider Provider { get; }

    internal string RelativePath { get; }

    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    internal static MaieuticsConfigurationFileProvider Create(string? path)
    {
        if (path is null)
        {
            return new MaieuticsConfigurationFileProvider(
                new NullFileProvider(),
                "maieutics.json",
                owner: null);
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new ArgumentException("The configuration path has no parent directory.", nameof(path));
        var relativePath = Path.GetFileName(fullPath);
        while (!Directory.Exists(directory))
        {
            var parent = Directory.GetParent(directory)
                         ?? throw new DirectoryNotFoundException(
                             $"No existing parent directory was found for configuration path '{fullPath}'.");
            relativePath = Path.Combine(Path.GetFileName(directory), relativePath);
            directory = parent.FullName;
        }

        var provider = new PollingFileProvider(directory, TimeSpan.FromMilliseconds(250));
        return new MaieuticsConfigurationFileProvider(provider, relativePath, provider);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            owner?.Dispose();
        }
    }

    private sealed class PollingFileProvider : IFileProvider, IDisposable
    {
        private readonly PhysicalFileProvider inner;
        private readonly TimeSpan interval;
        private readonly ConcurrentDictionary<long, PollingChangeToken> tokens = new();
        private long nextTokenId;
        private int disposed;

        internal PollingFileProvider(string root, TimeSpan interval)
        {
            inner = new PhysicalFileProvider(root);
            this.interval = interval;
        }

        public IFileInfo GetFileInfo(string subpath) => inner.GetFileInfo(subpath);

        public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

        public IChangeToken Watch(string filter)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var id = Interlocked.Increment(ref nextTokenId);
            var fullPath = Path.Combine(inner.Root, filter);
            var token = new PollingChangeToken(fullPath, interval, () => tokens.TryRemove(id, out _));
            if (!tokens.TryAdd(id, token))
            {
                token.Dispose();
                throw new InvalidOperationException("Could not register a configuration file change token.");
            }

            return token;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            foreach (var token in tokens.Values)
            {
                token.Dispose();
            }

            tokens.Clear();
            inner.Dispose();
        }
    }

    private sealed class PollingChangeToken : IChangeToken, IDisposable
    {
        private readonly CancellationTokenSource changed = new();
        private readonly Action onChanged;
        private readonly FileStamp initialStamp;
        private readonly Timer timer;
        private int disposed;

        internal PollingChangeToken(string path, TimeSpan interval, Action onChanged)
        {
            this.onChanged = onChanged;
            initialStamp = FileStamp.Read(path);
            timer = new Timer(Poll, path, interval, interval);
        }

        public bool HasChanged => changed.IsCancellationRequested;

        public bool ActiveChangeCallbacks => true;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
            changed.Token.Register(callback, state);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            timer.Dispose();
            changed.Dispose();
        }

        private void Poll(object? state)
        {
            if (Volatile.Read(ref disposed) != 0 || state is not string path)
            {
                return;
            }

            if (FileStamp.Read(path) == initialStamp)
            {
                return;
            }

            try
            {
                timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                changed.Cancel();
                onChanged();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                Dispose();
            }
        }

        private readonly record struct FileStamp(bool Exists, long Length, long LastWriteTicks)
        {
            internal static FileStamp Read(string path)
            {
                try
                {
                    var file = new FileInfo(path);
                    file.Refresh();
                    return file.Exists
                        ? new FileStamp(true, file.Length, file.LastWriteTimeUtc.Ticks)
                        : default;
                }
                catch (IOException)
                {
                    return new FileStamp(true, -1, DateTime.UtcNow.Ticks);
                }
                catch (UnauthorizedAccessException)
                {
                    return new FileStamp(true, -1, DateTime.UtcNow.Ticks);
                }
            }
        }
    }
}

internal sealed class MaieuticsConfigurationFileErrors
{
    private readonly ConcurrentQueue<Exception> errors = new();
    private Action? signal;

    internal void Record(Exception exception)
    {
        errors.Enqueue(exception);
        Volatile.Read(ref signal)?.Invoke();
    }

    internal IDisposable RegisterSignal(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (Interlocked.CompareExchange(ref signal, callback, null) is not null)
        {
            throw new InvalidOperationException("A configuration error signal is already registered.");
        }

        return new SignalRegistration(this, callback);
    }

    internal Exception? TakeLatest()
    {
        Exception? latest = null;
        while (errors.TryDequeue(out var error))
        {
            latest = error;
        }

        return latest;
    }

    private sealed class SignalRegistration(MaieuticsConfigurationFileErrors owner, Action callback) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Interlocked.CompareExchange(ref owner.signal, null, callback);
            }
        }
    }
}