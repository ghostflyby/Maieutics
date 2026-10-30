namespace Maieutics.Persistence;

/// <summary>Display metadata ownership, independent of the authoritative transcript.</summary>
internal sealed record SessionTitleState(string? Title, bool Manual, long Revision,
    string? Mode = null, long ControlRevision = 0, string Initial = "eligible", string Source = "preview");
