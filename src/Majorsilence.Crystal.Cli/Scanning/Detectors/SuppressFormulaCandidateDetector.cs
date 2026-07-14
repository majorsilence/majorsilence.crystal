using System.Collections.Concurrent;

namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// Scans tag-255 SectionProperties records for formula-driven properties. After
/// the tag-254 child block (8-byte header + data), the payload is a sequence of
/// formula-hook entries, one per drivable section property. Each entry is a
/// MUTF-8 formula name (empty when no formula is attached) followed by 3 trailer
/// bytes (observed idle trailer: 00 FF FF). A non-empty name references a
/// tag-119 formula field (typically "*_Visibility" for the suppress hook).
/// </summary>
public sealed class SuppressFormulaCandidateDetector : IFeatureDetector
{
    public string Id => "suppress-formula-candidate";
    public string BacklogItem => "Section-level suppress formula";

    private readonly ConcurrentDictionary<int, int> _entryHits = new();

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        foreach (var rec in ctx.Chunks.Where(r => r.Tag == 255))
        {
            var ch254 = rec.ParseChildren().FirstOrDefault(c => c.Tag == 254);
            if (ch254 is null) continue;

            int pos = 8 + ch254.Data.Length;
            for (int entry = 0; pos + 8 <= rec.Data.Length; entry++)
            {
                string? name = rec.ReadMutf8String(pos, out int consumed);
                if (consumed <= 0 || name is null) break;   // out of entry space / not an entry
                pos += consumed + 3;                        // 3 trailer bytes per entry

                if (name.Length == 0) continue;
                _entryHits.AddOrUpdate(entry, 1, (_, v) => v + 1);
                yield return $"entry {entry} formula '{name}' at offset {rec.StreamOffset}";
            }
        }
    }

    public IEnumerable<string> Summarize()
    {
        foreach (var (entry, count) in _entryHits.OrderBy(kv => kv.Key))
            yield return $"formula hook at entry {entry}: {count} records";
    }
}
