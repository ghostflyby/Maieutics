namespace Maieutics.Persistence;

/// <summary>
///     Wires the SQLitePCLRaw provider before the first SQLite connection opens. Which
///     provider is compiled in is decided by <c>SqliteProviderMode</c> (see
///     Maieutics.csproj): System modes need an explicit SetProvider (their packages are not
///     self-initializing); the Bundled package self-initializes through its own module
///     initializer, and calling Batteries.Init() again is a documented no-op.
/// </summary>
internal static class SqliteProviderInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
#if SQLITE_PROVIDER_WINSQLITE3
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
#elif SQLITE_PROVIDER_SQLITE3
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries.Init();
#endif
    }
}
