using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser.Chunks;

namespace Majorsilence.Crystal.Parser;

/// <summary>
/// What a chart's tag-289 definition record holds besides its fields: the chart style and
/// subtype, its text (title, axis titles), its text faces, weights and slants, and its
/// legend and gridline settings.
///
/// Every field here was checked against the Crystal runtime's own object model
/// (ISCRChartObject.ChartStyle, its TextOptions and the internal style interface) over all
/// 84 chart objects in the public and third-party corpora, and the font and legend fields
/// further by changing one setting through that object model, saving a copy and comparing
/// the records:
/// <list type="bullet">
///   <item>Byte 0 is the runtime's CrChartStyleTypeEnum (0 bar, 1 line, 2 area, 3 pie,
///   4 doughnut, ...) and byte 1 the subtype (0 side by side, 1 stacked, 2 percent for a bar;
///   30/31 for a flat or faked-3D pie). Both agreed with the runtime for every chart whose
///   strings are laid out this way.</item>
///   <item>From byte 2, eight length-prefixed strings: title, subtitle, footnote, two that
///   are empty in every file, the group (category) axis title, the data (value) axis title
///   and the series title. The runtime reports the same text, except that it blanks the
///   data title of a pie, which has no value axis.</item>
///   <item>Eight face names, in the order title, subtitle, footnote, group title, data
///   title, series title, legend, group labels.</item>
///   <item>A settings block: byte 1 is ShowLegend, byte 2 the legend position (0 right,
///   1 left, 2 bottom centre, 3 custom), and for bar, line and area charts byte 8 the
///   group-axis and byte 10 the data-axis gridlines (CrGridTypeEnum: 2 major, 3 both).
///   Pies and doughnuts carry pie size and slice detachment at bytes 6 and 7 instead.</item>
///   <item>Two more face names, ten two-byte flags (one per text style), four bytes, two
///   bytes, then the weights block: a weight (400 normal, 700 bold) and an italic byte per
///   text style. Setting the title, axis titles, legend and labels italic or bold one at a
///   time moved exactly the bytes at the offsets below.</item>
/// </list>
/// The sizes of the text are not here; they are in the chart's drawing template stream
/// (<see cref="ChartTemplateReader"/>).
/// </summary>
internal static class ChartDefinitionDecoder
{
    internal sealed record Definition(
        int StyleType,
        int Subtype,
        string Title,
        string GroupTitle,
        string DataTitle,
        bool ShowLegend,
        ChartLegendPosition LegendPosition,
        bool GroupGridLines,
        bool DataGridLines,
        ChartFont TitleFont,
        ChartFont GroupTitleFont,
        ChartFont DataTitleFont,
        ChartFont LegendFont,
        ChartFont GroupLabelFont,
        ChartFont DataLabelFont);

    // Index of each text style in the face-name list and the per-style flag list.
    private const int FontTitle = 0, FontGroupTitle = 3, FontDataTitle = 4, FontLegend = 6, FontGroupLabel = 7;

    // Offsets of each style's weight and italic byte within the weights block. The group
    // title's entry is one byte longer than the others, so everything after it is shifted.
    private static readonly (int Style, int Weight, int Italic)[] WeightOffsets =
    [
        (FontTitle, 0, 2),
        (FontGroupTitle, 15, 18),
        (FontDataTitle, 21, 23),
        (FontLegend, 31, 33),
        (FontGroupLabel, 36, 38),
    ];

    /// <summary>
    /// Decodes a tag-289 record laid out as above; null when it is not (a Gantt or funnel
    /// chart's record starts differently, and its title then is not at byte 2).
    /// </summary>
    internal static Definition? Decode(TslvRecord rec)
    {
        var d = rec.Data;
        if (d.Length < 3) return null;

        int off = 2;
        var strings = new string?[8];
        for (int i = 0; i < strings.Length; i++)
        {
            strings[i] = rec.ReadMutf8String(off, out int used);
            if (used == 0) return null;
            off += used;
        }
        if (strings[0] is null) return null;

        var faces = new string?[8];
        for (int i = 0; i < faces.Length; i++)
        {
            faces[i] = rec.ReadMutf8String(off, out int used);
            if (used == 0 || !IsFaceName(faces[i])) return null;
            off += used;
        }

        int settings = off;
        int styleType = d[0], subtype = d[1];
        bool showLegend = At(d, settings + 1) == 1;
        var legendPosition = At(d, settings + 2) switch
        {
            1 => ChartLegendPosition.Left,
            2 => ChartLegendPosition.Bottom,
            _ => ChartLegendPosition.Right
        };
        // Pies and doughnuts carry two bytes of their own (pie size, slice detachment)
        // before the gridlines, and draw no axes; read gridlines for the axis charts only.
        bool axisChart = styleType is 0 or 1 or 2 or 5;
        bool groupGrid = axisChart && At(d, settings + 8) is 2 or 3;
        bool dataGrid = axisChart && At(d, settings + 10) is 2 or 3;

        var fonts = new ChartFont[8];
        for (int i = 0; i < fonts.Length; i++)
            fonts[i] = new ChartFont { Name = faces[i] };
        ReadWeights(rec, settings, fonts);

        return new Definition(
            styleType, subtype,
            strings[0] ?? string.Empty,
            strings[5] ?? string.Empty,
            strings[6] ?? string.Empty,
            showLegend, legendPosition, groupGrid, dataGrid,
            fonts[FontTitle], fonts[FontGroupTitle], fonts[FontDataTitle], fonts[FontLegend],
            fonts[FontGroupLabel],
            // The value-axis labels' weight was never seen to move with any setting; it
            // takes the group labels' face, which every corpus file has the same as theirs.
            new ChartFont { Name = faces[FontGroupLabel] });
    }

    /// <summary>The chart kind and stacking a Crystal style type and subtype draw as.</summary>
    internal static (ChartKind Kind, ChartStacking Stacking) MapStyle(int styleType, int subtype)
    {
        ChartStacking BarStacking() => (subtype % 3) switch
        {
            1 => ChartStacking.Stacked,
            2 => ChartStacking.PercentStacked,
            _ => ChartStacking.None
        };

        return styleType switch
        {
            // A bar chart is vertical in every corpus file (IsVerticalBar), which RDL calls
            // a column chart. Subtypes 3-5 are the faked-3D forms of 0-2.
            0 => (ChartKind.Column, BarStacking()),
            1 => (ChartKind.Line, ChartStacking.None),
            2 => (ChartKind.Area, ChartStacking.None),
            3 => (ChartKind.Pie, ChartStacking.None),
            4 => (ChartKind.Doughnut, ChartStacking.None),
            // 3D riser: bars drawn in depth.
            5 => (ChartKind.Column, ChartStacking.None),
            _ => (ChartKind.Column, ChartStacking.None)
        };
    }

    private static void ReadWeights(TslvRecord rec, int settings, ChartFont[] fonts)
    {
        // The two further face names come after the settings block, whose length differs
        // by chart type; find them as the first pair of face names after it.
        var d = rec.Data;
        for (int start = settings + 8; start + 8 < d.Length; start++)
        {
            string? first = rec.ReadMutf8String(start, out int used1);
            if (used1 <= 5 || !IsFaceName(first)) continue;
            string? second = rec.ReadMutf8String(start + used1, out int used2);
            if (used2 <= 5 || !IsFaceName(second)) continue;

            // Ten two-byte flags, four bytes, two bytes, then the weights.
            int block = start + used1 + used2 + 20 + 4 + 2;
            foreach (var (style, weightAt, italicAt) in WeightOffsets)
            {
                if (block + italicAt >= d.Length) return;
                int weight = (d[block + weightAt] << 8) | d[block + weightAt + 1];
                // A weight is 100-900 in hundreds; anything else means the layout is not
                // the one decoded here, and the styles stay as they were.
                if (weight is < 100 or > 900 || weight % 100 != 0) return;
                fonts[style].Bold = weight >= 600;
                fonts[style].Italic = d[block + italicAt] == 1;
            }
            return;
        }
    }

    private static int At(byte[] d, int i) => i < d.Length ? d[i] : -1;

    private static bool IsFaceName(string? s) =>
        s is { Length: > 0 and <= 32 } && s.All(c => c >= 0x20 && c < 0x7F);
}
