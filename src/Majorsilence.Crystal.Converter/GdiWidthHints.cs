namespace Majorsilence.Crystal.Converter;

/// <summary>
/// How much wider GDI sets a line of text at a whole number of pixels to the em than the
/// font's design widths scaled to that em - see RdlConverter.LineBreakInset.
///
/// At the screen's resolution GDI's widths are hinted: the font's own instructions round
/// each glyph's advance to whole pixels, per size, by weight and slant, so a line of text
/// comes out several percent wider or narrower than its design width. Arial at 9 pixels
/// to the em sets text 5% wide, at 8 pixels 6.5% narrow. Crystal measures a
/// line at 96 dpi to decide where it breaks, so these are part of where its lines break.
///
/// Each row is one family and style, one letter per pixel of em from <see cref="FirstPpem"/>
/// up: the hinted width of <see cref="Sample"/> over its design width, 'a' for the same
/// width and each letter after it half a percent wider, each before it half a percent
/// narrower. Past the end of a row GDI's widths are within two percent of the design ones
/// and are taken as equal. A family with one row has the same widths in every style;
/// Impact has no bold of its own.
///
/// Read from GDI's GetTextExtentPoint32 with the Windows 11 versions of these fonts;
/// GdiWidthHintsTests compares the table against GDI wherever it runs on Windows.
/// </summary>
internal static class GdiWidthHints
{
    internal const int FirstPpem = 6;
    internal const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// The text each ratio is measured on: words, capitals and figures in about the
    /// proportions a report's labels and fields have them.
    /// </summary>
    internal const string Sample =
        "The quick brown fox jumps over the lazy dog. Sphinx of black quartz, judge my vow; " +
        "Customer Name and Address; Invoice Total 2024; Order Number 12-345-678.90; " +
        "PACK MY BOX WITH FIVE DOZEN LIQUOR JUGS 0123456789";

    /// <summary>
    /// GDI's hinted width over the design width, for this font at this many pixels to the
    /// em; 1 where the table does not cover it.
    /// </summary>
    internal static double Ratio(string family, bool bold, bool italic, int ppem)
    {
        int i = ppem - FirstPpem;
        if (i < 0 || !Rows.TryGetValue(family, out var styles))
            return 1;
        string row = styles[(bold ? 1 : 0) + (italic ? 2 : 0)];
        return i < row.Length ? 1 + (Letters.IndexOf(row[i]) - 26) * 0.005 : 1;
    }

    private static string[] AllStyles(string row) => [row, row, row, row];

    // Regular, Bold, Italic, Bold Italic; 6 to 40 pixels to the em.
    internal static readonly Dictionary<string, string[]> Rows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] =
        [
            "WeNkkVbZaTTSYaYYXWabXbaZZYYWWXYaabX",
            "cZTkgdVcgWZUdeZbWaaZafcaZZZYZXZbabX",
            "WhOeiaebcWZXafbdYbZbWccabZbYZZabacY",
            "cYTmgfaaaWYTafadXZZYXacaZZbYaXabYbY",
        ],
        ["Arial Black"] = AllStyles("ccaXdZZbZbbYabZabZabaaaaabaaaZabaZa"),
        ["Verdana"] =
        [
            "iQbyugfabZccYbbYabYbZbcZcaZbbZaaZba",
            "WYfWahZWaXVabdaZcZZbZacaZbaZbaaaaaa",
            "iRbfcfYbdZcbYbaZabYbZbbYcaZcbaaaZbZ",
            "WYdjXfcVcYVabdaZcZZbZacbZbaZbaaaaaa",
        ],
        ["Times New Roman"] =
        [
            "jlfichaWbVSWbWVXYZYYaaWUZZXYZZXYZZY",
            "fjYedoZSbdTZaaVXadYZccZYYXaaaaYZaZZ",
            "incncjaedhadcdaccebccfZbacZbadabaca",
            "gnZffgZdcebadeabccZcceZbbdZcbcabccb",
        ],
        ["Courier New"] = AllStyles("wQiLamUfQaiWeTagXdUafXcVaeYcWaeYcXa"),
        ["Tahoma"] =
        [
            "UcZofWhYZadWbZabaabcaZaZaaaZcaaZbab",
            "kSWycZdUZYbbYbcYacZZbabbabZaaZbaZaa",
            "UcZofWhYZadWbZabaabcaZaZaaaZcaaZbab",
            "kSWycZdUZYbbYbcYacZZbabbabZaaZbaZaa",
        ],
        ["Calibri"] =
        [
            "VjacWenfXYZcZbebdZZbbZabbZaZbaaZbaa",
            "TjZcWZZcbVecbacaaaZbaaZdabZbabZaabZ",
            "XnbgWdccYYaeZbbdabWcZaYcZbYaZbaZcab",
            "TkYcXZacXVacbadbcZZbbaaabZaYbZabaca",
        ],
        ["Impact"] = AllStyles("WgQiZfYcWeaaWZaZZaYZabaaZYaaZZZaaZZ"),
        ["Cambria"] =
        [
            "SfXacXeYdWdZbaZdabZbaabZcZbZZabZaca",
            "bdgXZcbdVaabcYabbbZbbZbZbaabZbZaaab",
            "VkaeWZYbVaacaaZcdZbacZaYcaaZaZaaYba",
            "TeiYdUbZZabdaZXbdYbbcZZabbaaXaaZaab",
        ],
    };
}
