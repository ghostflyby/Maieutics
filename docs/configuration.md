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

For example, `Maieutics__Model__Name` overrides `MAIEUTICS_MODEL`, while `--model` overrides both.

## Model provider

```json
{
  "Maieutics": {
    "Model": {
      "Provider": "OpenAI",
      "Name": "model-id"
    },
    "Providers": {
      "OpenAI": {
        "ApiFlavor": "Responses",
        "ApiKey": "",
        "Endpoint": null
      }
    }
  }
}
```

| Setting | Environment alias | Command line | Default |
|---|---|---|---|
| `Maieutics:Model:Provider` | `MAIEUTICS_PROVIDER` | `--provider` | `OpenAI` |
| `Maieutics:Model:Name` | `MAIEUTICS_MODEL` | `--model` | Required |
| `Maieutics:Providers:OpenAI:ApiKey` | `OPENAI_API_KEY` | - | Required for OpenAI |
| `Maieutics:Providers:OpenAI:Endpoint` | `OPENAI_BASE_URL` | - | OpenAI service endpoint |
| `Maieutics:Providers:OpenAI:ApiFlavor` | `MAIEUTICS_OPENAI_API` | `--openai-api` | `Responses` |

The current executable registers only the `OpenAI` provider factory. Supported OpenAI API flavors are `Responses` and
`ChatCompletions`. Both explicitly send `store: false`; the canonical transcript remains local and provider-neutral.

Credentials may be stored in a protected user configuration file, but environment variables or an external secret
injection mechanism are preferred. Environment variable changes require a process restart.

## Hot reload

The selected JSON file is monitored for changes. Each syntactically and semantically valid update becomes a new
immutable runtime configuration version. Invalid JSON, invalid limits, unsupported providers, and provider-client
construction failures are logged and rejected; the last valid version remains active. Repairing the file triggers a
normal later update.

Each Agent run captures one model client and options lease. Therefore these settings apply to the next turn and never
change during an active model/tool loop:

- provider, model, API flavor, endpoint, and API key;
- system prompt;
- Agent limits and event-buffer capacity.

Jupyter flush settings are captured at the beginning of each execution. `Jupyter:ConnectionFile` is captured once at
startup; changing it only logs that a restart is required.

When Provider settings change, Maieutics constructs the replacement client before publishing the new configuration.
The old client is retained until every active run lease has been released.

## Agent limits

| Setting | Default |
|---|---:|
| `Maieutics:Agent:MaxRetainedTurns` | `50` |
| `Maieutics:Agent:MaxHistoryCharacters` | `200000` |
| `Maieutics:Agent:MaxInputCharacters` | `32000` |
| `Maieutics:Agent:MaxResponseCharacters` | `64000` |
| `Maieutics:Agent:MaxModelIterationsPerTurn` | `8` |
| `Maieutics:Agent:MaxToolCallsPerTurn` | `16` |
| `Maieutics:Agent:MaxToolArgumentsBytes` | `65536` |
| `Maieutics:Agent:MaxToolResultBytes` | `262144` |
| `Maieutics:Agent:MaxToolProgressEventsPerCall` | `256` |
| `Maieutics:Agent:EventBufferCapacity` | `128` |

History limits are applied when the next successful turn commits. Tool argument and result sizes are measured as UTF-8
JSON bytes.

Example:

```bash
OPENAI_API_KEY=... \
maieutics \
  --connection-file /path/to/connection.json \
  --provider OpenAI \
  --model model-id \
  --openai-api Responses
```
