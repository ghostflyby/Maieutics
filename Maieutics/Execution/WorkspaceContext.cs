namespace Maieutics.Execution;

internal sealed class WorkspaceContext
{
    private readonly Lock gate = new();
    private readonly WorkspaceRoot startupRoot;
    private WorkspaceSnapshot current;

    internal WorkspaceContext(WorkspaceRoot startupRoot)
    {
        this.startupRoot = startupRoot ?? throw new ArgumentNullException(nameof(startupRoot));
        current = new WorkspaceSnapshot(startupRoot, Version: 0, HasSessionOverride: false);
    }

    internal WorkspaceSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return current;
        }
    }

    internal WorkspaceSnapshot Use(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        while (true)
        {
            var previous = GetSnapshot();
            var replacement = WorkspaceRoot.Create(path, previous.Root.Path);
            lock (gate)
            {
                if (current.Version != previous.Version)
                {
                    continue;
                }

                current = new WorkspaceSnapshot(
                    replacement,
                    checked(previous.Version + 1),
                    HasSessionOverride: true);
                return current;
            }
        }
    }

    internal WorkspaceSnapshot Reset()
    {
        lock (gate)
        {
            if (!current.HasSessionOverride)
            {
                return current;
            }

            current = new WorkspaceSnapshot(
                startupRoot,
                checked(current.Version + 1),
                HasSessionOverride: false);
            return current;
        }
    }
}

internal sealed record WorkspaceSnapshot(
    WorkspaceRoot Root,
    long Version,
    bool HasSessionOverride);