using System.Runtime.CompilerServices;

namespace Maieutics.Persistence;

/// <summary>
///     Wires the SQLitePCLRaw provider and SQLite's threading default before the first
///     connection opens. Which provider is compiled in is decided by <c>SqliteProviderMode</c>
///     (see Maieutics.csproj): System modes need an explicit SetProvider (their packages are not
///     self-initializing); the Bundled package self-initializes through its own module
///     initializer, and calling Batteries.Init() again is a documented no-op.
/// </summary>
internal static class SqliteProviderInit
{
    /// <summary>SQLite's runtime default-threading-mode switch (<c>sqlite3_config</c> option
    /// <c>SQLITE_CONFIG_SERIALIZED</c>). Accepted only before any connection is open.</summary>
    private const int SqliteConfigSerialized = 3;

    private static int initialized;

    /// <summary>Module load is the first opportunity, so a process whose composition opens a
    ///     store without touching this type explicitly is still covered.</summary>
    [ModuleInitializer]
    internal static void InitializeOnModuleLoad()
    {
        EnsureInitialized();
    }

    /// <summary>Idempotently registers the provider and requests SQLite's serialized default.
    ///     Called from module load and again from the store constructor, so correctness does not
    ///     depend on which type the runtime happened to touch first (a test host, for example,
    ///     can reach the Microsoft.Data.Sqlite API before this assembly's module initializer
    ///     runs).</summary>
    internal static void EnsureInitialized()
    {
        // Interlocked rather than a plain flag: two stores can be constructed concurrently, and
        // SetProvider must not race.
        if (Interlocked.Exchange(ref initialized, 1) != 0) return;

        SetProvider();
        RequestSerializedDefault();
    }

    private static void SetProvider()
    {
#if SQLITE_PROVIDER_WINSQLITE3
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
#elif SQLITE_PROVIDER_SQLITE3
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries.Init();
#endif
    }

    /// <summary>Asks the OS SQLite to serialize connections by default.
    ///     <para>
    ///     The engine this project links on macOS and Windows is the operating system's, and
    ///     Apple's build is compiled with <c>SQLITE_THREADSAFE=2</c> (multi-thread) while the
    ///     bundled <c>e_sqlite3</c> used on Linux is <c>SQLITE_THREADSAFE=1</c> (serialized).
    ///     In multi-thread mode SQLite documents that no connection or statement may be used by
    ///     two threads at once, and the connections this provider opens carry no mutex at all —
    ///     Microsoft.Data.Sqlite does not pass <c>SQLITE_OPEN_FULLMUTEX</c>, so each connection
    ///     inherits that unsafe default. Concurrent use of one connection then corrupts SQLite's
    ///     heap instead of failing cleanly (observed as a native SIGSEGV inside
    ///     <c>sqlite3Prepare</c>).
    ///     </para>
    ///     <para>
    ///     This switch restores the serialized default for every connection the process opens,
    ///     so the same defensive guarantee the bundled engine provides applies here too. It is
    ///     a no-op on a <c>SQLITE_THREADSAFE=1</c> engine (the request is already satisfied) and
    ///     a documented error return, not a failure, on builds that freeze the mode
    ///     (<c>SQLITE_THREADSAFE=0</c>); neither case is fatal, so the result is deliberately
    ///     not surfaced.
    ///     </para></summary>
    private static void RequestSerializedDefault()
    {
        _ = SQLitePCL.raw.sqlite3_config(SqliteConfigSerialized);
    }
}
