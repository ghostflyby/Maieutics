using System.Security.Cryptography;
using Maieutics.Agent;

namespace Maieutics.Persistence;

/// <summary>
///     Content-addressed object store under <c>objects/</c>: one immutable file per unique byte
///     sequence, named by its lowercase SHA-256 inside a two-hex-character fan-out directory.
///     Publication follows the ADR 0009 protocol: one streaming pass hashes and writes a
///     staging temporary, the file is flushed to disk, and the temporary is atomically renamed
///     into place (the staging area shares the objects volume so the rename cannot degrade into
///     a copy). Durability follows the plugin-store precedent (ADR 0022): the file contents are
///     fsynced before publication, but the directory entry added by the rename is not flushed
///     separately, so a power loss can lose the entry while the complete bytes survive on disk.
///     The loss is detectable and unambiguous — the metadata layer never references an object
///     before publication, and a missing object is a typed error. An identical object already
///     in the store is a CAS hit: the temporary is dropped and the existing bytes are shared.
///     The write path never rewrites or deletes published objects; a crash may leave staging
///     temporaries, which <see cref="SweepStaging" /> reclaims.
/// </summary>
internal sealed class ObjectStore : IAgentObjectStore, IObjectReclaimer
{
    private readonly string objectsRoot;
    private readonly string stagingRoot;

    public ObjectStore(string objectsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectsRoot);
        this.objectsRoot = objectsRoot;
        stagingRoot = Path.Combine(objectsRoot, ".staging");
        Directory.CreateDirectory(objectsRoot);
        Directory.CreateDirectory(stagingRoot);
    }

    /// <summary>One published object: its content address and byte size.</summary>
    public sealed record IngestedObject(string Sha256, long Size);

    /// <summary>Computes the store path of an object id.</summary>
    internal static string ObjectPath(string objectsRoot, string sha256)
    {
        ValidateId(sha256);
        return Path.Combine(objectsRoot, sha256[..2], sha256);
    }

    /// <summary>Ingests a stream: hashes while writing, then publishes atomically. The stream is
    /// not disposed by the store; the caller owns it.</summary>
    public IngestedObject Ingest(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var tempPath = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.tmp");
        try
        {
            string sha256;
            long size;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var target = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20,
                FileOptions.SequentialScan))
            {
                var buffer = new byte[1 << 20];
                size = 0;
                int read;
                while ((read = content.Read(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                    size += read;
                }

                target.Flush(flushToDisk: true);
                sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            var finalPath = ObjectPath(objectsRoot, sha256);
            var fanout = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrEmpty(fanout))
                throw new InvalidOperationException("Cannot resolve the object fan-out directory.");
            Directory.CreateDirectory(fanout);

            if (File.Exists(finalPath))
            {
                // CAS hit: the identical bytes are already published; discard the temporary.
                File.Delete(tempPath);
            }
            else
            {
                // Same-volume rename: atomic publication. The directory entry is not fsynced
                // separately (see the type remarks for the accepted power-loss window).
                File.Move(tempPath, finalPath);
            }

            return new IngestedObject(sha256, size);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Opens a published object for reading.</summary>
    /// <exception cref="FileNotFoundException">The object is not present in the store.</exception>
    public Stream Open(string sha256)
    {
        ValidateId(sha256);
        var path = ObjectPath(objectsRoot, sha256);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Object '{sha256}' is not present in the store.", path);

        return new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
    }

    /// <summary>Returns whether the object is present.</summary>
    public bool Exists(string sha256)
    {
        ValidateId(sha256);
        return File.Exists(ObjectPath(objectsRoot, sha256));
    }

    /// <summary>Deletes one object. Reclamation is GC work, never part of the write path.</summary>
    public void Delete(string sha256)
    {
        ValidateId(sha256);
        File.Delete(ObjectPath(objectsRoot, sha256));
    }

    /// <summary>Removes crashed or abandoned staging temporaries older than the cutoff.
    /// Individual failures (a scanner holding a lock) are skipped and left to the next sweep.</summary>
    /// <returns>The number of temporaries removed.</returns>
    public int SweepStaging(TimeSpan maxAge)
    {
        if (!Directory.Exists(stagingRoot)) return 0;

        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(stagingRoot, "*.tmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>Reclamation for garbage collection: deletes every published object whose
    /// address is not in the live set and whose file was last written before the grace cutoff —
    /// an unreferenced object written moments ago may belong to a turn that has not committed
    /// yet. Absent objects are ignored and individual failures (a scanner holding a lock) are
    /// skipped; both surface on the next pass.</summary>
    /// <returns>The number of objects removed.</returns>
    public int DeleteExcept(IReadOnlyCollection<string> liveSha256, DateTimeOffset olderThan)
    {
        if (!Directory.Exists(objectsRoot)) return 0;

        var keep = liveSha256.ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(objectsRoot))
        {
            // Dot-prefixed directories (.staging, future .trash) are not object fan-out.
            if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal)) continue;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (keep.Contains(Path.GetFileName(file))) continue;
                if (File.GetLastWriteTimeUtc(file) >= olderThan) continue;

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return removed;
    }

    internal static void ValidateId(string sha256)
    {
        if (sha256.Length != 64 || sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException(
                $"Object id '{sha256}' is not a lowercase SHA-256 hex digest.", nameof(sha256));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The temporary becomes staging garbage and is reclaimed by the next sweep.
        }
    }

    AgentObjectDescriptor IAgentObjectStore.Ingest(Stream content)
    {
        var ingested = Ingest(content);
        return new AgentObjectDescriptor(ingested.Sha256, ingested.Size);
    }
}

/// <summary>Maintenance-only reclamation for the object store; deliberately absent from the
/// commit-path <see cref="IAgentObjectStore"/> so garbage collection can never masquerade as a
/// write-path operation.</summary>
internal interface IObjectReclaimer
{
    /// <summary>Deletes unreferenced objects last written before the grace cutoff.</summary>
    int DeleteExcept(IReadOnlyCollection<string> liveSha256, DateTimeOffset olderThan);
}
