using System.Runtime.InteropServices;
using Majorsilence.Crystal.Converter;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// The em sizes the converter emits, against GDI's own: a font created by cell height at
/// one twip to the device unit, as Crystal creates it, and its tmHeight - tmInternalLeading.
/// That is the number Crystal's PDF export writes as the font size, so this is the check
/// that GdiEmSizes still says what GDI says. Windows only, and per family only where the
/// font is installed - GDI substitutes another face for a missing one, and that face's
/// metrics would be no test of the table.
///
/// A failure prints the row GDI gives, in the table's own encoding, to paste in its place.
/// </summary>
[TestFixture]
public class GdiEmSizesTests
{
    // Every family the converter knows, including the one (Cambria) that needs no row.
    private static readonly string[] Families =
        ["Arial", "Arial Black", "Verdana", "Times New Roman", "Courier New",
         "Tahoma", "Calibri", "Impact", "Cambria"];

    private static IEnumerable<TestCaseData> Cases() =>
        from family in Families
        from style in new[] { (false, false), (true, false), (false, true), (true, true) }
        select new TestCaseData(family, style.Item1, style.Item2)
            .SetName($"{family}{(style.Item1 ? " Bold" : "")}{(style.Item2 ? " Italic" : "")}");

    [TestCaseSource(nameof(Cases))]
    public void EmSize_IsGdisOwn_ThroughTheTable(string family, bool bold, bool italic)
    {
        AssumeGdiHas(family);

        var wrong = new List<string>();
        var row = new System.Text.StringBuilder();
        int end = GdiEmSizes.FirstCellTwips + GdiEmSizes.RowLength;
        for (int cell = GdiEmSizes.FirstCellTwips; cell < end; cell++)
        {
            int gdi = Gdi.EmTwips(family, cell, bold, italic);
            int ours = Ours(family, cell, bold, italic);
            if (ours != gdi) wrong.Add($"{cell / 20.0}pt: {ours} against {gdi}");
            int ratioOnly = ours - GdiEmSizes.CorrectionTwips(family, bold, italic, cell);
            row.Append((char)('J' + gdi - ratioOnly));
        }

        Assert.That(wrong, Is.Empty,
            $"{wrong.Count} sizes differ, among them {string.Join("; ", wrong.Take(5))}. GDI's row: {row}");
    }

    // Above the table the ratio is used alone, and GDI agrees with it to within a twip:
    // a twentieth of a point, which is rounding and nothing that can be seen.
    [TestCaseSource(nameof(Cases))]
    public void EmSize_AboveTheTable_IsWithinATwipOfGdis(string family, bool bold, bool italic)
    {
        AssumeGdiHas(family);

        int start = GdiEmSizes.FirstCellTwips + GdiEmSizes.RowLength;
        var wrong = new List<string>();
        for (int cell = start; cell <= 1440; cell++)
        {
            int gdi = Gdi.EmTwips(family, cell, bold, italic);
            int ours = Ours(family, cell, bold, italic);
            if (Math.Abs(ours - gdi) > 1) wrong.Add($"{cell / 20.0}pt: {ours} against {gdi}");
        }

        Assert.That(wrong, Is.Empty, string.Join("; ", wrong.Take(5)));
    }

    private static int Ours(string family, int cellTwips, bool bold, bool italic) =>
        (int)Math.Round(RdlConverter.EmPointsFor(family, cellTwips / 20.0, bold, italic) * 20);

    private static void AssumeGdiHas(string family)
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True, "GDI is Windows-only");
        Assume.That(Gdi.FaceFor(family), Is.EqualTo(family).IgnoreCase, $"{family} is not installed");
    }

    private static class Gdi
    {
        private static readonly object Gate = new();
        private static readonly IntPtr Dc = CreateCompatibleDC(IntPtr.Zero);

        public static int EmTwips(string family, int cellTwips, bool bold, bool italic) =>
            WithFont(family, cellTwips, bold, italic, () =>
            {
                GetTextMetricsW(Dc, out var tm);
                return tm.tmHeight - tm.tmInternalLeading;
            });

        public static string FaceFor(string family) =>
            WithFont(family, 200, false, false, () =>
            {
                var name = new char[64];
                GetTextFaceW(Dc, name.Length, name);
                return new string(name).TrimEnd('\0');
            });

        // A positive height asks for the cell height; DEFAULT_CHARSET, ANTIALIASED_QUALITY.
        private static T WithFont<T>(string family, int cellTwips, bool bold, bool italic, Func<T> read)
        {
            lock (Gate)
            {
                IntPtr font = CreateFontW(cellTwips, 0, 0, 0, bold ? 700 : 400, italic ? 1u : 0u,
                    0, 0, 1, 0, 0, 4, 0, family);
                IntPtr old = SelectObject(Dc, font);
                try { return read(); }
                finally { SelectObject(Dc, old); DeleteObject(font); }
            }
        }

        [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
            int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
            uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern bool GetTextMetricsW(IntPtr dc, out TextMetric tm);
        [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern int GetTextFaceW(IntPtr dc, int count, char[] face);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TextMetric
        {
            public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading,
                tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
            public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
            public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
        }
    }
}
