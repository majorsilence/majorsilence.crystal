using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Parser.Chunks;
using Majorsilence.Crystal.Parser.OleStorage;

namespace Majorsilence.Crystal.Cli.Scanning;

/// <summary>
/// Everything a feature detector may inspect for one file. The absolute path is
/// never serialized into reports — only <see cref="RelativePath"/> appears in output.
/// </summary>
public sealed class ScanContext
{
    public required string FilePath { get; init; }
    public required string RelativePath { get; init; }
    public required ParseResult Result { get; init; }

    public IReadOnlyList<TslvRecord> Chunks => Result.RawChunks;

    /// <summary>
    /// OLE compound-document entries (recursive), loaded lazily because
    /// <see cref="ParseResult"/> does not retain the OLE structure.
    /// </summary>
    public Lazy<IReadOnlyList<OleEntryInfo>> OleEntries { get; }

    public ScanContext()
    {
        OleEntries = new Lazy<IReadOnlyList<OleEntryInfo>>(() =>
        {
            try
            {
                using var ole = OleReader.Open(FilePath!);   // required property; set before Lazy resolves
                return ole.EnumerateEntries(recursive: true).ToList();
            }
            catch
            {
                return [];
            }
        });
    }
}
