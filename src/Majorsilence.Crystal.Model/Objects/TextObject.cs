namespace Majorsilence.Crystal.Model.Objects;

public sealed class TextObject : ReportObject
{
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The date format of each field embedded in the text that sets one, by the reference as
    /// it is written in <see cref="Text"/> without its braces ("?Sale Date").
    /// </summary>
    public Dictionary<string, string> FieldFormats { get; init; } = [];

    /// <summary>
    /// The text in pieces, each in its own font, when they are not all in one: a letterhead's
    /// bold first line over a plain address, an italic word inside a sentence. Joined, the
    /// pieces' text is <see cref="Text"/>; empty when one font covers it all.
    /// </summary>
    public IReadOnlyList<TextRun> Runs { get; init; } = [];
}

/// <summary>A piece of a text object's text and the font it is in.</summary>
public sealed record TextRun(string Text, string? FontName, double? FontSize, bool Bold, bool Italic, bool Underline);
