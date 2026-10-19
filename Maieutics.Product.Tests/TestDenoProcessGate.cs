using System.Runtime.CompilerServices;
using Maieutics.DenoExecution;

namespace Maieutics.Product.Tests;

/// <summary>Enables the internal Deno child concurrency gate once per test run, before
/// any test executes: live internal Deno children (plugin host generations, REPL
/// sessions, helper children) stay bounded to the machine's core count no matter how
/// many test classes run in parallel, so process startup storms on loaded CI runners
/// cannot eat the tests' timing budgets. The gate is inert in production — only this
/// assembly turns it on.
///
/// The initializer also raises the thread-pool minimum to 64: several configuration
/// tests deliberately block pool threads with synchronous fault-injection gates, and
/// with the default minimum (= core count) those blocks starve the pool — FileSystemWatcher
/// callbacks and continuations of unrelated tests then sit in the global queue for tens
/// of seconds on 4-core CI runners, which is the mechanism behind the rotating test
/// timeouts. A high minimum means every blocked thread is replaced immediately instead
/// of trickling in at the injection rate.</summary>
internal static class TestProcessGateAndThreadPool
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        InternalDenoProcessGate.EnableForTests(Math.Max(1, Environment.ProcessorCount));
        ThreadPool.SetMinThreads(64, 64);
    }
}
