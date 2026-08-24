using Majorsilence.Crystal.Model.Objects;

namespace Majorsilence.Crystal.Model;

public sealed class Section
{
    public SectionType Type { get; init; }
    public int GroupLevel { get; init; }
    // Settable for the same reason the field data types are: a later pass refines it.
    // Free-form objects carry no position of their own, so the converter flows them
    // across the printable width and grows the band to fit the rows that produces.
    public int HeightTwips { get; set; }
    public bool Suppress { get; init; }
    public bool NewPageBefore { get; init; }
    public bool NewPageAfter { get; init; }
    public bool ResetPageNumber { get; init; }
    public bool RepeatGroupHeader { get; init; }

    /// <summary>Crystal formula text driving conditional suppression; null when suppression is static.</summary>
    public string? SuppressFormula { get; init; }

    /// <summary>Crystal formula text gating a page break before this section; null when none is attached.</summary>
    public string? NewPageBeforeFormula { get; init; }

    /// <summary>Crystal formula text gating a page break after this section; null when none is attached.</summary>
    public string? NewPageAfterFormula { get; init; }

    /// <summary>Crystal formula text driving the section's background colour; null when none is attached.</summary>
    public string? BackColorFormula { get; init; }
    public List<ReportObject> Objects { get; init; } = [];
}
