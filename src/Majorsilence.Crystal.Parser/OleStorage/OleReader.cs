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

    public void Dispose() => _storage.Dispose();
}
