namespace Maieutics.Skills;

/// <summary>Counts the directories whose entries one or more discovery walks enumerated.
/// Optional plumbing so production callers ignore it while differential tests assert on
/// the delta: a rescan that touched only one skill directory increments by a handful,
/// a full-tree walk by one per directory of the tree.</summary>
internal sealed class SkillDirectoryVisitCounter
{
    private long count;

    /// <summary>Total directories probed so far. Thread-safe: separate root pumps may be
    /// walking concurrently against one catalog-owned counter.</summary>
    internal long Count => Interlocked.Read(ref count);

    internal void Increment() => Interlocked.Increment(ref count);
}
