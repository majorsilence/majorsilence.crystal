using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser.Chunks;

namespace Majorsilence.Crystal.Parser;

/// <summary>
/// A cross-tab's grid: the settings Crystal's Customize Style dialog holds, the size of
/// every cell, and the format of every value in it. <see cref="ParseCrossTabObject"/> reads
/// what the grid is built from - the group fields and the summaries - and hands its record
/// span here for how that grid is drawn.
///
/// Records between the tag-185 wrapper and its tag-186 end, in file order:
/// <code>
///   206 ... 207   one column-axis level: level 0 (the grand total) first, then each group
///                 outermost first. Holds the level's tag-229 group record, the 161/162
///                 group-value header cell (not on level 0), and a 167/168 text object, the
///                 label its totals print ("Total").
///   210 ... 211   one row-axis level, laid out the same way.
///   215 ... 216   one grid cell per pairing of a row level with a column level. Its tag-214
///                 child is the cell's box; the 161/162 cell objects inside it are its
///                 summarized values, one per summary, in the order the summaries are
///                 defined.
/// </code>
/// Every 161/162 cell object and 167/168 text object carries the same format records as a
/// field or text object placed in a section (font, numeric format, alignment, border), and
/// a tag-190 placement after its wrapper.
///
/// How each value was established is noted where it is read. The method throughout: change
/// one setting through the runtime's own object model (ReportClientDocument's
/// CrossTabObject.CrossTabFormat.CrossTabStyle), save a copy, and compare the records this
/// parser reads; then confirm the meaning in the runtime's PDF export of the changed copy.
/// The third-party corpus independently carries a family of reports built that way -
/// parking__crosstab_base and one copy per setting - and in each copy the value named below
/// for its setting is the one that changes. (Saving a copy also rewrites a few bytes nothing
/// here reads, so a copy is never identical to its base outside the setting.)
/// </summary>
public sealed partial class RptParser
{
    private const int TagCrossTabDefinition = 184;       // nested in the tag-185 wrapper: 158, then the grid settings
    private const int TagCrossTabColumnLevelStart = 206;
    private const int TagCrossTabColumnLevelEnd = 207;
    private const int TagCrossTabRowLevelStart = 210;
    private const int TagCrossTabRowLevelEnd = 211;
    private const int TagCrossTabGridCellStart = 215;    // wraps a tag-214 box
    private const int TagCrossTabGridCellEnd = 216;
    private const int TagCrossTabGridCellBox = 214;
    private const int TagCrossTabTextStart = 167;        // a text object inside a cross-tab: wraps a nested 165
    private const int TagCrossTabTextEnd = 168;

    /// <summary>
    /// Reads the cross-tab's grid settings, cell boxes and formats into
    /// <paramref name="crossTab"/>. <paramref name="start"/> is the tag-185 wrapper's index
    /// and <paramref name="end"/> the index just past its tag-186.
    /// </summary>
    private static void ReadCrossTabLayout(CrossTabObject crossTab, List<TslvRecord> records,
        int start, int end, ReportBuilder? report)
    {
        ReadCrossTabSettings(crossTab, records[start]);

        int columnLevel = -1, rowLevel = -1;
        bool inColumnLevel = false, inRowLevel = false;
        string? levelField = null;
        ObjectFormat? headerFormat = null;
        ObjectBounds? headerBounds = null;
        string? totalLabel = null;
        ObjectFormat? totalLabelFormat = null;
        string? totalBackColor = null;
        CrossTabCellLayout? gridCell = null;

        for (int i = start + 1; i < end; i++)
        {
            var rec = records[i];
            switch (rec.Tag)
            {
                case TagCrossTabColumnLevelStart:
                case TagCrossTabRowLevelStart:
                    inColumnLevel = rec.Tag == TagCrossTabColumnLevelStart;
                    inRowLevel = !inColumnLevel;
                    if (inColumnLevel) columnLevel++; else rowLevel++;
                    levelField = null; headerFormat = null; headerBounds = null;
                    totalLabel = null; totalLabelFormat = null;
                    totalBackColor = ReadLevelTotalColour(rec);
                    break;

                case TagCrossTabColumnLevelEnd:
                case TagCrossTabRowLevelEnd:
                    var level = new CrossTabAxisLevel
                    {
                        Level = inColumnLevel ? columnLevel : rowLevel,
                        HeaderFormat = headerFormat,
                        HeaderBounds = headerBounds,
                        TotalLabel = totalLabel,
                        TotalLabelFormat = totalLabelFormat,
                        TotalBackColor = totalBackColor,
                    };
                    (inColumnLevel ? crossTab.ColumnLevels : crossTab.RowLevels).Add(level);
                    inColumnLevel = inRowLevel = false;
                    break;

                case TagGroupCondition when inColumnLevel || inRowLevel:
                    string? tableField = rec.ReadMutf8String(0, out _);
                    if (!string.IsNullOrEmpty(tableField))
                        levelField = tableField.Contains('.') ? tableField[(tableField.IndexOf('.') + 1)..] : tableField;
                    break;

                case TagCrossTabGridCellStart:
                    gridCell = ReadGridCellBox(rec);
                    if (gridCell is not null) crossTab.CellLayouts.Add(gridCell);
                    break;

                case TagCrossTabGridCellEnd:
                    gridCell = null;
                    break;

                case TagCrossTabCellStart:
                {
                    var inner = rec.ParseChildren().FirstOrDefault(c => c.Tag == TagFieldObjectStart);
                    var size = inner is not null ? ExtractObjectBounds(inner) : new ObjectBounds(0, 0, 0, 0);
                    var placed = ApplyPlacement(records, i, size);
                    if (gridCell is not null)
                    {
                        // A summarized value: "Sum of Table.Column" or "Sum of @formula".
                        var (fn, column) = inner is not null ? CrossTabSummary(inner) : (null, null);
                        gridCell.SummaryFormats.Add(ReadCrossTabCellFormat(records, ref i, column, fn, report));
                    }
                    else
                    {
                        // A group-value header, "Row #1 Name" - the value is the level's group field.
                        headerBounds = placed;
                        headerFormat = ReadCrossTabCellFormat(records, ref i, levelField, null, report);
                    }
                    break;
                }

                case TagCrossTabTextStart:
                {
                    var (text, format) = ReadCrossTabText(records, ref i);
                    // Level 0 of an axis can hold more than one: the summaries' own labels
                    // when there are several follow the "Total". The first is the total's.
                    if ((inColumnLevel || inRowLevel) && totalLabel is null)
                        (totalLabel, totalLabelFormat) = (text, format);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The settings held in the tail of the wrapper's own records, after their nested
    /// object header.
    ///
    /// tag-184, after its nested tag-158:
    /// <code>
    ///   [0..1]   Show Grid (Int16 BE). [2..3] turns off with it, and [4..7] is 0 in every
    ///            other file but FFFFFFFF in parking__crosstab_show_grid_off; neither is read.
    ///   [8..11]  column-header height, twips (Int32 BE)
    ///   [12..15] row-header width, twips (Int32 BE)
    ///   [16..19] the cross-tab's own left and top, as its tag-190 placement has them
    ///   [21..22] Keep Columns Together, [23..24] Repeat Row Labels, [25..26] Suppress Empty
    ///            Columns, [27..28] Suppress Empty Rows - each the one Int16 its parking__
    ///            copy changes. Not read: none of them changes what a Matrix can draw.
    /// </code>
    /// The two sizes are the header bands as Crystal's own export draws them: Canada-CrossTab
    /// holds 421 and 1678, and its PDF has the column headers 21.05pt tall and the row
    /// headers 83.9pt wide. With the cell margins turned off both drop by the 200 twips of
    /// margin, to 221 and 1478, and the export follows (11.05pt, 73.9pt).
    ///
    /// tag-185, after its nested tag-184:
    /// <code>
    ///   [0..1]   Suppress Column Grand Totals (Int16 BE) - the Total row
    ///   [2..3]   Suppress Row Grand Totals (Int16 BE) - the Total column
    /// </code>
    /// Which total each one hides is from the export, not the names: with the row-grand-total
    /// flag set Canada-CrossTab's Total column is gone and its Total row prints, and the other
    /// way about with the column flag.
    /// </summary>
    private static void ReadCrossTabSettings(CrossTabObject crossTab, TslvRecord wrapper)
    {
        var definition = wrapper.ParseChildren().FirstOrDefault();
        if (definition is null || definition.Tag != TagCrossTabDefinition) return;

        var objectHeader = definition.ParseChildren().FirstOrDefault();
        if (objectHeader is not null && objectHeader.Tag == TagReportObjectHeader)
        {
            // The grid's own size, which the wrapper's direct children do not carry: its
            // tag-158 sits one level further down than a field's.
            if (objectHeader.Data.Length >= 8)
                crossTab.Bounds = crossTab.Bounds with
                {
                    Width = Math.Max(0, objectHeader.ReadInt32BE(0)),
                    Height = Math.Max(0, objectHeader.ReadInt32BE(4)),
                };

            int s = NestedRecordLength(definition.Data, 0);
            if (s > 0 && definition.Data.Length >= s + 16)
            {
                crossTab.ShowGrid = definition.ReadInt16BE(s) != 0;
                crossTab.ColumnHeaderHeightTwips = Math.Max(0, definition.ReadInt32BE(s + 8));
                crossTab.RowHeaderWidthTwips = Math.Max(0, definition.ReadInt32BE(s + 12));
            }
        }

        int t = NestedRecordLength(wrapper.Data, 0);
        if (t > 0 && wrapper.Data.Length >= t + 4)
        {
            crossTab.SuppressColumnGrandTotals = wrapper.ReadInt16BE(t) != 0;
            crossTab.SuppressRowGrandTotals = wrapper.ReadInt16BE(t + 2) != 0;
        }
    }

    /// <summary>
    /// A level's total colour: the level record (206 or 210) wraps one more (204 or 205),
    /// whose tag-203 child starts with the colour as flag, B, G, R - the order the border
    /// record's background uses - flag 0 meaning set. parking__crosstab_row_grandtotal_bgcolor
    /// differs from parking__crosstab_base in that one word of its row axis's level 0,
    /// FFFFFFFF to 0000FFFF, and its export fills its Total row, label cell included, with
    /// RGB (1, 1, 0); the column_grandtotal copy changes the column axis's and fills the
    /// Total column.
    /// </summary>
    private static string? ReadLevelTotalColour(TslvRecord level)
    {
        var inner = level.ParseChildren().FirstOrDefault()?.ParseChildren().FirstOrDefault(c => c.Tag == 203);
        if (inner is null || inner.Data.Length < 4 || inner.Data[0] != 0) return null;
        return $"#{inner.Data[3]:X2}{inner.Data[2]:X2}{inner.Data[1]:X2}";
    }

    /// <summary>
    /// The full length - header and data - of the record starting at <paramref name="pos"/>
    /// in an already-decoded payload, or 0 when no record header can be read there. The
    /// settings in a wrapper's tail start where its nested records end.
    /// </summary>
    private static int NestedRecordLength(byte[] data, int pos)
    {
        if (pos + 2 > data.Length) return 0;
        byte n2 = data[pos];
        int sizeLen = ((n2 >> 6) & 3) switch { 3 => 4, 2 => 2, 1 => 1, _ => 0 };
        int header = 2 + ((n2 & 0x04) != 0 ? 2 : 0) + ((n2 & 0x20) != 0 ? 2 : 0);
        if (pos + header + sizeLen > data.Length) return 0;
        int size = 0;
        for (int k = 0; k < sizeLen; k++)
            size = (size << 8) | data[pos + header + k];
        return header + sizeLen + size;
    }

    /// <summary>
    /// tag-215 → tag-214, a grid cell's box:
    /// <code>
    ///   [0..3]   left margin, twips (Int32 BE)
    ///   [4..7]   top margin, twips
    ///   [8..9]   left, [10..11] top - section-relative twips, as a placement is
    ///   [12..13] width, [14..15] height - the whole cell, margins included
    ///   [16..17] row level, [18..19] column level (0 = grand total)
    /// </code>
    /// Canada-CrossTab's data cell is 1328 x 430 and its PDF draws the grid columns 66.4pt
    /// apart and the rows 21.5pt. "Show Cell Margins" off turns the margins from 100 to 0 and
    /// the cell to 1128 x 230, and the PDF's columns and rows shrink to 56.4pt and 11.5pt, the
    /// values flush with the grid lines where they had sat 5pt inside them. The positions
    /// say where the totals print: parking__crosstab_base's grand-total cells are left of and
    /// above its data cell, and its export prints the Total column first and the Total row
    /// first; Canada-CrossTab's are right and below, and print last.
    /// </summary>
    private static CrossTabCellLayout? ReadGridCellBox(TslvRecord gridCell)
    {
        var box = gridCell.ParseChildren().FirstOrDefault(c => c.Tag == TagCrossTabGridCellBox && c.Data.Length >= 20);
        if (box is null) return null;
        int U16(int o) => (box.Data[o] << 8) | box.Data[o + 1];
        return new CrossTabCellLayout
        {
            MarginLeftTwips = Math.Max(0, box.ReadInt32BE(0)),
            MarginTopTwips = Math.Max(0, box.ReadInt32BE(4)),
            Box = new ObjectBounds(U16(8), U16(10), U16(12), U16(14)),
            RowLevel = U16(16),
            ColumnLevel = U16(18),
        };
    }

    /// <summary>
    /// A cross-tab cell object's format - the same records a placed field object carries,
    /// read the same way (see <see cref="ParseFieldObject"/>), and advances
    /// <paramref name="i"/> to the cell's tag-162 end.
    ///
    /// <paramref name="column"/> is what the cell shows - the summarized column, or the
    /// group field for a header - and decides, as it does for a field, whether the numeric
    /// and date records mean anything: every cell carries both. A count is a number whatever
    /// it counts.
    ///
    /// Alignment differs. 172 of the 191 cell objects in the two corpora leave it unset (0),
    /// and Crystal then prints a summarized value flush right and a group value flush left -
    /// whatever its type: parking__crosstab_base's row headers are the numbers 1, 2, 3 and its export
    /// starts each one 5pt inside the left grid line, while its summaries, and
    /// Canada-CrossTab's, end the cell margin short of the right one.
    /// </summary>
    private static ObjectFormat ReadCrossTabCellFormat(List<TslvRecord> records, ref int i,
        string? column, AggregateFunction? fn, ReportBuilder? report)
    {
        ObjectFormat font = new();
        string? foreColor = null, dateFormat = null;
        bool machineFormats = false;
        NumericFormatRecord? numeric = null, currency = null;
        (byte L, byte R, byte T, byte B, bool Shadow, string? BackColor, int WidthTwips, string? Color)? borders = null;
        byte alignCode = 0;

        for (i++; i < records.Count && records[i].Tag != TagCrossTabCellEnd; i++)
        {
            var r = records[i];
            if (r.Tag == TagFont) font = ExtractFontFormat(r);
            else if (r.Tag == TagFontColourProps) foreColor = ExtractForeColor(r);
            else if (r.Tag == TagObjectProps)
                alignCode = r.ParseChildren().FirstOrDefault(c => c.Tag == TagObjectPropsInner && c.Data.Length >= 3)?.Data[2] ?? 0;
            else if (r.Tag == TagCommonFormat) machineFormats |= ExtractUsesMachineFormats(r);
            else if (r.Tag == TagDateFormat) dateFormat ??= ExtractDateFormat(r);
            else if (r.Tag == TagNumericFormat)
            {
                // The first for a Currency value, the second for any other; as a field's are.
                var record = ExtractNumericFormat(r);
                currency ??= record;
                numeric = record ?? numeric;
            }
            else if (r.Tag == TagObjectBorder) borders ??= ExtractBorders(r);
        }

        bool isCount = fn is AggregateFunction.Count or AggregateFunction.DistinctCount;
        bool isDate = !isCount && report is not null && column is not null
            && report.Fields.OfType<DatabaseField>().Any(f =>
                string.Equals(f.ColumnName, column, StringComparison.OrdinalIgnoreCase) && f.DataType == "DateTime");
        bool isNumber = isCount || fn == AggregateFunction.Percentage
            || (report is not null && column is not null
                && (IsNumericColumn(report, column)
                    || (column.StartsWith('@') && IsNumericFormula(report, column[1..]))));
        if (!isDate || machineFormats) dateFormat = null;

        if (report is not null && report.NumberLanguage is null && numeric is { } lang)
            report.NumberLanguage = LanguageForSeparators(lang.Thousands, lang.DecimalSep);
        if (report is not null && column is not null && currency is not null
            && IsCurrencyValued(report, column, fn))
            numeric = currency;
        // A placed field in a bracketed format leaves room after the number (see
        // BuildNumericFormat); a cross-tab cell does not. Canada-CrossTab's "$230,190.63"
        // ends 4.9pt inside its grid line, which is the cell's 100-twip margin and nothing
        // more - with the space it would end 2.5pt further in.
        string? format = isNumber && !isDate && numeric is { } n
            ? BuildNumericFormat(n, machineFormats, reserveRoom: false)
            : null;
        format ??= dateFormat;
        if (format is null && fn == AggregateFunction.Percentage) format = "0.00'%'";

        var align = alignCode == 0
            ? (fn is not null ? HorizontalAlignment.Right : HorizontalAlignment.Default)
            : AlignmentFromCode(alignCode);

        return font with
        {
            ForeColor = foreColor,
            HAlign = align,
            FormatString = format,
            BorderLeft = borders?.L ?? 0, BorderRight = borders?.R ?? 0,
            BorderTop = borders?.T ?? 0, BorderBottom = borders?.B ?? 0,
            BackColor = borders?.BackColor,
            BorderWidthTwips = borders?.WidthTwips ?? 0,
        };
    }

    /// <summary>
    /// A text object inside a cross-tab - a total's label - read as a placed text object's
    /// text and font are, and advances <paramref name="i"/> to its tag-168 end.
    /// </summary>
    private static (string Text, ObjectFormat Format) ReadCrossTabText(List<TslvRecord> records, ref int i)
    {
        var text = new System.Text.StringBuilder();
        ObjectFormat font = new();
        string? foreColor = null;
        HorizontalAlignment? paragraphAlign = null;
        byte alignCode = 0;
        (byte L, byte R, byte T, byte B, bool Shadow, string? BackColor, int WidthTwips, string? Color)? borders = null;
        for (i++; i < records.Count && records[i].Tag != TagCrossTabTextEnd; i++)
        {
            var r = records[i];
            if (r.Tag == TagFont) font = ExtractFontFormat(r);
            else if (r.Tag == TagFontColourProps) foreColor = ExtractForeColor(r);
            else if (r.Tag == TagObjectBorder) borders ??= ExtractBorders(r);
            else if (r.Tag == TagObjectProps)
                alignCode = r.ParseChildren().FirstOrDefault(c => c.Tag == TagObjectPropsInner && c.Data.Length >= 3)?.Data[2] ?? 0;
            else if (r.Tag == TagTextParagraph) paragraphAlign ??= ExtractParagraphAlignment(r);
            else if (r.Tag == TagTextStaticSection && r.ReadMutf8String(0, out _) is { Length: > 0 } s)
                text.Append(s.TrimEnd('\t', '\n', '\r'));
        }
        return (text.ToString(), font with
        {
            ForeColor = foreColor,
            HAlign = paragraphAlign ?? AlignmentFromCode(alignCode),
            BorderLeft = borders?.L ?? 0, BorderRight = borders?.R ?? 0,
            BorderTop = borders?.T ?? 0, BorderBottom = borders?.B ?? 0,
            BackColor = borders?.BackColor,
            BorderWidthTwips = borders?.WidthTwips ?? 0,
        });
    }
}
