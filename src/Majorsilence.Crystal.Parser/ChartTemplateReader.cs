using System.Buffers.Binary;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser.Decryption;
using Majorsilence.Crystal.Parser.OleStorage;

namespace Majorsilence.Crystal.Parser;

/// <summary>
/// Reads each chart's drawing template - the "CHART &lt;n&gt;l" streams of a report's
/// storage - for the two things the chart's definition record does not hold: the size of
/// each piece of text, and the colours the series are drawn in.
///
/// A template stream is enciphered and compressed like the Contents stream beside it (same
/// key, same per-file IV, no header of its own). Inflated, it starts "TIFFGRAPH", has a
/// 256-byte header, then little-endian records: a two-byte id, a two-byte length, the
/// payload. There is one stream per chart object, numbered in the order the report stores
/// its charts: in every corpus file the number of streams equals the number of tag-180
/// chart objects, and the graph kind each stream declares (record 0x07D1: 18 for bars, 38
/// for pies) follows the charts' own types in that order.
///
/// <para><b>Text sizes.</b> Record 0x2339 is a text style, keyed by an object id at payload
/// bytes 2-3, with its height at bytes 8-9 in 1/32767ths of the chart's height. Setting
/// a chart's title to 12pt and to 30pt through the runtime's object model stored 1820 and
/// 4550 on a 216pt-tall chart (12 x 32767 / 216 = 1820). The default 2047 is the 13.5pt
/// title Crystal's own PDF export draws, and the same 1031 is 6.8pt labels on a 216pt
/// chart and 8.1pt on a 258pt one, which is what the export draws for each. Changing one
/// text style at a time identified the ids: 0x07DF title, 0x07E2 group (category) labels,
/// 0x07E3 data (value) labels, 0x07E4 legend, 0x07E5 group title, 0x07E6 data title.</para>
///
/// <para><b>Colours.</b> Record 0x2329 for object 0x0854 is one series colour: its index at
/// payload bytes 0-1 and its red, green and blue at bytes 11, 13 and 15. In the current
/// template these are exactly the fills Crystal's export draws, in order (#3A6598, #EFA252,
/// #009470, ...), one per category of a one-series chart. In the older template every entry
/// carries a 1 at byte 4 and the list does not describe the drawing: that chart draws all
/// its bars in one shaded blue although its first entry is maroon. Such a list is not used.</para>
/// </summary>
internal static class ChartTemplateReader
{
    private const int RecordFontStyle = 0x2339;
    private const int RecordFill = 0x2329;
    private const int ObjectSeriesFill = 0x0854;
    private const int HeightScale = 32767;

    internal const int TitleText = 0x07DF;
    internal const int GroupLabelText = 0x07E2;
    internal const int DataLabelText = 0x07E3;
    internal const int LegendText = 0x07E4;
    internal const int GroupTitleText = 0x07E5;
    internal const int DataTitleText = 0x07E6;

    internal sealed record Template(Dictionary<int, int> TextHeights, List<string> Palette, bool PaletteDescribesFills);

    /// <summary>
    /// Fills the template-held parts of each chart: its text sizes and, where the template
    /// describes them, its colours. <paramref name="storagePrefix"/> is "" for the main
    /// report and "Subdocument N/" for a subreport's own storage.
    /// </summary>
    internal static void Apply(OleReader ole, string storagePrefix, IEnumerable<ChartObject> charts,
        List<string> warnings)
    {
        var list = charts.Where(c => c.ChartIndex >= 0).ToList();
        if (list.Count == 0) return;

        List<Template?> templates;
        try
        {
            templates = ReadAll(ole, storagePrefix);
        }
        catch (Exception ex)
        {
            warnings.Add($"Chart templates could not be read ({ex.Message}); charts keep default text sizes and colours.");
            return;
        }

        foreach (var chart in list)
        {
            if (chart.ChartIndex >= templates.Count || templates[chart.ChartIndex] is not { } t)
                continue;
            ApplyTemplate(chart, t);
        }
    }

    internal static void ApplyTemplate(ChartObject chart, Template t)
    {
        double heightPt = chart.Bounds.Height / 20.0;
        void Size(ChartFont font, int id)
        {
            if (heightPt > 0 && t.TextHeights.TryGetValue(id, out int raw) && raw > 0)
                font.SizePt = Math.Round(raw * heightPt / HeightScale, 1);
        }
        Size(chart.TitleFont, TitleText);
        Size(chart.CategoryAxisTitleFont, GroupTitleText);
        Size(chart.ValueAxisTitleFont, DataTitleText);
        Size(chart.LegendFont, LegendText);
        Size(chart.CategoryLabelFont, GroupLabelText);
        Size(chart.ValueLabelFont, DataLabelText);

        if (t.PaletteDescribesFills && t.Palette.Count > 0)
        {
            chart.Palette = t.Palette;
            chart.ColorByCategory = true;
        }
    }

    /// <summary>Every chart template in the storage, in chart order; null where one cannot be read.</summary>
    internal static List<Template?> ReadAll(OleReader ole, string storagePrefix)
    {
        var names = ole.EnumerateEntries(recursive: storagePrefix.Length > 0)
            .Where(e => !e.IsStorage && e.Path.StartsWith(storagePrefix, StringComparison.Ordinal))
            .Select(e => e.Path[storagePrefix.Length..])
            .Where(n => !n.Contains('/'))
            .Select(n => (Name: n, Number: StreamNumber(n)))
            .Where(x => x.Number >= 0)
            .OrderBy(x => x.Number)
            .ToList();
        if (names.Count == 0) return [];

        byte[] contents = ole.ReadStreamAt($"{storagePrefix}Contents");
        var result = new List<Template?>();
        foreach (var (name, _) in names)
        {
            try
            {
                byte[] inflated = ContentDecryptor.DecryptHeaderless(contents, ole.ReadStreamAt($"{storagePrefix}{name}"));
                result.Add(Parse(inflated));
            }
            catch
            {
                result.Add(null);
            }
        }
        return result;
    }

    // "CHART 24l" -> 24; anything else -> -1.
    private static int StreamNumber(string name)
    {
        if (!name.StartsWith("CHART ", StringComparison.Ordinal) || !name.EndsWith('l')) return -1;
        return int.TryParse(name.AsSpan(6, name.Length - 7), out int n) ? n : -1;
    }

    internal static Template? Parse(byte[] data)
    {
        if (data.Length < 0x104 || !data.AsSpan(0, 9).SequenceEqual("TIFFGRAPH"u8)) return null;

        var heights = new Dictionary<int, int>();
        var fills = new SortedDictionary<int, string>();
        bool flagged = false;
        int off = 0x100;
        while (off + 4 <= data.Length)
        {
            int id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 2));
            if (off + 4 + len > data.Length) break;
            var p = data.AsSpan(off + 4, len);

            if (id == RecordFontStyle && len >= 10 && p[0] == 0xFF && p[1] == 0xFF)
            {
                int obj = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
                heights.TryAdd(obj, BinaryPrimitives.ReadUInt16LittleEndian(p[8..]));
            }
            else if (id == RecordFill && len == 16 && BinaryPrimitives.ReadUInt16LittleEndian(p[2..]) == ObjectSeriesFill)
            {
                int index = BinaryPrimitives.ReadUInt16LittleEndian(p);
                fills.TryAdd(index, $"#{p[11]:X2}{p[13]:X2}{p[15]:X2}");
                if (p[4] != 0) flagged = true;
            }
            off += 4 + len;
        }
        return new Template(heights, fills.Values.ToList(), PaletteDescribesFills: !flagged);
    }
}
