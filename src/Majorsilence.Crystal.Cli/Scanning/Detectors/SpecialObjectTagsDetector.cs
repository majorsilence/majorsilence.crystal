namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// Flags files containing the special object wrapper tags suspected to be
/// charts (175/176), maps (182/183), and cross-tabs (185/186). Tags 170/171 and
/// 172/173 are documented as Line/Box objects in RptParser but are included in
/// the detail for context because BACKLOG.md lists 170–176 as chart candidates.
/// </summary>
public sealed class SpecialObjectTagsDetector : IFeatureDetector
{
    public string Id => "special-object-tags";
    public string BacklogItem => "Charts / maps / cross-tabs";

    private static readonly int[] TriggerTags = [175, 176, 182, 183, 185, 186];
    private static readonly int[] ContextTags = [170, 171, 172, 173];

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        var counts = ctx.Chunks
            .Where(r => TriggerTags.Contains(r.Tag) || ContextTags.Contains(r.Tag))
            .GroupBy(r => r.Tag)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), MaxLen: g.Max(r => r.Data.Length)));

        if (!TriggerTags.Any(counts.ContainsKey))
            yield break;

        var parts = counts.OrderBy(kv => kv.Key)
            .Select(kv => $"tag{kv.Key}×{kv.Value.Count}(maxLen={kv.Value.MaxLen})");
        yield return string.Join(" ", parts);
    }
}
