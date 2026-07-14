using Majorsilence.Crystal.Model.Objects;

namespace Majorsilence.Crystal.Model;

public sealed class Section
{
    public SectionType Type { get; init; }
    public int GroupLevel { get; init; }
    public int HeightTwips { get; init; }
    public bool Suppress { get; init; }
    public bool NewPageBefore { get; init; }
    public bool NewPageAfter { get; init; }
    public bool ResetPageNumber { get; init; }
    public bool RepeatGroupHeader { get; init; }

    /// <summary>Crystal formula text driving conditional suppression; null when suppression is static.</summary>
    public string? SuppressFormula { get; init; }
    public List<ReportObject> Objects { get; init; } = [];
}
