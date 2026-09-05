using Maieutics;

#if SQLite_PROVIDER_SYSTEM && SQLITE_PROVIDER_WINDOWS
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
#elif SQLite_PROVIDER_SYSTEM
// The OS sqlite3 provider is not self-initializing (unlike the bundle); wire it before
// any SQLite connection opens (transcript persistence lazily opens the family store).
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#endif
// Bundled: the bundle package self-initializes via its module initializer.

await MaieuticsHost.CreateApplication(args).RunAsync();
