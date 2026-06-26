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
    public bool CanGrow { get; init; }
    public bool SuppressIfBlank { get; init; }
    public string? FormatString { get; init; }
}

public enum HorizontalAlignment { Left, Center, Right }
