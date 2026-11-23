using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Maieutics.Frontend;

/// <summary>
/// Executable-owned passive text projection. The socket snapshot and its subscription share
/// one lock; HTTP pages never move a client's stream watermark. Discarding this cache starts
/// a new epoch, rebuilt from the authoritative transcript.
/// </summary>
internal sealed class FrontendConversationView(string sessionId)
{
    private const int MaximumEntries = 100_000;
    private const int MaximumTextCharacters = 64_000;
    private const int MaximumRetainedCharacters = 4_000_000;
    private const string Truncation = "\n[此视图仅显示有界正文预览；完整记录请在 Notebook 中打开。]";
    private readonly Lock _gate = new();
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private readonly List<Row> _rows = [];
    private readonly Dictionary<string, Row> _byId = new(StringComparer.Ordinal);
    private readonly HashSet<Channel<FrontendViewFrame>> _subscribers = [];
    private long _revision;
    private int _retainedCharacters;
    private int _reservations;
    private string? _activeRunId;
    private bool _overflowed;

    internal bool CanEvict
    {
        get { lock (_gate) return _reservations == 0 && _activeRunId is null && _subscribers.Count == 0; }
    }

    internal void Reserve() { lock (_gate) _reservations++; }
    internal void Release() { lock (_gate) _reservations--; }

    internal void Seed(FrontendTranscript transcript)
    {
        lock (_gate)
        {
            foreach (var turn in transcript.Turns)
                for (var index = 0; index < turn.Messages.Count; index++)
                {
                    var message = turn.Messages[index];
                    var text = string.Join("\n", message.Parts.Where(part => part.Kind == "text").Select(part => part.Text));
                    if (text.Length > 0 || message.Parts.All(part => part.Kind is not ("tool_call" or "tool_result")))
                        PutLocked($"{turn.RunId}:stored:{index}", turn.RunId,
                            message.Role == "user" ? "input" : "message", text.Length > 0 ? text : Render(message), "sealed", [],
                            message.Role == "assistant" ? new FrontendViewPresentation(1, turn.Truncated ? "markdown" : "assistant") :
                            message.Role == "user" ? null : new FrontendViewPresentation(1, "markdown"));
                    foreach (var part in message.Parts)
                    {
                        var id = $"{turn.RunId}:tool:{part.CallId}";
                        if (part.Kind == "tool_call")
                            PutToolLocked(id, turn.RunId, new FrontendViewTool(part.CallId ?? "", part.Name ?? "tool", "ok", part.Value), []);
                        else if (part.Kind == "tool_result")
                            PutToolLocked(id, turn.RunId, ReadTool(id) with { Status = ToolStatus(part.Value), Result = part.Value }, []);
                    }
                }
            _publishedRevision = _revision;
        }
    }

    internal void Start(string runId, string text)
    {
        lock (_gate)
        {
            _activeRunId = runId;
            var operations = new List<FrontendViewOperation>();
            PutLocked($"{runId}:input", runId, "input", text, "sealed", operations);
            PublishLocked(operations);
        }
    }

    internal void Publish(FrontendEventFrame frame)
    {
        lock (_gate)
        {
            var run = frame.RunId ?? _activeRunId;
            if (run is null) return;
            var operations = new List<FrontendViewOperation>();
            var messageId = $"{run}:message:{frame.MessageId}";
            var toolId = $"{run}:tool:{frame.CallId}";
            switch (frame.Type)
            {
                case "text.delta":
                case "reasoning.delta":
                    if (frame.MessageId is null) break;
                    if (frame.Type == "reasoning.delta") messageId += ":summary";
                    var previous = _byId.GetValueOrDefault(messageId);
                    if (previous?.Truncated == true) break;
                    PutLocked(messageId, run, frame.Type == "reasoning.delta" ? "reasoning-summary" : "message",
                        (previous?.Text ?? "") + frame.Text, "open", operations,
                        new FrontendViewPresentation(1, "markdown"));
                    break;
                case "message.completed" when frame.AgentMessage is { } message:
                    // Tool calls/results have their own structured activity rows; avoid duplicating
                    // them as an empty assistant message on completion.
                    var text = string.Join("\n", message.Parts.Where(part => part.Kind == "text").Select(part => part.Text));
                    if (text.Length > 0 || _byId.ContainsKey(messageId))
                        PutLocked(messageId, run, "message", text, "sealed", operations, new FrontendViewPresentation(1, "markdown"));
                    break;
                case "tool.started":
                    PutToolLocked(toolId, run, new FrontendViewTool(frame.CallId ?? "", frame.Tool ?? "tool", "running", frame.Arguments), operations);
                    break;
                case "tool.finished":
                    var finished = ReadTool(toolId);
                    PutToolLocked(toolId, run, finished with { Status = ToolStatus(frame.Result), Result = frame.Result }, operations);
                    break;
                case "tool.progress" when frame.Content is { } content:
                    var tool = _byId.GetValueOrDefault(toolId);
                    if (tool?.Truncated == true) break;
                    var progress = ReadTool(toolId);
                    PutToolLocked(toolId, run, progress with { Progress = content.Text ?? content.Value?.ToString() }, operations);
                    break;
                case "run.failed":
                    PutLocked($"{run}:failure", run, "diagnostic", $"{frame.Code}: {frame.Message}", "sealed", operations);
                    goto case "run.completed";
                case "run.completed":
                    foreach (var row in _rows.Where(row => row.Entry.InteractionId == run &&
                        (row.Entry.State == "open" ||
                         frame.Type == "run.completed" && frame.Truncated is not true && row.Entry.Kind == "message" && !row.Truncated)).ToArray())
                    {
                        // Match Notebook provenance: failed, interrupted and truncated replies stay passive.
                        if (row.Entry.Kind == "message" && !row.Truncated && frame.Type == "run.completed" && frame.Truncated is not true)
                        {
                            PutLocked(row.Entry.Id, run, "message", row.Text, "sealed", operations, new FrontendViewPresentation(1, "assistant"));
                            continue;
                        }
                        if (row.Entry.Kind == "tool")
                        {
                            PutToolLocked(row.Entry.Id, run, ReadTool(row.Entry.Id) with { Status = "error" }, operations);
                            continue;
                        }
                        row.Entry = row.Entry with { State = "sealed" };
                        operations.Add(new FrontendViewOperation("upsert", row.Entry));
                    }
                    if (run == _activeRunId) _activeRunId = null;
                    break;
                case "repl.display":
                case "repl.updateDisplay":
                case "repl.error":
                    var displayText = frame.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data &&
                        data.TryGetProperty("text/plain", out var plain) && plain.ValueKind == System.Text.Json.JsonValueKind.String
                        ? plain.GetString() ?? "" : "[富内容请在 Notebook 中查看]";
                    PutLocked($"{run}:display:{frame.DisplayId ?? _rows.Count.ToString(CultureInfo.InvariantCulture)}",
                        run, frame.Type == "repl.error" ? "diagnostic" : "display", displayText, "sealed", operations,
                        frame.Data is { } bundle ? new FrontendViewPresentation(1, "mime", bundle.Clone()) : null);
                    break;
                case "repl.clear":
                    foreach (var display in _rows.Where(row => row.Entry.InteractionId == run && row.Entry.Kind == "display").ToArray())
                        PutLocked(display.Entry.Id, run, "unavailable", "输出已清除", "sealed", operations);
                    break;
            }
            if (operations.Count > 0 || frame.Type is "run.completed" or "run.failed") PublishLocked(operations);
        }
    }

    internal FrontendViewPage Page(int? offset, int limit, string? epoch)
    {
        lock (_gate)
        {
            ValidateEpoch(epoch);
            if (limit is < 1 or > 80 || offset < 0)
                throw new FrontendFailureException("invalid_request", "Page limit must be 1–80 and offset must be nonnegative.");
            return PageLocked(offset ?? Math.Max(0, _rows.Count - limit), limit);
        }
    }

    internal FrontendViewContent Content(string id, string revision, string epoch)
    {
        lock (_gate)
        {
            ValidateEpoch(epoch);
            if (!_byId.TryGetValue(id, out var row)) throw new FrontendFailureException("not_found", "No view block matches this id.");
            if (row.Entry.ContentRevision != revision)
                throw new FrontendFailureException("content_revision_gap", "The live block has advanced; read its latest index entry.");
            return new FrontendViewContent(id, revision, row.Text, row.Presentation);
        }
    }

    internal (FrontendViewFrame Snapshot, Channel<FrontendViewFrame> Channel) Subscribe()
    {
        lock (_gate)
        {
            if (_subscribers.Count >= 8) throw new FrontendFailureException("view_capacity", "This session already has eight conversation views.");
            var channel = Channel.CreateBounded<FrontendViewFrame>(new BoundedChannelOptions(256)
            {
                SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false
            });
            _subscribers.Add(channel);
            return (new FrontendViewFrame("view.snapshot", Page: PageLocked(Math.Max(0, _rows.Count - 80), 80), ActiveRunId: _activeRunId), channel);
        }
    }

    internal void Unsubscribe(Channel<FrontendViewFrame> channel)
    {
        lock (_gate) { _subscribers.Remove(channel); channel.Writer.TryComplete(); }
    }

    private void ValidateEpoch(string? epoch)
    {
        if (epoch is not null && epoch != _epoch) throw new FrontendFailureException("epoch_changed", "The view cache was rebuilt; reconnect the view.");
    }

    private FrontendViewPage PageLocked(int offset, int limit) => new(sessionId, _epoch, Revision(_revision),
        _rows.Count, _rows.Skip(offset).Take(limit).Select(row => row.Entry).ToArray());

    private void PutLocked(string id, string run, string kind, string text, string state, List<FrontendViewOperation> operations, FrontendViewPresentation? presentation = null)
    {
        var row = _byId.GetValueOrDefault(id);
        if (row is null && _rows.Count >= MaximumEntries - 1)
        {
            if (_overflowed) return;
            _overflowed = true;
            id = "view:capacity"; run = "view"; kind = "unavailable";
            text = "此视图已达到索引容量；请在 Notebook 中打开完整历史。"; state = "sealed"; presentation = null;
        }
        var available = MaximumRetainedCharacters - _retainedCharacters + (row?.Size ?? 0);
        var presentationSize = presentation?.Value?.GetRawText().Length ?? 0;
        var presentationTruncated = presentationSize > MaximumTextCharacters || presentationSize + text.Length > available;
        if (presentationTruncated)
        { presentation = null; presentationSize = 0; text = string.Concat(text.AsSpan(0, Math.Min(text.Length, MaximumTextCharacters - Truncation.Length)), Truncation); }
        var budget = Math.Max(Truncation.Length, available);
        var limit = Math.Min(MaximumTextCharacters, budget);
        var truncated = presentationTruncated || text.Length > limit;
        if (text.Length > limit) { text = string.Concat(text.AsSpan(0, limit - Truncation.Length), Truncation); presentation = null; presentationSize = 0; }
        // Once the shared budget is exhausted, only a fixed-size unavailable marker is admitted.
        if (_retainedCharacters + text.Length + presentationSize - (row?.Size ?? 0) > MaximumRetainedCharacters)
        { text = ""; kind = "unavailable"; truncated = true; presentation = null; presentationSize = 0; }
        var contentRevision = Revision(++_revision);
        var entry = new FrontendViewEntry(id, row?.Entry.Ordinal ?? _rows.Count, run, kind,
            contentRevision, kind == "unavailable" && text.Length == 0 ? "正文超出视图缓存容量；请在 Notebook 中查看。" : text[..Math.Min(text.Length, 240)], state);
        _retainedCharacters += text.Length + presentationSize - (row?.Size ?? 0);
        if (row is null) { row = new Row(entry, text, truncated, presentation, text.Length + presentationSize); _rows.Add(row); _byId.Add(id, row); }
        else { row.Entry = entry; row.Text = text; row.Truncated = truncated; row.Presentation = presentation; row.Size = text.Length + presentationSize; }
        operations.Add(new FrontendViewOperation("upsert", entry));
    }

    private static string ToolStatus(JsonElement? result) =>
        result is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.String && status.GetString() == "error" ? "error" : "ok";

    private FrontendViewTool ReadTool(string id) =>
        _byId.GetValueOrDefault(id)?.Presentation?.Value?.Deserialize(FrontendJsonContext.Default.FrontendViewTool)
        ?? new FrontendViewTool(id, "tool", "running");

    private void PutToolLocked(string id, string run, FrontendViewTool tool, List<FrontendViewOperation> operations)
    {
        // A truncated row has discarded its structured identity; only seal its retained preview.
        if (_byId.TryGetValue(id, out var row) && row.Truncated)
        {
            if (tool.Status != "running" && row.Entry.State == "open")
            {
                row.Entry = row.Entry with { State = "sealed" };
                operations.Add(new FrontendViewOperation("upsert", row.Entry));
            }
            return;
        }
        PutLocked(id, run, "tool", $"{tool.Tool}\n{tool.Arguments}\n{tool.Progress}\n{tool.Result}",
            tool.Status == "running" ? "open" : "sealed", operations,
            new FrontendViewPresentation(1, "tool", JsonSerializer.SerializeToElement(tool, FrontendJsonContext.Default.FrontendViewTool)));
    }

    private long _publishedRevision;
    private void PublishLocked(List<FrontendViewOperation> operations)
    {
        // Empty transactions still deliver busy/idle state under a distinct watermark.
        if (_revision == _publishedRevision) _revision++;
        var change = new FrontendViewChange(_epoch, Revision(_publishedRevision), Revision(_revision), _rows.Count, operations.ToArray());
        _publishedRevision = _revision;
        var frame = new FrontendViewFrame("view.change", Change: change, ActiveRunId: _activeRunId);
        foreach (var channel in _subscribers.ToArray())
            if (!channel.Writer.TryWrite(frame))
            {
                channel.Writer.TryComplete(new FrontendFailureException("view_backpressure", "Reconnect to obtain an atomic view snapshot."));
                _subscribers.Remove(channel);
            }
    }

    private static string Revision(long revision) => revision.ToString(CultureInfo.InvariantCulture);

    private static string Render(FrontendMessage message)
    {
        var text = new StringBuilder();
        foreach (var part in message.Parts)
        {
            var value = part.Kind switch
            {
                "text" => part.Text,
                "tool_call" => $"{part.Name} ({part.CallId})\n{part.Value}",
                "tool_result" => $"工具结果 ({part.CallId})\n{part.Value}",
                _ => $"[{part.Kind}]"
            };
            if (text.Length > 0) text.AppendLine();
            if (value is not null)
            {
                text.Append(value.AsSpan(0, Math.Min(value.Length, MaximumTextCharacters)));
                if (value.Length > MaximumTextCharacters) text.Append(Truncation);
            }
            if (text.Length > MaximumTextCharacters) break;
        }
        return text.ToString();
    }

    private sealed class Row(FrontendViewEntry entry, string text, bool truncated, FrontendViewPresentation? presentation, int size)
    {
        internal FrontendViewEntry Entry = entry;
        internal string Text = text;
        internal bool Truncated = truncated;
        internal FrontendViewPresentation? Presentation = presentation;
        internal int Size = size;
    }
}
