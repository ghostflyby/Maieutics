using System.Runtime.CompilerServices;
using Maieutics.DenoExecution;

namespace Maieutics.Product.Tests;

/// <summary>Enables the suite-wide child-process budget once per test run, before any test
/// executes. Every child the suite starts — internal Deno children (plugin host
/// generations, REPL sessions, cache/helper children, registry probes) as well as
/// test-driven children such as real Deno probes and the published smoke executable —
/// acquires a slot from the same semaphore; the gate is inert in production. The budget is
/// the runner's core count (parallel collections, see
/// <see cref="ProcessorCountParallelizationAttribute"/>) plus one: at most one long-lived
/// child per running collection stays unqueued, and a collection's second child (a cache or
/// discovery helper) is admitted by the spare slot instead of waiting for a peer
/// collection's host to exit. Anything beyond that queues, so live children still track the
/// machine instead of the collection count.</summary>
internal static class TestDenoProcessGate
{
    /// <summary>Live-child cap: cores plus one, floored at two. Parallelism is the core
    /// count and a collection holds at most two children at once (a plugin host or REPL
    /// plus at most one helper), so cores + 1 admits every concurrent long-lived child plus
    /// one helper while any further start queues — a throttle, never a deadlock, because
    /// every slot holder is itself progressing.</summary>
    internal static readonly int Limit = ComputeLimit(Environment.ProcessorCount);

    /// <summary>The budget for a given core count, split out so the mapping is directly
    /// testable without mutating the process-wide gate.</summary>
    internal static int ComputeLimit(int processorCount)
    {
        return Math.Max(2, processorCount + 1);
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
