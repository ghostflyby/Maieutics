using System.Runtime.InteropServices;
using FluentAssertions;
using Maieutics.Persistence;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Maieutics.Product.Tests;

/// <summary>
///     Guards the SQLite threading defaults this process relies on.
///     <para>
///     The engine selected on macOS and Windows is the operating system's. Apple's build is
///     <c>SQLITE_THREADSAFE=2</c> (multi-thread), in which SQLite makes no promise about a
///     connection used from two threads at once, while the bundled <c>e_sqlite3</c> used on
///     Linux is <c>SQLITE_THREADSAFE=1</c> (serialized). Microsoft.Data.Sqlite does not pass
///     <c>SQLITE_OPEN_FULLMUTEX</c>, so under a multi-thread engine its connections carry no
///     mutex at all and concurrent use corrupts SQLite's heap — observed as a native SIGSEGV
///     inside <c>sqlite3Prepare</c> rather than a clean error.
///     </para>
///     <para>
///     <see cref="SqliteProviderInit" /> therefore requests SQLite's serialized default at
///     module load. These tests pin the two properties that fix depends on: the request is
///     accepted on the running engine, and the connections this process opens actually carry
///     the mutex.
///     </para>
/// </summary>
public sealed class SqliteProviderInitTests
{
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_db_mutex", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr BundledDbMutex(IntPtr db);

    [DllImport("libsqlite3", EntryPoint = "sqlite3_db_mutex", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SystemDbMutex(IntPtr db);

    [Fact]
    public void ConnectionsOpenedByThisProcessCarryADatabaseMutex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "maieutics-sqlite-init-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "history.db");
        try
        {
            using var store = new SqliteTranscriptStore(databasePath);
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            connection.Open();

            var handle = ((sqlite3?)connection.Handle)?.DangerousGetHandle() ?? IntPtr.Zero;
            handle.Should().NotBe(IntPtr.Zero, "an open connection exposes its native handle");

            // Which native library the process bound decides where to look the symbol up.
            var mutex = OperatingSystem.IsWindows()
                ? IntPtr.Zero
                : OperatingSystem.IsMacOS()
                    ? SystemDbMutex(handle)
                    : BundledDbMutex(handle);

            if (OperatingSystem.IsWindows())
            {
                // winsqlite3 ships no exported sqlite3_db_mutex, so the mutex cannot be probed
                // here; the request in the initializer still applies to the engine.
                return;
            }

            mutex.Should().NotBe(
                IntPtr.Zero,
                "a connection without a mutex lets two threads corrupt SQLite's heap under a "
                + "multi-thread engine, which is what the serialized default prevents");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact(Timeout = 30_000)]
    public void ManyConnectionsAcrossThreadsDoNotCorruptTheEngine()
    {
        // The reported symptom was a native SIGSEGV from the engine itself, so this exercises
        // the engine from many threads. Each thread uses its OWN connection — the pattern the
        // store's per-instance lock produces — because Microsoft.Data.Sqlite's managed
        // connection object is not itself a concurrency-safe handle.
        // The store is the product's own entry point and performs this itself; doing it here
        // first keeps the test independent of which type the host touched first.
        SqliteProviderInit.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), "maieutics-sqlite-init-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var failures = new List<Exception>();
            var workers = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            {
                var path = Path.Combine(directory, $"history-{index}.db");
                try
                {
                    using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
                    connection.Open();
                    using var setup = connection.CreateCommand();
                    setup.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);";
                    setup.ExecuteNonQuery();

                    for (var i = 0; i < 200; i++)
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = "INSERT INTO t(v) VALUES ('x'); SELECT COUNT(*) FROM t;";
                        command.ExecuteScalar();
                    }
                }
                catch (Exception exception)
                {
                    lock (failures) failures.Add(exception);
                }
            }, cancellationToken)).ToArray();

            // Reaching this point is the assertion: the engine did not take the process down.
            FluentActions.Invoking(() => Task.WaitAll(workers, cancellationToken))
                .Should().NotThrow();

            failures.Should().OnlyContain(static exception => exception is SqliteException);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Initialization is a barrier, not a flag: a caller that returns from
    /// <see cref="SqliteProviderInit.EnsureInitialized" /> must find a fully initialized
    /// provider. Setting the flag before the work ran let a second caller proceed to open a
    /// connection while the first was still inside <c>SetProvider</c>/<c>sqlite3_config</c> —
    /// the latter is accepted only while no connection is open, so losing that race could
    /// silently leave connections without a mutex.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ConcurrentInitializationObserversAllSeeACompletedInitialization()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const int callers = 16;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;

        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == callers) ready.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            SqliteProviderInit.EnsureInitialized();

            // Every observer must find a usable provider: this connection would fail — or
            // carry no mutex — had the caller returned before initialization completed.
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version();";
            command.ExecuteScalar().Should().NotBeNull();
        }, cancellationToken)).ToArray();

        await ready.Task.WaitAsync(cancellationToken);
        gate.TrySetResult();
        await Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }
}
