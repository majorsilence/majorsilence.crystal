using Majorsilence.Crystal.Parser.Chunks;

namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// Flags unexpected tags in the section wrapper sequence. The standard layout is
/// sectionStart → 157 (SectionCode) → 255 (SectionProperties) → first object.
/// Any other tag before the first object is a candidate for the formula-driven
/// section-suppress reference the parser does not yet understand.
/// </summary>
public sealed class SuppressFormulaCandidateDetector : IFeatureDetector
{
    public string Id => "suppress-formula-candidate";
    public string BacklogItem => "Section-level suppress formula";

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        var chunks = ctx.Chunks;
        for (int i = 0; i < chunks.Count; i++)
        {
            if (!TslvRecord.IsSectionStart(chunks[i].Tag)) continue;

            int endTag = chunks[i].Tag + 1;
            for (int j = i + 1; j < chunks.Count; j++)
            {
                int t = chunks[j].Tag;
                if (t == endTag || t == 139 || TslvRecord.IsSectionStart(t) ||
                    t is 131 or 133 or 135 or 137)
                    break;                       // section end / next boundary — no objects present
                if (t >= 159)
                    break;                       // first object record — wrapper sequence over
                if (t is 157 or 255)
                    continue;                    // expected wrapper records

                yield return $"unexpected tag {t} (len={chunks[j].Data.Length}) after " +
                             $"{TslvRecord.SectionKindFromTag(chunks[i].Tag)} start at offset {chunks[j].StreamOffset}";
            }
        }
    }
}
