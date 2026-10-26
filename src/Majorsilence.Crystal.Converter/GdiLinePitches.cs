namespace Majorsilence.Crystal.Converter;

/// <summary>
/// The pitch Crystal sets the lines of a text object at - from the top of one line to the
/// top of the next - for a font asked for by its cell height. RDL's engine sets lines at the
/// font size unless told a LineHeight, which put every line of a paragraph about a quarter
/// closer than Crystal's: the paragraph typography probe's 12pt Arial lines are 60 pixels
/// apart in Crystal's 300-dpi render and were 45 in ours.
///
/// Crystal lays a text object out in screen pixels, 96 to the inch, at whatever line height
/// GDI gives the font there, and its export keeps that pitch. So the pitch is a whole number
/// of 96-dpi pixels - every one measured is within a tenth of a pixel of one - and it is not
/// a ratio of the size: Arial at 8, 10 and 12 points is 14, 16 and 19 pixels, and Bold or
/// Italic can move it a pixel either way (Arial Bold 12 is 20).
///
/// Each family and style is a list of (cell height in twips, pitch in 96-dpi pixels),
/// measured by rendering a four-line text object through Crystal's own runtime in that font
/// at each size and reading its line tops off the page: Arial from 5 to 24 points in half
/// points, with the 5.95 and 7.95 the production reports use and 26, 28 and 36; the other
/// families from 6 to 18 points, in Regular and Bold. A size between two measured ones takes
/// the nearer's pitch scaled to it. A family that is not listed has none, and keeps the
/// engine's own.
/// </summary>
internal static class GdiLinePitches
{
    /// <summary>The line pitch in points, or null for a family the table does not hold.</summary>
    internal static double? PitchPoints(string? family, double crystalPoints, bool bold, bool italic)
    {
        if (family is null || !Rows.TryGetValue(family, out var styles))
            return null;
        var row = styles[(bold ? 1 : 0) + (italic ? 2 : 0)];
        int twips = (int)Math.Round(crystalPoints * 20, MidpointRounding.AwayFromZero);
        var (cell, pixels) = row.MinBy(e => Math.Abs(e.CellTwips - twips));
        double px = cell == twips ? pixels : Math.Round(pixels * (double)twips / cell);
        return px * 72 / 96;
    }

    // Regular, Bold, Italic, Bold Italic.
    private static readonly Dictionary<string, (int CellTwips, int Pixels)[][]> Rows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] =
        [
            // Regular
            [(100, 7), (110, 7), (119, 10), (120, 10), (130, 12), (140, 12), (150, 13), (159, 14), (160, 14), (170, 14), (180, 15), (190, 16), (200, 16), (210, 16), (220, 17), (230, 17), (240, 19), (250, 20), (260, 20), (270, 22), (280, 23), (290, 23), (300, 24), (310, 25), (320, 25), (330, 26), (340, 27), (350, 27), (360, 28), (370, 29), (380, 29), (390, 32), (400, 33), (410, 33), (420, 33), (430, 34), (440, 34), (450, 36), (460, 36), (470, 36), (480, 37), (520, 41), (560, 43), (720, 57)],
            // Bold
            [(100, 7), (110, 7), (119, 10), (120, 10), (130, 11), (140, 11), (150, 12), (159, 14), (160, 14), (170, 14), (180, 15), (190, 16), (200, 16), (210, 16), (220, 18), (230, 18), (240, 20), (250, 20), (260, 20), (270, 22), (280, 23), (290, 23), (300, 25), (310, 25), (320, 25), (330, 27), (340, 28), (350, 28), (360, 30), (370, 31), (380, 31), (390, 31), (400, 33), (410, 33), (420, 34), (430, 35), (440, 35), (450, 36), (460, 37), (470, 37), (480, 38), (520, 42), (560, 45), (720, 58)],
            // Italic
            [(100, 7), (110, 7), (119, 11), (120, 11), (130, 12), (140, 12), (150, 13), (159, 14), (160, 14), (170, 14), (180, 15), (190, 16), (200, 16), (210, 16), (220, 17), (230, 17), (240, 20), (250, 21), (260, 21), (270, 23), (280, 24), (290, 24), (300, 25), (310, 25), (320, 25), (330, 27), (340, 29), (350, 29), (360, 29), (370, 30), (380, 30), (390, 31), (400, 32), (410, 32), (420, 32), (430, 35), (440, 35), (450, 36), (460, 37), (470, 37), (480, 38), (520, 41), (560, 44), (720, 57)],
            // Bold Italic
            [(100, 7), (110, 7), (119, 10), (120, 10), (130, 11), (140, 11), (150, 11), (159, 13), (160, 13), (170, 13), (180, 15), (190, 16), (200, 16), (210, 16), (220, 17), (230, 17), (240, 19), (250, 20), (260, 20), (270, 22), (280, 24), (290, 24), (300, 25), (310, 25), (320, 25), (330, 26), (340, 27), (350, 27), (360, 29), (370, 29), (380, 29), (390, 31), (400, 33), (410, 33), (420, 33), (430, 35), (440, 35), (450, 35), (460, 36), (470, 36), (480, 37), (520, 41), (560, 43), (720, 57)],
        ],
        ["Times New Roman"] =
        [
            // Regular
            [(120, 10), (140, 12), (160, 14), (180, 16), (200, 16), (220, 18), (240, 20), (260, 20), (280, 22), (300, 23), (320, 24), (360, 28)],
            // Bold
            [(120, 11), (140, 12), (160, 13), (180, 16), (200, 16), (220, 18), (240, 20), (260, 20), (280, 23), (300, 24), (320, 25), (360, 27)],
            // Italic (as measured without the slant)
            [(120, 10), (140, 12), (160, 14), (180, 16), (200, 16), (220, 18), (240, 20), (260, 20), (280, 22), (300, 23), (320, 24), (360, 28)],
            // Bold Italic (as measured without the slant)
            [(120, 11), (140, 12), (160, 13), (180, 16), (200, 16), (220, 18), (240, 20), (260, 20), (280, 23), (300, 24), (320, 25), (360, 27)],
        ],
        ["Calibri"] =
        [
            // Regular
            [(120, 10), (140, 11), (160, 13), (180, 14), (200, 15), (220, 18), (240, 19), (260, 21), (280, 23), (300, 24), (320, 26), (360, 29)],
            // Bold
            [(120, 10), (140, 11), (160, 13), (180, 14), (200, 15), (220, 18), (240, 19), (260, 21), (280, 23), (300, 24), (320, 26), (360, 29)],
            // Italic (as measured without the slant)
            [(120, 10), (140, 11), (160, 13), (180, 14), (200, 15), (220, 18), (240, 19), (260, 21), (280, 23), (300, 24), (320, 26), (360, 29)],
            // Bold Italic (as measured without the slant)
            [(120, 10), (140, 11), (160, 13), (180, 14), (200, 15), (220, 18), (240, 19), (260, 21), (280, 23), (300, 24), (320, 26), (360, 29)],
        ],
        ["Verdana"] =
        [
            // Regular
            [(120, 10), (140, 12), (160, 13), (180, 14), (200, 16), (220, 18), (240, 18), (260, 20), (280, 23), (300, 25), (320, 25), (360, 29)],
            // Bold
            [(120, 10), (140, 12), (160, 13), (180, 14), (200, 16), (220, 18), (240, 18), (260, 20), (280, 23), (300, 25), (320, 25), (360, 29)],
            // Italic (as measured without the slant)
            [(120, 10), (140, 12), (160, 13), (180, 14), (200, 16), (220, 18), (240, 18), (260, 20), (280, 23), (300, 25), (320, 25), (360, 29)],
            // Bold Italic (as measured without the slant)
            [(120, 10), (140, 12), (160, 13), (180, 14), (200, 16), (220, 18), (240, 18), (260, 20), (280, 23), (300, 25), (320, 25), (360, 29)],
        ],
        ["Tahoma"] =
        [
            // Regular
            [(120, 10), (140, 11), (160, 13), (180, 14), (200, 16), (220, 18), (240, 19), (260, 21), (280, 23), (300, 24), (320, 25), (360, 29)],
            // Bold
            [(120, 10), (140, 11), (160, 13), (180, 14), (200, 16), (220, 18), (240, 19), (260, 21), (280, 23), (300, 24), (320, 25), (360, 29)],
            // Italic (as measured without the slant)
            [(120, 10), (140, 11), (160, 13), (180, 14), (200, 16), (220, 18), (240, 19), (260, 21), (280, 23), (300, 24), (320, 25), (360, 29)],
            // Bold Italic (as measured without the slant)
            [(120, 10), (140, 11), (160, 13), (180, 14), (200, 16), (220, 18), (240, 19), (260, 21), (280, 23), (300, 24), (320, 25), (360, 29)],
        ],
        ["Arial Rounded MT Bold"] =
        [
            // Regular
            [(120, 9), (140, 12), (160, 13), (180, 15), (200, 16), (220, 18), (240, 20), (260, 20), (280, 24), (300, 25), (320, 25), (360, 29)],
            // Bold
            [(120, 9), (140, 12), (160, 13), (180, 15), (200, 16), (220, 18), (240, 20), (260, 20), (280, 24), (300, 25), (320, 25), (360, 29)],
            // Italic (as measured without the slant)
            [(120, 9), (140, 12), (160, 13), (180, 15), (200, 16), (220, 18), (240, 20), (260, 20), (280, 24), (300, 25), (320, 25), (360, 29)],
            // Bold Italic (as measured without the slant)
            [(120, 9), (140, 12), (160, 13), (180, 15), (200, 16), (220, 18), (240, 20), (260, 20), (280, 24), (300, 25), (320, 25), (360, 29)],
        ],
        ["Courier New"] =
        [
            // Regular
            [(120, 8), (140, 12), (160, 14), (180, 15), (200, 16), (220, 17), (240, 18), (260, 20), (280, 21), (300, 22), (320, 23), (360, 27)],
            // Bold
            [(120, 11), (140, 12), (160, 14), (180, 16), (200, 16), (220, 17), (240, 18), (260, 20), (280, 22), (300, 23), (320, 23), (360, 27)],
            // Italic (as measured without the slant)
            [(120, 8), (140, 12), (160, 14), (180, 15), (200, 16), (220, 17), (240, 18), (260, 20), (280, 21), (300, 22), (320, 23), (360, 27)],
            // Bold Italic (as measured without the slant)
            [(120, 11), (140, 12), (160, 14), (180, 16), (200, 16), (220, 17), (240, 18), (260, 20), (280, 22), (300, 23), (320, 23), (360, 27)],
        ],
    };
}
