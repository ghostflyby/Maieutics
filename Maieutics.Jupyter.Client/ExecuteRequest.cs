using System.Text.Json.Nodes;

namespace Maieutics.Jupyter.Client;

public sealed record ExecuteRequest(
    string Code,
    bool Silent = false,
    bool StoreHistory = true,
    bool AllowStdin = false,
    bool StopOnError = true)
{
    internal JsonObject ToContent()
    {
        return new JsonObject
        {
            ["code"] = Code,
            ["silent"] = Silent,
            ["store_history"] = StoreHistory,
            ["user_expressions"] = new JsonObject(),
            ["allow_stdin"] = AllowStdin,
            ["stop_on_error"] = StopOnError
        };
    }
}