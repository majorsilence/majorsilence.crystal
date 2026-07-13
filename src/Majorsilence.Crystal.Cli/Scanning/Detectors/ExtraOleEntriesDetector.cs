using System.Text;

namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// Flags OLE compound-document entries beyond the known Crystal Reports set.
/// Any nested storage — especially one containing its own Contents stream — is
/// the signature of an embedded subreport; unknown streams may hold images or
/// other OLE payloads.
/// </summary>
public sealed class ExtraOleEntriesDetector : IFeatureDetector
{
    public string Id => "extra-ole-entries";
    public string BacklogItem => "Subreports / embedded OLE objects";

    private static readonly HashSet<string> KnownStreams = new(StringComparer.Ordinal)
    {
        "Contents",
        "QESession",
        "ReportInfo",
        "\x05SummaryInformation",
        "\x05DocumentSummaryInformation",
        "\x01CompObj",
        "\x01Ole",
    };

    // Standard side-streams present in most files; the trailing token varies ("... 0l").
    private static readonly string[] KnownStreamPrefixes =
    [
        "ReportParametersStream",
        "ViewInformationStream",
        "AnalysisGridsStream",
        "SavedRecordsStream",
        "DataSourceManager",
        "TotallerStream",
    ];

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        foreach (var entry in ctx.OleEntries.Value)
        {
            if (entry.IsStorage)
            {
                yield return $"storage '{Printable(entry.Path)}'";
            }
            else if (!entry.Path.Contains('/') && !KnownStreams.Contains(entry.Path) &&
                     !KnownStreamPrefixes.Any(p => entry.Path.StartsWith(p, StringComparison.Ordinal)))
            {
                yield return $"stream '{Printable(entry.Path)}' ({entry.Length} bytes)";
            }
            else if (entry.Path.Contains('/') && entry.Path.EndsWith("/Contents", StringComparison.Ordinal))
            {
                yield return $"nested Contents '{Printable(entry.Path)}' ({entry.Length} bytes) — subreport candidate";
            }
        }
    }

    private static string Printable(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(c < 0x20 ? $"\\x{(int)c:X2}" : c);
        return sb.ToString();
    }
}
