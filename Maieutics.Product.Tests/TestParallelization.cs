using Xunit.Sdk;
using Xunit.v3;

// Parallel collections track the runner's core count, evaluated when the test assembly
// loads. A fixed cap below the core count only serialized independent work: the flakes
// that motivated an earlier cap were lost signal edges and a queue-version ordering race,
// and those are fixed at the source. The actual resource bound is the live-child budget in
// TestDenoProcessGate, sized from the same core count, so parallelism and process fan-out
// scale together instead of one masking the other.
[assembly: Maieutics.Product.Tests.ProcessorCountParallelization]

namespace Maieutics.Product.Tests;

/// <summary>Assembly-level parallelization: collection-parallel with the concurrency limit
/// taken from <see cref="Environment.ProcessorCount"/> at runtime. The built-in
/// <c>Parallelization</c> attribute requires a compile-time constant, which cannot follow
/// the runner's core count; this is the documented custom-attribute form of the same
/// setting.</summary>
internal sealed class ProcessorCountParallelizationAttribute : Attribute, IParallelizationAttribute
{
    /// <summary>Core count, so a larger runner runs more collections without retuning.</summary>
    public int? GetMaxThreads() => Environment.ProcessorCount;

    /// <summary>One collection per test class, the suite's existing model.</summary>
    public ParallelMode? GetMode() => ParallelMode.Collections;

    /// <summary>Conservative scheduling: test timing and timeouts are reliable only with
    /// this algorithm.</summary>
    public ParallelAlgorithm? GetAlgorithm() => ParallelAlgorithm.Conservative;
}
