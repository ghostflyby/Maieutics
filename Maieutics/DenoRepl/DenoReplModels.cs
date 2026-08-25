using System.Text.Json;

namespace Maieutics.DenoRepl;

internal sealed record DenoReplSessionResult(
    string SessionId,
    int Generation,
    string State,
    string Cwd,
    bool IsDefault);

internal sealed record DenoReplListResult(IReadOnlyList<DenoReplSessionResult> Sessions);

internal sealed record DenoReplCloseResult(string SessionId, bool Closed);

internal sealed record DenoReplExecutionResult(
    string SessionId,
    int Generation,
    int? ExecutionCount,
    string ExecutionStatus,
    IReadOnlyList<DenoReplOutputItem> Outputs,
    DenoReplPresentationResult Presentation,
    bool Truncated,
    int OmittedBytes);

internal sealed record DenoReplOutputItem(
    string Kind,
    string? Text = null,
    string? MediaType = null,
    JsonElement? Value = null,
    string? Name = null,
    IReadOnlyList<string>? Traceback = null,
    IReadOnlyList<string>? MediaTypes = null);

internal sealed record DenoReplPresentationResult(
    int DisplayCount,
    int UpdateCount,
    int ClearCount,
    int RateSkippedCount,
    int BundleSkippedCount,
    int DisplaySkippedCount,
    bool DigestTruncated,
    IReadOnlyList<DenoReplDisplayDigest> Digests)
{
    /// <summary>The total number of presentation items skipped, kept as the sum of the per-category
    /// counters: <see cref="RateSkippedCount"/> (display rate-limit drops), <see cref="BundleSkippedCount"/>
    /// (presentation bundle budget), and <see cref="DisplaySkippedCount"/> (every other presentation
    /// skip).</summary>
    public int SkippedCount => RateSkippedCount + BundleSkippedCount + DisplaySkippedCount;
}

/// <summary>A bounded model-side digest of one display or update, produced independently of the
/// full notebook presentation. The preview follows the MIME pick order (text/plain first, then
/// the first string <c>text/*</c> or <c>*+json</c> mime); binary mimes (image/*, application/pdf,
/// video/*, audio/*) contribute their mime key only, never a preview.</summary>
internal sealed record DenoReplDisplayDigest(
    IReadOnlyList<string> MediaTypes,
    string? Preview = null,
    string? DisplayId = null,
    bool IsUpdate = false);

internal enum DenoReplSessionState
{
    Created,
    Starting,
    Idle,
    Busy,
    Restarting,
    Faulted,
    Closing,
    Closed
}
