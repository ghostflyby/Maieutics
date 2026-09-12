# Logging

The executable logs through `Microsoft.Extensions.Logging`. Console is the only production sink; a
file sink is opt-in for CI and local diagnostics. The console provider is dropped entirely for
supervised frontend children (their parent owns stdout) unless `MAIEUTICS_CONSOLE_LOG` is set.

## Levels

| Level | Meaning | Default |
|---|---|---|
| Trace | Per-frame / per-message detail | Off |
| Debug | Lifecycle transitions, child-process output lines, diagnostic decisions | Off in production; on for `Maieutics` in CI file logs |
| Information | Operator-relevant state changes | On (default minimum) |
| Warning | Recoverable anomalies: budget exhaustion, race fallbacks, unexpected-but-handled child exits | On |
| Error | Lost work or an aborted subsystem operation | On |

Safe defaults are registered before configuration: minimum `Information`, with `Microsoft` and
`System` capped at `Warning`. Any `Logging` section rule (file, environment, or command line)
overrides them.

## Categories

- One `ILogger<T>` per class; the category is the type's namespace-qualified name.
- MCP surfaces use `Maieutics.Mcp.{id}`.

## Child-process bridging

A deno child's stderr and stdout are never silently dropped: the owning subsystem (for example
`DenoRunProcess`) bridges each output line into its own logger at `Debug`. Raising
`Logging:LogLevel:Maieutics` to `Debug` therefore exposes child-process output.

## Configuration

| Variable | Effect |
|---|---|
| `MAIEUTICS_LOG_DIR` | Opt-in file sink. When set to a writable directory, every enabled record is appended to `maieutics-{pid}.log` in that directory (`[yyyy-MM-dd HH:mm:ss.fff LEVEL] category: message` plus `Exception.ToString()` on its own line). An unusable directory is reported on stderr and disables the sink. Primarily the CI/diagnostic channel; production stays console-only unless set. |
| `MAIEUTICS_CONSOLE_LOG` | `1`/`true` re-enables the console provider in a supervised frontend child. |
| `Logging__LogLevel__*` | Standard `Microsoft.Extensions.Logging` configuration binding (`Logging:LogLevel:{Category}`); environment variables use the double-underscore form and win over the file via the normal configuration layering. |

## CI

The `test` and `native-aot` jobs set `MAIEUTICS_LOG_DIR` to a job-local `test-logs/` directory,
raise `Logging__LogLevel__Maieutics` to `Debug`, and keep `Microsoft`/`System` at `Warning`. On
failure (and on scheduled runs) the directory is uploaded as a workflow artifact with a 14-day
retention; `if-no-files-found: ignore` keeps passing runs clean.

## Maieutics.Agent

`Maieutics.Agent` deliberately emits no log records: its typed event stream is the observable
surface for runs, turns, and tool activity. Hosts and adapters that compose the Agent log on its
behalf.
