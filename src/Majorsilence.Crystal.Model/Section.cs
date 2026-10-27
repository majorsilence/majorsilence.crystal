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

    /// <summary>
    /// Crystal's "Underlay Following Sections": the sections after this one print from its
    /// top rather than below it, over it, so it takes no space of its own.
    /// </summary>
    public bool Underlay { get; init; }

    /// <summary>
    /// Crystal's "Keep Together": a section that does not fit in the space left on the page
    /// starts on the next one, rather than printing what fits and the rest overleaf. On for
    /// a new section, and for all but about one section in a hundred.
    /// </summary>
    public bool KeepTogether { get; init; } = true;
    public bool RepeatGroupHeader { get; init; }

    /// <summary>Crystal formula text driving conditional suppression; null when suppression is static.</summary>
    public string? SuppressFormula { get; init; }

    /// <summary>Crystal formula text gating a page break before this section; null when none is attached.</summary>
    public string? NewPageBeforeFormula { get; init; }

    /// <summary>Crystal formula text gating a page break after this section; null when none is attached.</summary>
    public string? NewPageAfterFormula { get; init; }

    /// <summary>Crystal formula text driving the section's background colour; null when none is attached.</summary>
    public string? BackColorFormula { get; init; }

    /// <summary>
    /// The section's own background colour as "#RRGGBB", or null when it has none. Drawn
    /// across the whole band, behind every object; an object's own background colour is
    /// drawn over it.
    /// </summary>
    public string? BackColor { get; init; }
    public List<ReportObject> Objects { get; init; } = [];
}
