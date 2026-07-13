using OpenMcdf;

namespace Majorsilence.Crystal.Parser.OleStorage;

/// <summary>
/// Reads named streams from an OLE Compound File Binary (.rpt container).
/// </summary>
public sealed class OleReader : IDisposable
{
    private readonly RootStorage _storage;

    private OleReader(RootStorage storage) => _storage = storage;

    public static OleReader Open(string path) =>
        new(RootStorage.OpenRead(path));

    public static OleReader Open(Stream stream) =>
        new(RootStorage.Open(stream, StorageModeFlags.LeaveOpen));

    public bool HasStream(string name) =>
        _storage.ContainsEntry(name);

    public byte[] ReadStream(string name)
    {
        using var cfbStream = _storage.OpenStream(name);
        var buf = new byte[cfbStream.Length];
        cfbStream.ReadExactly(buf);
        return buf;
    }

    public IEnumerable<string> ListStreamNames() =>
        _storage.EnumerateEntries().Select(e => e.Name);

    /// <summary>
    /// Read a stream at a '/'-separated path (as produced by <see cref="EnumerateEntries"/>),
    /// walking through nested storages.
    /// </summary>
    public byte[] ReadStreamAt(string path)
    {
        string[] parts = path.Split('/');
        Storage storage = _storage;
        for (int i = 0; i < parts.Length - 1; i++)
            storage = storage.OpenStorage(parts[i]);
        using var cfbStream = storage.OpenStream(parts[^1]);
        var buf = new byte[cfbStream.Length];
        cfbStream.ReadExactly(buf);
        return buf;
    }

    /// <summary>
    /// Enumerate all entries in the compound document, optionally recursing into
    /// nested storages. Paths use '/' separators relative to the root storage.
    /// </summary>
    public IEnumerable<OleEntryInfo> EnumerateEntries(bool recursive = false) =>
        Enumerate(_storage, string.Empty, recursive);

    private static IEnumerable<OleEntryInfo> Enumerate(Storage storage, string prefix, bool recursive)
    {
        foreach (var entry in storage.EnumerateEntries())
        {
            string path = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            bool isStorage = entry.Type == EntryType.Storage;
            yield return new OleEntryInfo(path, isStorage, entry.Length);
            if (recursive && isStorage && storage.TryOpenStorage(entry.Name, out var child) && child is not null)
            {
                foreach (var nested in Enumerate(child, path, recursive))
                    yield return nested;
            }
        }
    }

    public void Dispose() => _storage.Dispose();
}

/// <summary>An entry (stream or storage) inside an OLE compound document.</summary>
public sealed record OleEntryInfo(string Path, bool IsStorage, long Length);
