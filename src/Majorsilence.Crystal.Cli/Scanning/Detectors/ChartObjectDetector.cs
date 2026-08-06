namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>Flags files containing the tag-180 chart/graph object wrapper.</summary>
public sealed class ChartObjectDetector : IFeatureDetector
{
    public string Id => "chart-object";
    public string BacklogItem => "Charts / graphs";

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        int count = ctx.Chunks.Count(r => r.Tag == 180);
        if (count > 0)
            yield return $"tag180 x{count}";
    }
}
