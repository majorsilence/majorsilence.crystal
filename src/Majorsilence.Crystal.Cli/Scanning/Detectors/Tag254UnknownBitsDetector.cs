using System.Collections.Concurrent;

namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// Accumulates a per-offset value histogram for the undocumented tail of the
/// tag-254 section-flags record (bytes 29..52; bytes 0..28 are documented in
/// RptParser.ExtractSectionFlags). Offsets whose value varies across the corpus
/// are candidate homes for un-mapped flags such as RepeatGroupHeader. The section
/// kind is derived from the record itself: byte[0] = AreaPairKind
/// (1=Page, 2=Report, 3=Group, 4=Detail), bytes[1..2] = isHeader.
/// </summary>
public sealed class Tag254UnknownBitsDetector : IFeatureDetector
{
    public string Id => "tag254-unknown-bits";
    public string BacklogItem => "RepeatGroupHeader binary bit";

    private const int DocumentedEnd = 29;   // first undocumented offset
    private const int MaxExampleFiles = 5;

    private readonly ConcurrentDictionary<(string Kind, int Offset, byte Value), int> _histogram = new();
    private readonly ConcurrentDictionary<(string Kind, int Offset, byte Value), ConcurrentQueue<string>> _examples = new();

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        foreach (var rec in ctx.Chunks.Where(r => r.Tag == 255))
        {
            var ch = rec.ParseChildren().FirstOrDefault(c => c.Tag == 254);
            if (ch is null || ch.Data.Length < 5) continue;

            var d = ch.Data;
            if (d[3] == 0 && d[4] == 0) continue;   // area-level record — skip

            string kind = KindName(d[0], isHeader: d[1] != 0 || d[2] != 0);
            for (int off = DocumentedEnd; off < d.Length && off <= 52; off++)
            {
                var key = (kind, off, d[off]);
                _histogram.AddOrUpdate(key, 1, (_, v) => v + 1);
                var files = _examples.GetOrAdd(key, _ => new ConcurrentQueue<string>());
                if (files.Count < MaxExampleFiles && !files.Contains(ctx.RelativePath))
                    files.Enqueue(ctx.RelativePath);
            }
        }
        yield break;   // no per-file hits — variance only shows corpus-wide (see Summarize)
    }

    public IEnumerable<string> Summarize()
    {
        var byOffset = _histogram
            .GroupBy(kv => (kv.Key.Kind, kv.Key.Offset))
            .Where(g => g.Count() > 1)                       // >1 distinct value → interesting
            .OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Offset);

        foreach (var group in byOffset)
        {
            var values = group.OrderByDescending(kv => kv.Value).ToList();
            var parts = values.Select(kv => $"0x{kv.Key.Value:X2}×{kv.Value}");
            yield return $"{group.Key.Kind} byte[{group.Key.Offset}] varies: {string.Join(" ", parts)}";

            // Minority values are the interesting ones — list example files for each
            foreach (var kv in values.Skip(1))
            {
                if (_examples.TryGetValue(kv.Key, out var files))
                    yield return $"    value 0x{kv.Key.Value:X2} e.g. {string.Join("; ", files.Take(3))}";
            }
        }
    }

    private static string KindName(byte areaPairKind, bool isHeader) => areaPairKind switch
    {
        1 => isHeader ? "PageHeader" : "PageFooter",
        2 => isHeader ? "ReportHeader" : "ReportFooter",
        3 => isHeader ? "GroupHeader" : "GroupFooter",
        4 => "Detail",
        _ => $"Unknown({areaPairKind})"
    };
}
