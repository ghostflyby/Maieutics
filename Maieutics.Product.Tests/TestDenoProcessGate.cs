using System.Runtime.CompilerServices;
using Maieutics.DenoExecution;

namespace Maieutics.Product.Tests;

/// <summary>Enables the suite-wide child-process budget once per test run, before any test
/// executes. Every child the suite starts — internal Deno children (plugin host
/// generations, REPL sessions, cache/helper children, registry probes) as well as
/// test-driven children such as real Deno probes and the published smoke executable —
/// acquires a slot from the same semaphore; the gate is inert in production. The budget
/// leaves one core for the test host and its GC while capping live children at three on a
/// four-core runner and two on the three-core macOS runner, which keeps the suite inside
/// the hosted runners' memory budget (see the Test job notes in ci.yml).</summary>
internal static class TestDenoProcessGate
{
    /// <summary>Live-child cap: the core count, floored at two and ceilinged at four. The
    /// ceiling is what bounds startup storms and inherited-heap memory; the floor keeps a
    /// two-core runner from serializing every Deno test. A test collection holds at most
    /// two children at once (a plugin host or REPL plus at most one helper), so at the
    /// three-collection parallel cap the gate admits the first four nested starts and
    /// queues the rest until a child exits — a throttle, never a deadlock, because every
    /// slot holder is itself progressing.</summary>
    internal static readonly int Limit = ComputeLimit(Environment.ProcessorCount);

    /// <summary>The budget for a given core count, split out so the mapping is directly
    /// testable without mutating the process-wide gate.</summary>
    internal static int ComputeLimit(int processorCount)
    {
        return Math.Clamp(processorCount, 2, 4);
    }

    [ModuleInitializer]
    internal static void Enable()
    {
        InternalDenoProcessGate.EnableForTests(Limit);
    }
}

/// <summary>Test-side front door to the shared child-process budget: a general child the
/// suite starts directly (real Deno probes, the published executable) joins the same
/// semaphore the production Deno paths use, so the cap covers the whole suite instead of
/// only internal Deno children. Callers dispose the lease when the child is no longer live;
/// the disabled-gate no-op lease makes this safe outside the test process.</summary>
internal static class TestChildProcessGate
{
    internal static Task<ChildSlotLease> AcquireAsync(CancellationToken cancellationToken)
    {
        return InternalDenoProcessGate.AcquireAsync(cancellationToken);
    }
}
