# Runtime Configuration

Maieutics reads `appsettings.json`, environment variables, and command-line arguments through the .NET configuration
pipeline. Provider credentials must be supplied outside the checked-in JSON files.

## OpenAI

| Setting | Environment alias | Command line | Default |
|---|---|---|---|
| `Maieutics:Model` | `MAIEUTICS_MODEL` | `--model` | Required |
| `Maieutics:OpenAI:ApiKey` | `OPENAI_API_KEY` | - | Required |
| `Maieutics:OpenAI:Endpoint` | `OPENAI_BASE_URL` | - | OpenAI service endpoint |
| `Maieutics:OpenAI:ApiFlavor` | `MAIEUTICS_OPENAI_API` | `--openai-api` | `Responses` |

Supported API flavor values are `Responses` and `ChatCompletions`.

Both flavors explicitly send `store: false`. Maieutics reconstructs each request from its committed local transcript
and does not use Responses `previous_response_id` or Conversations as canonical session state. This does not disable
provider prompt caching.

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

Model iteration and tool limits are enforced by the Maieutics run owner even when the underlying framework uses a
different internal iteration convention. Tool argument and result sizes are measured as UTF-8 JSON bytes. The current
composition root registers no production tools; tests inject deterministic tools through the immutable tool registry.

Example:

```bash
OPENAI_API_KEY=... \
maieutics \
  --connection-file /path/to/connection.json \
  --model model-id \
  --openai-api Responses
```
