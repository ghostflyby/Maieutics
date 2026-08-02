# Runtime Configuration

Maieutics uses one external `maieutics.json` file together with environment variables and command-line overrides. The
configuration file remains outside the NativeAOT executable so that an installed kernel and a portable deployment can
both be reconfigured without republishing the binary.

## Configuration file

The active file is selected once during process startup:

1. `--config <path>`
2. `MAIEUTICS_CONFIG`
3. `maieutics.json` beside the executable, when present
4. `ApplicationData/Maieutics/maieutics.json`

On macOS the user path is normally `~/Library/Application Support/Maieutics/maieutics.json`. Linux and Windows use the
platform path returned by `Environment.SpecialFolder.ApplicationData`.

Explicit relative paths are resolved from the startup working directory. An explicit file must exist and contain valid
JSON. The default user file is optional when environment variables and command-line arguments provide all required
values. Maieutics never reads `maieutics.json` implicitly from the notebook working directory and does not create or
rewrite a configuration file.

`Maieutics/maieutics.example.json` is a checked-in example, not an active published configuration.

## Precedence

Configuration sources override each other in this order:

```text
code defaults
< active JSON file
< shortcut environment variables
< standard .NET hierarchical environment variables
< command-line arguments
```

For example, `Maieutics__DefaultProfile` overrides `MAIEUTICS_PROFILE`, while `--profile` overrides both.

## Workspace tools

The executable registers three read-only tools: `list_directory`, `read_text`, and `search_text`. They operate on one
process-local workspace context whose startup root is selected when the process begins:

| Setting | Environment alias | Command line | Default |
|---|---|---|---|
| `Maieutics:Workspace:Root` | `MAIEUTICS_WORKSPACE` | `--workspace` | Startup working directory |

The startup root must be an existing directory and cannot itself be a symbolic link. Relative startup values are
resolved against the startup working directory. Configuration is not hot reloaded; changing the JSON setting requires
a process restart or an explicit session command.

Tools accept canonical `workspace://local/...` URIs rather than operating-system absolute paths. They reject path
traversal, `.git` metadata access, and symbolic-link traversal. Text reads and searches require regular UTF-8 files and
apply explicit line, result, file-count, directory-entry, byte, and regular-expression limits. Binary and large values
remain deferred to the artifact boundary.

## Deno REPL tools

The executable also registers `repl_execute`, `repl_create`, `repl_list`, `repl_restart`, and `repl_close`. The default
REPL starts lazily; each explicitly created REPL starts one independent local `deno jupyter` process. A REPL captures the
currently selected workspace root as its working directory when the session is created. Later workspace commands do not
move an existing process.

The configuration is captured once when the Maieutics host builder is created:

| Setting | Default |
|---|---:|
| `Maieutics:DenoRepl:Executable` | `deno` |
| `Maieutics:DenoRepl:MaxSessionsPerAgent` | `4` |
| `Maieutics:DenoRepl:StartupTimeout` | `00:00:30` |
| `Maieutics:DenoRepl:ExecutionTimeout` | `00:02:00` |
| `Maieutics:DenoRepl:InterruptGracePeriod` | `00:00:05` |
| `Maieutics:DenoRepl:ShutdownTimeout` | `00:00:10` |
| `Maieutics:DenoRepl:MaxModelOutputBytes` | `131072` |
| `Maieutics:DenoRepl:MaxPresentationTextBytes` | `1048576` |
| `Maieutics:DenoRepl:MaxPresentationEventsPerExecution` | `256` |
| `Maieutics:DenoRepl:MaxPresentationBundleBytes` | `16777216` |

`deno jupyter` is privileged local code execution, not an untrusted-code sandbox. The child receives an allowlisted
operational environment rather than the complete Maieutics environment; provider credentials such as
`OPENAI_API_KEY` and `ANTHROPIC_API_KEY` are not inherited. The first implementation has no worker, target, isolation,
pooling, retry, or automatic code-replay setting.

Jupyter output types define their audience. stdout and the final expression return only to the Agent; display/update/
clear output goes only to the notebook; stderr and execution errors go to both. Deno input requests are forwarded to
the notebook user. The model can intentionally publish user-visible output with the standard `Deno.jupyter.display`
API; Maieutics does not inject a proprietary Deno API.

Raw MIME bundles must use `{ raw: true }` and should include a `text/plain` fallback. Tracked updates use the same
snake-case `display_id` for the initial display and every replacement:

```ts
const displayId = crypto.randomUUID();
await Deno.jupyter.display(
  { "text/html": "<b>initial</b>", "text/plain": "initial" },
  { raw: true, display_id: displayId },
);
await Deno.jupyter.display(
  { "text/html": "<b>updated</b>", "text/plain": "updated" },
  { raw: true, display_id: displayId, update: true },
);
```

An `update_display_data` message without a usable `transient.display_id` is a non-critical malformed presentation
event. Maieutics preserves its execution order as a skipped output, continues draining the execution, and keeps the REPL
usable. Malformed completion, status, and input messages remain terminal protocol failures because execution cannot be
completed or interacted with safely without them.

## Model sources and profiles

```json
{
  "Maieutics": {
    "Workspace": {
      "Root": null
    },
    "DefaultProfile": "gpt",
    "Sources": {
      "openai": {
        "Provider": "OpenAI",
        "ApiFlavor": "Responses",
        "ApiKey": "",
        "Endpoint": null
      },
      "anthropic": {
        "Provider": "Anthropic",
        "ApiKey": "",
        "Endpoint": null
      }
    },
    "Profiles": {
      "gpt": {
        "Source": "openai",
        "Model": "openai-model-id"
      },
      "claude": {
        "Source": "anthropic",
        "Model": "anthropic-model-id"
      }
    }
  }
}
```

| Setting | Environment alias | Command line | Default |
|---|---|---|---|
| `Maieutics:DefaultProfile` | `MAIEUTICS_PROFILE` | `--profile` | Required |
| `Maieutics:Sources:openai:ApiKey` | `OPENAI_API_KEY` | - | Required for OpenAI |
| `Maieutics:Sources:openai:Endpoint` | `OPENAI_BASE_URL` | - | OpenAI service endpoint |
| `Maieutics:Sources:openai:ApiFlavor` | `MAIEUTICS_OPENAI_API` | `--openai-api` | `Responses` |
| `Maieutics:Sources:anthropic:ApiKey` | `ANTHROPIC_API_KEY` | - | Required for Anthropic |
| `Maieutics:Sources:anthropic:Endpoint` | `ANTHROPIC_BASE_URL` | - | Anthropic service endpoint |

Source and profile identifiers are case-insensitive and must match `[A-Za-z0-9][A-Za-z0-9_-]{0,63}`. A source owns
credentials, endpoint, API flavor, and connection resources. A profile selects one source and one provider model ID.
Multiple profiles may share a source.

The executable registers OpenAI and Anthropic factories. OpenAI supports `Responses` and `ChatCompletions`; both send
`store: false`. Anthropic uses the Messages API. Neither provider owns the canonical conversation history.

The legacy `Model` and `Providers:OpenAI` structure, together with `--provider`, `--model`, and `MAIEUTICS_MODEL`, is
accepted only when the named structure is completely absent. Mixing legacy and named model configuration is rejected.

Credentials may be stored in a protected user configuration file, but environment variables or an external secret
injection mechanism are preferred. Environment variable changes require a process restart.

## Hot reload

The selected JSON file is monitored for changes. Each syntactically and semantically valid update becomes a new
immutable runtime configuration version. Invalid JSON, invalid limits, unsupported providers, and provider-client
construction failures are logged and rejected; the last valid version remains active. Repairing the file triggers a
normal later update.

Each Agent run captures one profile generation and options lease. Therefore these settings apply to the next turn and never
change during an active model/tool loop:

- provider, model, API flavor, endpoint, and API key;
- system prompt;
- Agent limits and event-buffer capacity.

Jupyter flush settings are captured at the beginning of each execution. `Jupyter:ConnectionFile` is captured once at
startup; changing it only logs that a restart is required.

Every added or changed profile client is constructed before a candidate catalog is published. One construction failure
rejects the entire candidate. Unchanged profile generations are reused; removed or replaced clients remain alive until
their final active run lease has been released.

## Notebook model commands

The following control cells select the profile used by the next Agent run:

```text
%model
%model list
%model current
%model use <profile>
%model reset
```

Control cells do not call a model and do not enter the Agent transcript. A manual selection lasts for the Kernel
lifetime while that profile exists. Configuration default changes affect sessions without an override; removing the
selected profile clears the override and falls back to the new default. Commands never display credentials or endpoints.

The Kernel provides Jupyter completion for `%model` and `%workspace`, their subcommands, and the currently configured
profile IDs accepted by `%model use <profile>`. Typing a leading `/` (for example `/model`) offers the same canonical
`%` commands through completion; accepting a candidate replaces the slash token with the `%` form. Slash-prefixed text
that is not completed remains ordinary input and never executes as a command.

The legacy `%maieutics model ...` form remains accepted for existing notebooks and is deprecated.

## Notebook workspace commands

The following control cells inspect or change the workspace used by subsequent read-only tool calls:

```text
%workspace
%workspace current
%workspace use <path>
%workspace reset
```

`use` accepts an absolute path or a path relative to the current workspace, including unquoted spaces. The selected
directory must exist and cannot itself be a symbolic link. `reset` restores the startup root. The override lasts only
for the current Kernel process: it does not edit configuration, survive restart, call a model, or enter the Agent
transcript. Shell execution is serialized, so a command affects subsequent turns rather than an active turn.

Jupyter completion covers the workspace command and its `current`, `use`, and `reset` subcommands; filesystem paths are
not enumerated for completion. The legacy `%maieutics workspace ...` form remains accepted and is deprecated.

## Agent limits

| Setting | Default |
|---|---:|
| `Maieutics:Agent:MaxRetainedTurns` | `50` |
| `Maieutics:Agent:MaxHistoryBytes` | `400000` |
| `Maieutics:Agent:MaxInputCharacters` | `32000` |
| `Maieutics:Agent:MaxResponseCharacters` | `64000` |
| `Maieutics:Agent:MaxModelIterationsPerTurn` | `8` |
| `Maieutics:Agent:MaxToolCallsPerTurn` | `16` |
| `Maieutics:Agent:MaxToolArgumentsBytes` | `65536` |
| `Maieutics:Agent:MaxToolResultBytes` | `262144` |
| `Maieutics:Agent:MaxToolProgressEventsPerCall` | `256` |
| `Maieutics:Agent:EventBufferCapacity` | `128` |

History limits are applied when the next successful turn commits. `MaxHistoryBytes` measures the compact canonical
message JSON as UTF-8 and evicts complete turns. Tool argument and result sizes are also measured as UTF-8 JSON bytes.

During the configuration compatibility window, `Maieutics:Agent:MaxHistoryCharacters` is still accepted when
`MaxHistoryBytes` is absent and is converted to bytes as `value * 2`. Configuring both fields is invalid. Invalid reloads
retain the last-known-good runtime snapshot.

Example:

```bash
OPENAI_API_KEY=... \
maieutics \
  --connection-file /path/to/connection.json \
  --profile gpt
```
