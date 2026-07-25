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

## Model sources and profiles

```json
{
  "Maieutics": {
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
%maieutics model
%maieutics model list
%maieutics model current
%maieutics model use <profile>
%maieutics model reset
```

Control cells do not call a model and do not enter the Agent transcript. A manual selection lasts for the Kernel
lifetime while that profile exists. Configuration default changes affect sessions without an override; removing the
selected profile clears the override and falls back to the new default. Commands never display credentials or endpoints.

The Kernel provides Jupyter completion for the `%maieutics` command, model subcommands, and the currently configured
profile IDs accepted by `%maieutics model use <profile>`.

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
