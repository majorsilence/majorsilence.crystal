using System.Collections.Concurrent;

namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// Scans the tag-229 group-condition record's 2-byte unknown slot — immediately after the
/// known MUTF-8 field reference + Int16 condition code + Int16 sort code, right before the
/// "Others" MUTF-8 strings — for corpus-wide variance. Only records belonging to the
/// report's own groups (marked "@Group #N Order", not the "@Row #N Order"/"@Column #N
/// Order"/"@Detail Value Grid #N Order" markers used by cross-tab axes and charts) are
/// considered, since only real report groups can carry a RepeatGroupHeader option.
/// </summary>
public sealed class GroupConditionTailDetector : IFeatureDetector
{
    public string Id => "group-condition-tail";
    public string BacklogItem => "RepeatGroupHeader binary bit";

    private const int MaxExampleFiles = 5;

    private readonly ConcurrentDictionary<(byte B0, byte B1), int> _histogram = new();
    private readonly ConcurrentDictionary<(byte B0, byte B1), ConcurrentQueue<string>> _examples = new();

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        foreach (var rec in ctx.Chunks.Where(r => r.Tag == 229))
        {
            string? tableField = rec.ReadMutf8String(0, out int nc);
            if (string.IsNullOrEmpty(tableField) || nc <= 0) continue;

            bool isReportGroup = false;
            foreach (var (_, _, s) in Scanning.Mutf8Scanner.Scan(rec.Data))
            {
                if (s.StartsWith("@Group #", StringComparison.Ordinal)) { isReportGroup = true; break; }
                if (s.StartsWith("@Row #", StringComparison.Ordinal) ||
                    s.StartsWith("@Column #", StringComparison.Ordinal) ||
                    s.StartsWith("@Detail Value Grid #", StringComparison.Ordinal))
                    break;   // axis/chart group, not a real report group — skip
            }
            if (!isReportGroup) continue;

            int tailStart = nc + 4;   // past condCode(2) + sortCode(2)
            if (tailStart + 1 >= rec.Data.Length) continue;

            var key = (rec.Data[tailStart], rec.Data[tailStart + 1]);
            _histogram.AddOrUpdate(key, 1, (_, v) => v + 1);
            var files = _examples.GetOrAdd(key, _ => new ConcurrentQueue<string>());
            if (files.Count < MaxExampleFiles && !files.Contains(ctx.RelativePath))
                files.Enqueue(ctx.RelativePath);
        }
        yield break;   // corpus-wide variance only — see Summarize
    }

    public IEnumerable<string> Summarize()
    {
        var ordered = _histogram.OrderByDescending(kv => kv.Value).ToList();
        if (ordered.Count == 0) yield break;

        yield return $"group-condition tail values: {ordered.Count} distinct, {ordered.Sum(kv => kv.Value)} total groups";
        foreach (var (key, count) in ordered)
        {
            string files = _examples.TryGetValue(key, out var q) ? string.Join("; ", q) : "";
            yield return $"[0x{key.B0:X2} 0x{key.B1:X2}] x{count}  e.g. {files}";
        }
    }
}
