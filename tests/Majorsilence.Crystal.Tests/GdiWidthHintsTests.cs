using System.Runtime.InteropServices;
using Majorsilence.Crystal.Converter;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// The hinted widths the converter takes Crystal's line breaks to be measured with, against
/// GDI's own: the sample text's width from GetTextExtentPoint32 at each whole-pixel em, over
/// its width at an em large enough for hinting not to move it. Windows only, and per family
/// only where the font is installed - GDI substitutes another face for a missing one.
///
/// A failure prints the row GDI gives, in the table's own encoding, to paste in its place.
/// </summary>
[TestFixture]
public class GdiWidthHintsTests
{
    private static readonly string[] Families =
        ["Arial", "Arial Black", "Verdana", "Times New Roman", "Courier New",
         "Tahoma", "Calibri", "Impact", "Cambria"];

    // Impact has no bold face, and GDI's made-up one sets every glyph a pixel wider; the
    // table gives it the regular face's widths.
    private static IEnumerable<TestCaseData> Cases() =>
        from family in Families
        from style in new[] { (false, false), (true, false), (false, true), (true, true) }
        where !(family == "Impact" && style.Item1)
        select new TestCaseData(family, style.Item1, style.Item2)
            .SetName($"{family}{(style.Item1 ? " Bold" : "")}{(style.Item2 ? " Italic" : "")}");

    [TestCaseSource(nameof(Cases))]
    public void WidthRatio_IsGdisOwn_ThroughTheTable(string family, bool bold, bool italic)
    {
        AssumeGdiHas(family);

        var wrong = new List<string>();
        var row = new System.Text.StringBuilder();
        int end = GdiWidthHints.FirstPpem + GdiWidthHints.Rows[family][0].Length;
        for (int ppem = GdiWidthHints.FirstPpem; ppem < end; ppem++)
        {
            double gdi = Gdi.Ratio(family, ppem, bold, italic);
            char letter = GdiWidthHints.Letters[Math.Clamp(26 + (int)Math.Round((gdi - 1) / 0.005), 0, 51)];
            row.Append(letter);
            double ours = GdiWidthHints.Ratio(family, bold, italic, ppem);
            if (Math.Abs(ours - gdi) > 0.0026) wrong.Add($"{ppem}px: {ours:0.000} against {gdi:0.000}");
        }

        Assert.That(wrong, Is.Empty,
            $"{wrong.Count} sizes differ, among them {string.Join("; ", wrong.Take(5))}. GDI's row: {row}");
    }

    // Above the table the ratio is taken as 1, and GDI's widths are within two percent of
    // the design ones up to 96 pixels to the em (72pt).
    [TestCaseSource(nameof(Cases))]
    public void WidthRatio_AboveTheTable_IsWithinTwoPercent(string family, bool bold, bool italic)
    {
        AssumeGdiHas(family);

        int start = GdiWidthHints.FirstPpem + GdiWidthHints.Rows[family][0].Length;
        var wrong = new List<string>();
        for (int ppem = start; ppem <= 96; ppem++)
        {
            double gdi = Gdi.Ratio(family, ppem, bold, italic);
            if (Math.Abs(gdi - 1) > 0.02) wrong.Add($"{ppem}px: {gdi:0.000}");
        }

        Assert.That(wrong, Is.Empty, string.Join("; ", wrong.Take(5)));
    }

    private static void AssumeGdiHas(string family)
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True, "GDI is Windows-only");
        Assume.That(Gdi.FaceFor(family), Is.EqualTo(family).IgnoreCase, $"{family} is not installed");
    }

    private static class Gdi
    {
        private static readonly object Gate = new();
        private static readonly IntPtr Dc = CreateCompatibleDC(IntPtr.Zero);

        // The sample's width at ppem pixels to the em, over its width at 4096 scaled down.
        public static double Ratio(string family, int ppem, bool bold, bool italic) =>
            Width(family, ppem, bold, italic) / (Width(family, 4096, bold, italic) * ppem / 4096.0);

        private static int Width(string family, int ppem, bool bold, bool italic) =>
            WithFont(family, -ppem, bold, italic, () =>
            {
                GetTextExtentPoint32W(Dc, GdiWidthHints.Sample, GdiWidthHints.Sample.Length, out var size);
                return size.Cx;
            });

        public static string FaceFor(string family) =>
            WithFont(family, 200, false, false, () =>
            {
                var name = new char[64];
                GetTextFaceW(Dc, name.Length, name);
                return new string(name).TrimEnd('\0');
            });

        // A negative height asks for the em; DEFAULT_CHARSET, DEFAULT_QUALITY.
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
        [DllImport("gdi32", CharSet = CharSet.Unicode)]
        private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int count, out Size size);
        [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern int GetTextFaceW(IntPtr dc, int count, char[] face);

        [StructLayout(LayoutKind.Sequential)]
        private struct Size { public int Cx, Cy; }
    }
}
