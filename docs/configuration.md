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

Example:

```bash
OPENAI_API_KEY=... \
maieutics \
  --connection-file /path/to/connection.json \
  --model model-id \
  --openai-api Responses
```
