using System.Runtime.InteropServices;
using Majorsilence.Crystal.Converter;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// The line pitch of a report set for a printer, against the runtime's own and against GDI's
/// metrics. The runtime's numbers are the gaps between the lines of four-line text objects and
/// multi-line fields in reports set for a 600-dpi printer, exported to PDF. GDI's are the
/// ascent, descent and external leading of the font asked for by its em in pixels, which is
/// the same font at any resolution; that comparison is Windows only, and per family only where
/// the font is installed.
///
/// A failure of the GDI comparison prints the rows GDI gives, in the table's own encoding.
/// </summary>
[TestFixture]
public class PrinterLinePitchesTests
{
    [TestCase("Arial", 8.0, false, false, 9.25)]
    [TestCase("Arial", 8.0, true, false, 9.6)]
    [TestCase("Arial", 9.0, false, false, 10.2)]
    [TestCase("Arial", 10.0, false, false, 11.4)]
    [TestCase("Arial", 10.0, true, false, 11.9)]
    [TestCase("Arial", 10.0, true, true, 11.6)]
    [TestCase("Arial", 7.95, false, false, 9.15)]
    [TestCase("Arial", 36.0, false, false, 41.55)]
    [TestCase("Times New Roman", 9.0, false, false, 10.55)]
    [TestCase("Times New Roman", 10.0, true, false, 11.65)]
    [TestCase("Courier New", 10.0, false, false, 10.55)]
    [TestCase("Verdana", 8.0, true, false, 9.6)]
    [TestCase("Tahoma", 8.0, false, false, 9.75)]
    [TestCase("Calibri", 10.0, false, false, 12.15)]
    public void Pitch_IsTheRuntimes(string family, double size, bool bold, bool italic, double points)
    {
        Assert.That(PrinterLinePitches.PitchPoints(family, size, bold, italic), Is.EqualTo(points).Within(1e-9));
    }

    [Test]
    public void Pitch_OfAFamilyNotHeld_IsNull()
    {
        Assert.That(PrinterLinePitches.PitchPoints("Garamond", 10, false, false), Is.Null);
    }

    private static readonly string[] Families =
        ["Arial", "Arial Black", "Verdana", "Times New Roman", "Courier New",
         "Tahoma", "Calibri", "Impact", "Cambria"];

    private static IEnumerable<TestCaseData> Cases() =>
        from family in Families
        from style in new[] { (false, false), (true, false), (false, true), (true, true) }
        select new TestCaseData(family, style.Item1, style.Item2)
            .SetName($"{family}{(style.Item1 ? " Bold" : "")}{(style.Item2 ? " Italic" : "")}");

    // Every em from the table's first to 72 points at 600 dpi.
    [TestCaseSource(nameof(Cases))]
    public void Pitch_IsGdisOwn(string family, bool bold, bool italic)
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True, "GDI is Windows-only");
        Assume.That(Gdi.FaceFor(family), Is.EqualTo(family).IgnoreCase, $"{family} is not installed");

        var wrong = new List<string>();
        var ascents = new System.Text.StringBuilder();
        var descents = new System.Text.StringBuilder();
        for (int ppem = PrinterLinePitches.FirstPpem; ppem <= 600; ppem++)
        {
            var (ascent, descent, leading) = Gdi.Metrics(family, ppem, bold, italic);
            int gdi = Twips(ascent) + Twips(descent) + Twips(leading);
            double points = ppem * 72.0 / PrinterLinePitches.Dpi;
            int ours = (int)Math.Round(PrinterLinePitches.PitchPoints(family, points, bold, italic)!.Value * 20);
            if (ours != gdi) wrong.Add($"{ppem}px: {ours} against {gdi}");
            if (ppem < PrinterLinePitches.FirstPpem + PrinterLinePitches.RowLength)
            {
                ascents.Append((char)('J' + ascent - Scaled(family, ppem, ascent: true)));
                descents.Append((char)('J' + descent - Scaled(family, ppem, ascent: false)));
            }
        }

        Assert.That(wrong, Is.Empty,
            $"{wrong.Count} sizes differ, among them {string.Join("; ", wrong.Take(5))}. GDI's rows: {ascents} {descents}");
    }

    private static int Twips(int pixels) => (int)Math.Round(pixels * 1440.0 / PrinterLinePitches.Dpi, MidpointRounding.AwayFromZero);

    // The ascent or descent the font's usWinAscent or usWinDescent scales to, before the
    // table's correction, for printing GDI's rows on a failure.
    private static int Scaled(string family, int ppem, bool ascent)
    {
        var (winAscent, winDescent) = family switch
        {
            "Arial" => (1854, 434),
            "Times New Roman" => (1825, 443),
            "Courier New" => (1705, 615),
            "Verdana" => (2059, 430),
            "Impact" => (2066, 432),
            "Arial Black" => (2254, 634),
            "Tahoma" => (2049, 423),
            "Calibri" => (1950, 550),
            _ => (1946, 455),
        };
        return (int)Math.Round((ascent ? winAscent : winDescent) * ppem / 2048.0, MidpointRounding.AwayFromZero);
    }

    private static class Gdi
    {
        private static readonly object Gate = new();
        private static readonly IntPtr Dc = CreateCompatibleDC(IntPtr.Zero);

        public static (int Ascent, int Descent, int Leading) Metrics(string family, int ppem, bool bold, bool italic) =>
            WithFont(family, -ppem, bold, italic, () =>
            {
                GetTextMetricsW(Dc, out var tm);
                return (tm.tmAscent, tm.tmDescent, tm.tmExternalLeading);
            });

        public static string FaceFor(string family) =>
            WithFont(family, -80, false, false, () =>
            {
                var name = new char[64];
                GetTextFaceW(Dc, name.Length, name);
                return new string(name).TrimEnd('\0');
            });

        // A negative height asks for the em; DEFAULT_CHARSET.
        private static T WithFont<T>(string family, int height, bool bold, bool italic, Func<T> read)
        {
            lock (Gate)
            {
                IntPtr font = CreateFontW(height, 0, 0, 0, bold ? 700 : 400, italic ? 1u : 0u,
                    0, 0, 1, 0, 0, 0, 0, family);
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
