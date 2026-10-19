using System.Runtime.CompilerServices;
using Maieutics.DenoExecution;

namespace Maieutics.Product.Tests;

/// <summary>Enables the internal Deno child concurrency gate once per test run, before
/// any test executes: live internal Deno children (plugin host generations, REPL
/// sessions, helper children) stay bounded to the machine's core count no matter how
/// many test classes run in parallel, so process startup storms on loaded CI runners
/// cannot eat the tests' timing budgets. The gate is inert in production — only this
/// assembly turns it on.</summary>
internal static class TestDenoProcessGate
{
    [ModuleInitializer]
    internal static void Enable()
    {
        InternalDenoProcessGate.EnableForTests(Math.Max(1, Environment.ProcessorCount));
    }
}
