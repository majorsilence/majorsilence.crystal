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

    /// <summary>
    /// Crystal's Highlighting Expert rules, in the order the file records them, which is the
    /// order they are tested in: the first one that matches decides the colours. Empty for
    /// almost every object - 7 of the 88 public reports carry any.
    /// </summary>
    public IReadOnlyList<ConditionalFormat> Conditions { get; init; } = [];
}

/// <summary>
/// One Highlighting Expert rule: compare the object's own value against a threshold, and if
/// it matches, use these colours instead of the object's own.
/// </summary>
/// <param name="Operator">The comparison to apply.</param>
/// <param name="Threshold">The value compared against, as the file records it.</param>
/// <param name="FontColor">Font colour when the rule matches, or null to leave it alone.</param>
/// <param name="BackColor">Background colour when it matches, or null to leave it alone.</param>
public sealed record ConditionalFormat(
    ConditionalOperator Operator, double Threshold, string? FontColor, string? BackColor);

/// <summary>
/// The two comparisons seen in either corpus. Crystal's expert offers more, and a record
/// naming one this does not recognise is dropped rather than guessed at - a rule applied
/// with the wrong comparison highlights the wrong rows, which is worse than not
/// highlighting at all.
/// </summary>
public enum ConditionalOperator { LessThan, GreaterThan }

// Crystal's four horizontal alignments. Justify has no RDL equivalent - the schema's
// TextAlign is General/Left/Center/Right - so the converter emits the name the target
// engine understands, and a consumer that does not know it falls back to its default.
public enum HorizontalAlignment { Left, Center, Right, Justify }
