namespace Majorsilence.Crystal.Cli.Scanning;

/// <summary>Serializable result of a corpus scan. Contains relative paths only.</summary>
public sealed class ScanReport
{
    public int TotalFiles { get; init; }
    public int Parsed { get; init; }
    public int Failed { get; init; }
    public required Dictionary<string, List<string>> ExceptionBuckets { get; init; }
    public required Dictionary<int, TagStat> TagHistogram { get; init; }
    public required List<DetectorSection> Detectors { get; init; }

    public sealed record TagStat(int Files, int Total);

    public sealed class DetectorSection
    {
        public required string Id { get; init; }
        public required string BacklogItem { get; init; }
        public required List<DetectorHit> Hits { get; init; }
        public required List<string> Summary { get; init; }
    }
}
