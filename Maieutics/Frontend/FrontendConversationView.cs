using System.Globalization;
using System.Text;
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
                    PutLocked($"{turn.RunId}:stored:{index}", turn.RunId,
                        message.Role == "user" ? "input" : "message", Render(message), "sealed", []);
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
                        (previous?.Text ?? "") + frame.Text, "open", operations);
                    break;
                case "message.completed" when frame.AgentMessage is { } message:
                    // Tool calls/results have their own structured activity rows; avoid duplicating
                    // them as an empty assistant message on completion.
                    var text = string.Join("\n", message.Parts.Where(part => part.Kind == "text").Select(part => part.Text));
                    if (text.Length > 0 || _byId.ContainsKey(messageId))
                        PutLocked(messageId, run, "message", text, "sealed", operations);
                    break;
                case "tool.started":
                    PutLocked(toolId, run, "tool", $"{frame.Tool}\n{frame.Arguments}", "open", operations);
                    break;
                case "tool.finished":
                    PutLocked(toolId, run, "tool", $"{_byId.GetValueOrDefault(toolId)?.Text}\n{frame.Result}", "sealed", operations);
                    break;
                case "tool.progress" when frame.Content is { } content:
                    var tool = _byId.GetValueOrDefault(toolId);
                    if (tool?.Truncated == true) break;
                    PutLocked(toolId, run, "tool", $"{tool?.Text}\n{content.Text ?? content.Value?.ToString()}", "open", operations);
                    break;
                case "run.failed":
                    PutLocked($"{run}:failure", run, "diagnostic", $"{frame.Code}: {frame.Message}", "sealed", operations);
                    goto case "run.completed";
                case "run.completed":
                    foreach (var row in _rows.Where(row => row.Entry.State == "open" && row.Entry.InteractionId == run))
                    {
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
                        run, frame.Type == "repl.error" ? "diagnostic" : "display", displayText, "sealed", operations);
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
            return new FrontendViewContent(id, revision, row.Text);
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

    private void PutLocked(string id, string run, string kind, string text, string state, List<FrontendViewOperation> operations)
    {
        var row = _byId.GetValueOrDefault(id);
        if (row is null && _rows.Count >= MaximumEntries - 1)
        {
            if (_overflowed) return;
            _overflowed = true;
            id = "view:capacity"; run = "view"; kind = "unavailable";
            text = "此视图已达到索引容量；请在 Notebook 中打开完整历史。"; state = "sealed";
        }
        var available = MaximumRetainedCharacters - _retainedCharacters + (row?.Text.Length ?? 0);
        var budget = Math.Max(Truncation.Length, available);
        var limit = Math.Min(MaximumTextCharacters, budget);
        var truncated = text.Length > limit;
        if (truncated) text = string.Concat(text.AsSpan(0, limit - Truncation.Length), Truncation);
        // Once the shared budget is exhausted, only a fixed-size unavailable marker is admitted.
        if (_retainedCharacters + text.Length - (row?.Text.Length ?? 0) > MaximumRetainedCharacters)
        { text = ""; kind = "unavailable"; truncated = true; }
        var contentRevision = Revision(++_revision);
        var entry = new FrontendViewEntry(id, row?.Entry.Ordinal ?? _rows.Count, run, kind,
            contentRevision, kind == "unavailable" && text.Length == 0 ? "正文超出视图缓存容量；请在 Notebook 中查看。" : text[..Math.Min(text.Length, 240)], state);
        _retainedCharacters += text.Length - (row?.Text.Length ?? 0);
        if (row is null) { row = new Row(entry, text, truncated); _rows.Add(row); _byId.Add(id, row); }
        else { row.Entry = entry; row.Text = text; row.Truncated = truncated; }
        operations.Add(new FrontendViewOperation("upsert", entry));
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

    private sealed class Row(FrontendViewEntry entry, string text, bool truncated)
    {
        internal FrontendViewEntry Entry = entry;
        internal string Text = text;
        internal bool Truncated = truncated;
    }
}
