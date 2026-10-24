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
            return new MaieuticsConfigurationFile(
                Normalize(commandLinePath, currentDirectory),
                true,
                "command line");

        var environmentPath = getEnvironmentVariable("MAIEUTICS_CONFIG");
        if (!string.IsNullOrWhiteSpace(environmentPath))
            return new MaieuticsConfigurationFile(
                Normalize(environmentPath, currentDirectory),
                true,
                "MAIEUTICS_CONFIG");

        var portablePath = System.IO.Path.Combine(applicationBaseDirectory, "maieutics.json");
        if (File.Exists(portablePath))
            return new MaieuticsConfigurationFile(
                System.IO.Path.GetFullPath(portablePath),
                true,
                "portable");

        if (string.IsNullOrWhiteSpace(applicationDataDirectory))
            return new MaieuticsConfigurationFile(null, false, "none");

        return new MaieuticsConfigurationFile(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(
                applicationDataDirectory,
                "Maieutics",
                "maieutics.json")),
            false,
            "user");
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
                    throw new ArgumentException("The --config option requires a path.", nameof(args));

                value = args[index];
            }
            else if (argument.StartsWith("--config=", StringComparison.Ordinal))
            {
                value = argument["--config=".Length..];
            }

            if (value is null) continue;

            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (result is not null)
                throw new ArgumentException("The --config option may be specified only once.", nameof(args));

            result = value;
        }

        return result;
    }

    private static string Normalize(string path, string currentDirectory)
    {
        return System.IO.Path.IsPathFullyQualified(path)
            ? System.IO.Path.GetFullPath(path)
            : System.IO.Path.GetFullPath(path, currentDirectory);
    }
}

internal sealed class MaieuticsConfigurationFileProvider : IDisposable
{
    private readonly IDisposable? _owner;
    private int _disposed;

    private MaieuticsConfigurationFileProvider(IFileProvider provider, string relativePath, IDisposable? owner)
    {
        Provider = provider;
        this._owner = owner;
        RelativePath = relativePath;
    }

    internal IFileProvider Provider { get; }

    internal string RelativePath { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner?.Dispose();
    }

    internal static MaieuticsConfigurationFileProvider Create(string? path)
    {
        if (path is null)
            return new MaieuticsConfigurationFileProvider(
                new NullFileProvider(),
                "maieutics.json",
                null);

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

    private sealed class PollingFileProvider : IFileProvider, IDisposable
    {
        private readonly PhysicalFileProvider _inner;
        private readonly TimeSpan _interval;
        private readonly ConcurrentDictionary<long, PollingChangeToken> _tokens = new();
        private int _disposed;
        private long _nextTokenId;

        internal PollingFileProvider(string root, TimeSpan interval)
        {
            _inner = new PhysicalFileProvider(root);
            this._interval = interval;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            foreach (var token in _tokens.Values) token.Dispose();

            _tokens.Clear();
            _inner.Dispose();
        }

        public IFileInfo GetFileInfo(string subpath)
        {
            return _inner.GetFileInfo(subpath);
        }

        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            return _inner.GetDirectoryContents(subpath);
        }

        public IChangeToken Watch(string filter)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var id = Interlocked.Increment(ref _nextTokenId);
            var fullPath = Path.Combine(_inner.Root, filter);
            var token = new PollingChangeToken(fullPath, _interval, () => _tokens.TryRemove(id, out _));
            if (!_tokens.TryAdd(id, token))
            {
                token.Dispose();
                throw new InvalidOperationException("Could not register a configuration file change token.");
            }

            return token;
        }
    }

    private sealed class PollingChangeToken : IChangeToken, IDisposable
    {
        private readonly CancellationTokenSource _changed = new();
        private readonly FileStamp _initialStamp;
        private readonly TimeSpan _interval;
        private readonly Action _onChanged;
        private readonly Timer _timer;
        private int _disposed;

        internal PollingChangeToken(string path, TimeSpan interval, Action onChanged)
        {
            this._onChanged = onChanged;
            this._interval = interval;
            _initialStamp = FileStamp.Read(path);
            _timer = new Timer(Poll, path, interval, interval);
        }

        public bool HasChanged => _changed.IsCancellationRequested;

        public bool ActiveChangeCallbacks => true;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
        {
            try
            {
                return _changed.Token.Register(callback, state);
            }
            catch (ObjectDisposedException)
            {
                // The token fired and its cancellation source was released while a configuration
                // consumer re-registered. The change already happened, so report it immediately
                // instead of breaking the reload chain.
                callback(state);
                return NullDisposable.Instance;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _timer.Dispose();
            _changed.Dispose();
        }

        private void Poll(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0 || state is not string path) return;

            if (FileStamp.Read(path) == _initialStamp) return;

            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Exception? consumerFailure = null;
            try
            {
                _changed.Cancel();
            }
            catch (Exception exception)
            {
                consumerFailure = exception;
            }

            if (consumerFailure is not null)
            {
                // A configuration consumer can fail transiently while the watched file is being
                // replaced. Resume polling so the change is retried instead of silently lost.
                try
                {
                    _timer.Change(_interval, _interval);
                }
                catch (ObjectDisposedException)
                {
                    // The provider was disposed while the change was being delivered.
                }

                return;
            }

            _onChanged();
            Dispose();
        }

        private sealed class NullDisposable : IDisposable
        {
            internal static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }

        private readonly record struct FileStamp(bool Exists, long Length, long LastWriteTicks, ulong ContentHash)
        {
            internal static FileStamp Read(string path)
            {
                try
                {
                    var file = new FileInfo(path);
                    file.Refresh();
                    return file.Exists
                        ? new FileStamp(true, file.Length, file.LastWriteTimeUtc.Ticks, ContentHashOf(path))
                        : default;
                }
                catch (IOException)
                {
                    return new FileStamp(true, -1, DateTime.UtcNow.Ticks, 0);
                }
                catch (UnauthorizedAccessException)
                {
                    return new FileStamp(true, -1, DateTime.UtcNow.Ticks, 0);
                }
            }

            // The content hash closes the Windows-only gap where a rewrite keeps the metadata
            // stamp identical (NTFS coalesces rapid same-length writes or a scheduling race
            // lands the write before the token captured its initial stamp). Without it the
            // poller never observes a difference and the reload silently never fires.
            private static ulong ContentHashOf(string path)
            {
                const int maxHashedBytes = 64 * 1024;
                var buffer = new byte[maxHashedBytes];
                int read;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }

                const ulong offsetBasis = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                var hash = offsetBasis;
                for (var index = 0; index < read; index++)
                {
                    hash ^= buffer[index];
                    hash *= prime;
                }

                return hash;
            }
        }
    }
}

internal sealed class MaieuticsConfigurationFileErrors
{
    private readonly ConcurrentQueue<Exception> _errors = new();
    private Action? _signal;

    internal void Record(Exception exception)
    {
        _errors.Enqueue(exception);
        Volatile.Read(ref _signal)?.Invoke();
    }

    internal IDisposable RegisterSignal(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (Interlocked.CompareExchange(ref _signal, callback, null) is not null)
            throw new InvalidOperationException("A configuration error signal is already registered.");

        return new SignalRegistration(this, callback);
    }

    internal Exception? TakeLatest()
    {
        Exception? latest = null;
        while (_errors.TryDequeue(out var error)) latest = error;

        return latest;
    }

    private sealed class SignalRegistration(MaieuticsConfigurationFileErrors owner, Action callback) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.CompareExchange(ref owner._signal, null, callback);
        }
    }
}
