using System.Collections.Concurrent;
using Majorsilence.Crystal.Parser.Chunks;

namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// Flags unknown object wrapper tags inside section bodies. Section bodies hold a
/// sequence of objects, each bracketed by a start tag and its end tag (start+1);
/// the interior records of known object types (format, font, colour, …) are
/// skipped wholesale so only genuinely unknown wrappers surface. Embedded
/// picture / OLE objects are the primary suspects.
/// </summary>
public sealed class UnknownSectionObjectTagsDetector : IFeatureDetector
{
    public string Id => "unknown-section-object-tags";
    public string BacklogItem => "Image / OLE picture objects";

    // Object wrapper start tags the parser already recognizes (end tag = start + 1):
    // 159 FieldObject, 165 TextObject, 170 Line, 172 Box,
    // 175/182/185 chart/map/cross-tab (covered by SpecialObjectTagsDetector).
    private static readonly HashSet<int> KnownObjectStarts = [159, 165, 170, 172, 175, 182, 185];

    // Non-object records legitimately flat inside a section body.
    private static readonly HashSet<int> KnownFlat = [157, 255];

    private readonly ConcurrentDictionary<int, int> _unknownTagFiles = new();

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        var chunks = ctx.Chunks;
        var unknown = new Dictionary<int, (int Count, int MaxLen)>();

        for (int i = 0; i < chunks.Count; i++)
        {
            if (!TslvRecord.IsSectionStart(chunks[i].Tag)) continue;

            int sectionEnd = chunks[i].Tag + 1;
            int j = i + 1;
            while (j < chunks.Count && chunks[j].Tag != sectionEnd)
            {
                int t = chunks[j].Tag;
                if (TslvRecord.IsSectionStart(t) || t is 139 or 131 or 133 or 135 or 137)
                    break;   // malformed nesting guard — bail out of this section

                if (KnownFlat.Contains(t)) { j++; continue; }

                if (KnownObjectStarts.Contains(t))
                {
                    j = SkipObject(chunks, j, t + 1, sectionEnd);
                    continue;
                }

                // Unknown record at object position — candidate new object type
                unknown[t] = unknown.TryGetValue(t, out var v)
                    ? (v.Count + 1, Math.Max(v.MaxLen, chunks[j].Data.Length))
                    : (1, chunks[j].Data.Length);

                // If it looks like a wrapper (odd tag), skip to its end tag to
                // avoid reporting all its interior records too.
                if (t % 2 == 1 && t is > 158 and < 230)
                    j = SkipObject(chunks, j, t + 1, sectionEnd);
                else
                    j++;
            }
            i = j;
        }

        foreach (var (tag, (count, maxLen)) in unknown.OrderBy(kv => kv.Key))
        {
            _unknownTagFiles.AddOrUpdate(tag, 1, (_, v) => v + 1);
            yield return $"tag {tag}×{count} (maxLen={maxLen})";
        }
    }

    private static int SkipObject(IReadOnlyList<TslvRecord> chunks, int start, int endTag, int sectionEnd)
    {
        int j = start + 1;
        while (j < chunks.Count && chunks[j].Tag != endTag && chunks[j].Tag != sectionEnd &&
               !TslvRecord.IsSectionStart(chunks[j].Tag) && !TslvRecord.IsSectionEnd(chunks[j].Tag))
            j++;
        return (j < chunks.Count && chunks[j].Tag == endTag) ? j + 1 : j;
    }

    public IEnumerable<string> Summarize()
    {
        foreach (var (tag, files) in _unknownTagFiles.OrderByDescending(kv => kv.Value))
            yield return $"unknown section tag {tag}: {files} files";
    }
}
