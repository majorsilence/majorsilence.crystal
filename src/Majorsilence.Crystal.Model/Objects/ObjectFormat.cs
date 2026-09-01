namespace Majorsilence.Crystal.Model.Objects;

public sealed class ObjectFormat
{
    public string? FontName { get; init; }
    public int? FontSize { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public string? ForeColor { get; init; }
    public string? BackColor { get; init; }
    public HorizontalAlignment HAlign { get; init; } = HorizontalAlignment.Left;
    /// <summary>Per-edge border style codes (0 = none, 1 = single, 2 = double, 3 = dashed,
    /// 4 = dotted). A report's "underline" beneath a column label and the box around a
    /// title are these, not drawn line objects.</summary>
    public byte BorderLeft { get; init; }
    public byte BorderRight { get; init; }
    public byte BorderTop { get; init; }
    public byte BorderBottom { get; init; }
    /// <summary>Border line width in twips; 20 (1pt) is Crystal's default.</summary>
    public int BorderWidthTwips { get; init; }
    public bool DropShadow { get; init; }
    public bool CanGrow { get; init; }
    public bool SuppressIfBlank { get; init; }
    public string? FormatString { get; init; }
}

// Crystal's four horizontal alignments. Justify has no RDL equivalent - the schema's
// TextAlign is General/Left/Center/Right - so the converter emits the name the target
// engine understands, and a consumer that does not know it falls back to its default.
public enum HorizontalAlignment { Left, Center, Right, Justify }
