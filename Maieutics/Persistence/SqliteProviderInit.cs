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

    /// <summary>Guards initialization so a caller cannot proceed until the provider is
    ///     registered and the serialized default has been requested. Held only across those
    ///     two synchronous calls.</summary>
    private static readonly Lock initGate = new();

    private static bool initialized;

    /// <summary>Module load is the first opportunity, so a process whose composition opens a
    ///     store without touching this type explicitly is still covered.</summary>
    [ModuleInitializer]
    internal static void InitializeOnModuleLoad()
    {
        EnsureInitialized();
    }

    /// <summary>Registers the provider and requests SQLite's serialized default, once, and
    ///     waits for that work to finish before returning.
    ///     <para>
    ///     Called from module load and again from the store constructor, so correctness does not
    ///     depend on which type the runtime happened to touch first (a test host, for example,
    ///     can reach the Microsoft.Data.Sqlite API before this assembly's module initializer
    ///     runs).
    ///     </para>
    ///     <para>
    ///     A plain flag is not enough: setting it before the work ran would let a second caller
    ///     return and open a connection while the first is still inside
    ///     <see cref="SetProvider"/> or <c>sqlite3_config</c>. The connection would then carry
    ///     no mutex on the OS engine (the macOS SIGSEGV), and <c>sqlite3_config</c> is only
    ///     accepted while no connection is open, so losing that race can silently fail to
    ///     apply. The lock makes the flag mean "initialization completed", not "started".
    ///     </para>
    ///     <para>
    ///     Re-entrancy is safe: the critical section calls no code that re-enters this method,
    ///     so a concurrent in-flight module initializer cannot deadlock against it.
    ///     </para></summary>
    internal static void EnsureInitialized()
    {
        lock (initGate)
        {
            if (initialized) return;

            SetProvider();
            RequestSerializedDefault();
            // Set last: a caller that observes it true has a fully initialized provider.
            initialized = true;
        }
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
