using Xunit.Sdk;
using Xunit.v3;

// Three concurrent test collections, independent of the runner's core count. The suite's
// collection count is not the constraint; live child processes and memory are. Each
// full-host collection starts a plugin host plus a module-graph warmer, so the default
// ProcessorCount cap (four on the ubuntu/windows runners) let the suite reach eight or
// more children at once — the memory-reclaim and OOM profile documented in ci.yml. Three
// collections pair with the process budget in TestDenoProcessGate (cores minus one,
// capped at three) so a fourth collection queues instead of oversubscribing the runner.
[assembly: Parallelization(Mode = ParallelMode.Collections, MaxThreads = 3)]
