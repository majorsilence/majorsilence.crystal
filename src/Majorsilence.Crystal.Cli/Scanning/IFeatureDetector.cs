namespace Majorsilence.Crystal.Cli.Scanning;

/// <summary>
/// A feature detector inspects one parsed .rpt file and flags binary patterns
/// that correspond to a not-yet-implemented converter feature (a BACKLOG.md item).
/// Inspect is called concurrently from multiple threads — detectors that keep
/// corpus-wide state must use thread-safe collections.
/// </summary>
public interface IFeatureDetector
{
    /// <summary>Stable kebab-case identifier used in reports.</summary>
    string Id { get; }

    /// <summary>The BACKLOG.md item this detector helps unlock.</summary>
    string BacklogItem { get; }

    /// <summary>Per-file hit details; empty when the file does not exercise the feature.</summary>
    IEnumerable<string> Inspect(ScanContext ctx);

    /// <summary>Corpus-wide summary lines, produced after all files were inspected.</summary>
    IEnumerable<string> Summarize() => [];
}

/// <summary>One detector hit for one file.</summary>
public sealed record DetectorHit(string DetectorId, string RelativePath, string Detail, int ChunkCount);
