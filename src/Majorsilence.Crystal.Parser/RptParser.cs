using System.Buffers.Binary;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser.Chunks;
using Majorsilence.Crystal.Parser.Decryption;
using Majorsilence.Crystal.Parser.OleStorage;
using Majorsilence.Crystal.Parser.Sections;

namespace Majorsilence.Crystal.Parser;

/// <summary>
/// Main entry point for parsing Crystal Reports .rpt files into a <see cref="ReportDefinition"/>.
///
/// The inflated TSLV stream layout (from CrystalReportsRuntime.jar decompiled Java):
///
/// AreaPair wrappers (flat, sibling records):
///   130/131  = ReportAreaPair   (Report Header + Footer areas)
///   132/133  = PageAreaPair     (Page Header + Footer areas)
///   134/135  = DetailAreaPair
///   136/137  = GroupAreaPair
///
/// Within each AreaPair (in order):
///   155 = AreaPairCode          (area pair type + group level)
///   138 = Area header           (area name, section count)  → end: 139
///   156 = AreaCode              (header vs footer, kind)
///   255 = SectionProperties     (area-level)
///   Section wrappers (one per section):
///     141/142 = ReportHeader section
///     143/144 = ReportFooter section
///     145/146 = PageHeader section
///     147/148 = PageFooter section
///     149/150 = Detail section
///     151/152 = GroupHeader section
///     153/154 = GroupFooter section
///   Section wrapper payload contains NESTED tag-140 (Section header):
///     tag-140 data: int32 height, bool hasRuler, int16u objectCount, string name
///   After section wrapper (flat):
///     157 = SectionCode
///     255 = SectionProperties   (section-level)
///     Objects (count from tag-140):
///       159 = FieldObject start  → payload contains nested 158 (ReportObject bounds)
///       160 = FieldObject end
///       165 = TextObject start   → payload contains nested 158
///       166 = TextObject end
///       170/171 = LineObject
///       172/173 = BoxObject
///
/// Tag-158 (ReportObject header, nested inside object wrappers):
///   int32 width, int32 height, int32 left, int32 top, int32 right, int32 bottom, string name
///   (all big-endian, from DataInputStream convention)
/// </summary>
public sealed class RptParser
{
    // FieldManager / field-definition tags
    private const int TagDbFieldDef = 115;
    private const int TagFormulaFieldDef = 119;
    private const int TagParamFieldDef = 122;
    private const int TagRunningTotalFieldDef = 128;   // tag-128 → tag-126 → tag-113 (same as formula chain)

    private static AggregateFunction MapAggregateFunction(int code) => code switch
    {
        1 => AggregateFunction.Sum,
        2 => AggregateFunction.Count,
        3 => AggregateFunction.DistinctCount,
        4 => AggregateFunction.Minimum,
        5 => AggregateFunction.Maximum,
        6 => AggregateFunction.Average,
        7 => AggregateFunction.StandardDeviation,
        8 => AggregateFunction.Variance,
        _ => AggregateFunction.Sum
    };

    // Crystal ValueType codes (Crystal Reports SDK)
    // Crystal Reports field value type codes (Little-Endian Int16 in TSLV tag-113)
    // Empirically derived: 0x000B=String, 0x0007=Float64/Number
    private static string MapCrValueType(int code) => code switch
    {
        1  => "Boolean",
        2  => "Int16",
        3  => "Int32",
        4  => "Float32",
        5  => "Float64",
        6  => "Currency",
        7  => "Float64",   // "Number" in Crystal UI (double-precision float)
        8  => "DateTime",
        9  => "DateTime",
        10 => "DateTime",
        11 => "String",
        12 => "String",    // Memo
        14 => "String",    // Blob (treat as string for display)
        _  => "String"
    };

    // Parameter type codes differ from field type codes: code 6 = String (not Currency)
    private static string MapParamValueType(int code) => code switch
    {
        1  => "Boolean",
        3  => "Int32",
        6  => "String",
        7  => "Float64",
        9  => "DateTime",
        _  => "String"
    };

    // AreaPair boundary tags
    private const int TagReportAreaStart = 130;
    private const int TagPageAreaStart = 132;
    private const int TagDetailAreaStart = 134;
    private const int TagGroupAreaStart = 136;

    // Area (section) records
    private const int TagAreaHeader = 138;
    private const int TagAreaEnd = 139;
    private const int TagAreaCode = 156;
    private const int TagAreaPairCode = 155;
    private const int TagSectionProperties = 255;
    private const int TagSectionCode = 157;

    // Object records
    private const int TagFont = 8;
    private const int TagReportObjectHeader = 158;
    private const int TagFieldObjectStart = 159;
    private const int TagFieldObjectEnd = 160;
    private const int TagTextObjectStart = 165;
    private const int TagTextObjectEnd = 166;
    private const int TagSubreportObjectStart = 163; // placed subreport; inner report lives in the "Subdocument N" OLE storage
    private const int TagSubreportObjectEnd = 164;
    private const int TagLineObjectStart = 170;
    private const int TagLineObjectEnd = 171;
    private const int TagBoxObjectStart = 172;
    private const int TagBoxObjectEnd = 173;
    private const int TagCrossTabObjectStart = 185;  // cross-tab grid; contains 229 group records and 161 cell objects
    private const int TagCrossTabObjectEnd = 186;
    private const int TagCrossTabCellStart = 161;    // wraps a nested tag-159 with the cell's field reference
    private const int TagCrossTabCellEnd = 162;
    private const int TagPictureObjectStart = 175;   // static picture; image bytes live in the "Embedding N" OLE storage
    private const int TagPictureObjectEnd = 176;
    private const int TagChartObjectStart = 180;     // chart/graph; nested 179→174→158 gives bounds/name
    private const int TagChartObjectEnd = 181;
    private const int TagChartTypeRecord = 284;      // byte[2]: 0x01=Pie confirmed (15 samples); other values unconfirmed, default Column
    private const int TagChartSeriesFieldRecord = 287; // fully-qualified "<Function> of Table.Column" MUTF-8 string
    private const int TagChartDefinitionRecord = 289;  // strings in order: title, bare category field, bare "<Function> of Column" (fallback)
    private const int TagChartDetailFieldDef = 127;    // "on change of group" charts: tag-127 -> tag-126 holds the raw (unaggregated) series field reference
    private const int TagBlobFieldObjectStart = 177; // database blob field rendered as image (barcodes, photos)
    private const int TagBlobFieldObjectEnd = 178;
    private const int TagOleObjectRef = 189;         // Int32 BE at [0] = index N of the "Embedding N" storage
    private const int TagTextStaticSection = 194;   // static text run within a TextObject
    private const int TagTextFieldSection  = 196;   // field/special-field embed within a TextObject
    private const int TagFontColourProps   = 257;   // wrapper record whose tag-256 child holds ARGB foreground color
    private const int TagFontColour        = 256;   // 4-byte ARGB child: byte[0]=A, [1]=R, [2]=G, [3]=B
    private const int TagGroupCondition    = 229;   // group condition field: MUTF-8 "Table.FieldName" at offset 0
    private const int TagObjectProps       = 253;   // ReportObjectProperties wrapper; tag-252 child holds alignment
    private const int TagObjectPropsInner  = 252;   // data[0..1]=lockSection(f()), data[2]=alignment(case())

    public static ParseResult Parse(string filePath)
    {
        try
        {
            using var reader = OleReader.Open(filePath);
            return Parse(reader);
        }
        catch (Exception ex)
        {
            return ParseResult.Failed($"Failed to parse '{filePath}': {ex.Message}");
        }
    }

    public static ParseResult Parse(Stream stream)
    {
        try
        {
            using var reader = OleReader.Open(stream);
            return Parse(reader);
        }
        catch (Exception ex)
        {
            return ParseResult.Failed($"Failed to parse stream: {ex.Message}");
        }
    }

    private static ParseResult Parse(OleReader ole)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        string reportTitle = string.Empty, reportAuthor = string.Empty, reportComments = string.Empty;
        if (ole.HasStream("\x05SummaryInformation"))
            (reportTitle, reportAuthor, reportComments) = SummaryInfoParser.Parse(ole.ReadStream("\x05SummaryInformation"));

        if (ole.HasStream("ReportInfo"))
            _ = new ReportInfoParser().Parse(ole.ReadStream("ReportInfo"));

        if (ole.HasStream("QESession"))
        {
            var qeSession = new QeSessionParser().Parse(ole.ReadStream("QESession"));
            if (!qeSession.IsValid)
                warnings.Add("QESession stream missing or unrecognized QENG header.");
        }

        if (!ole.HasStream("Contents"))
            return ParseResult.Failed("No Contents stream found — not a valid Crystal Reports file.");

        byte[] contents = ole.ReadStream("Contents");
        byte[] inflated = ContentDecryptor.Decrypt(contents);

        List<TslvRecord> records = TslvReader.ReadAll(inflated);

        var report = BuildReport(records, warnings);
        ResolveEmbeddedImages(ole, report, warnings);
        ResolveSubreports(ole, report, warnings);
        if (!string.IsNullOrEmpty(reportTitle)) report.ReportTitle = reportTitle;
        if (!string.IsNullOrEmpty(reportAuthor)) report.Author = reportAuthor;
        if (!string.IsNullOrEmpty(reportComments)) report.ReportComments = reportComments;

        return new ParseResult
        {
            Success = true,
            Report = report.ToModel(),
            Warnings = warnings,
            Errors = errors,
            RawChunks = records
        };
    }

    private static ReportBuilder BuildReport(List<TslvRecord> records, List<string> warnings)
    {
        var report = new ReportBuilder();

        ExtractFields(records, report);
        ExtractGroups(records, report);

        int i = 0;
        while (i < records.Count)
        {
            var rec = records[i];

            switch (rec.Tag)
            {
                // AreaPair containers — scan forward to collect contained areas
                case TagReportAreaStart:
                case TagPageAreaStart:
                case TagDetailAreaStart:
                case TagGroupAreaStart:
                    i = ParseAreaPair(records, i, report, warnings);
                    continue;

                default:
                    break;
            }
            i++;
        }

        return report;
    }

    private static void ExtractFields(List<TslvRecord> records, ReportBuilder report)
    {
        foreach (var rec in records)
        {
            if (rec.Tag == TagDbFieldDef)
            {
                var ch114 = rec.ParseChildren().FirstOrDefault(c => c.Tag == 114);
                if (ch114 == null) continue;
                var ch113 = ch114.ParseChildren().FirstOrDefault(c => c.Tag == 113);
                if (ch113 == null) continue;

                string? name = ch113.ReadMutf8String(0, out int consumed);
                if (string.IsNullOrEmpty(name)) continue;

                int typeCode = ch113.ReadInt16LE(consumed);
                report.Fields.Add(new DatabaseField
                {
                    Name = name,
                    ColumnName = name,
                    TableName = string.Empty,
                    DataType = MapCrValueType(typeCode)
                });
            }
            else if (rec.Tag == TagFormulaFieldDef)
            {
                var ch118 = rec.ParseChildren().FirstOrDefault(c => c.Tag == 118);
                if (ch118 == null) continue;

                var ch113 = ch118.ParseChildren().FirstOrDefault(c => c.Tag == 113);
                if (ch113 == null) continue;

                string? displayName = ch113.ReadMutf8String(0, out _);
                if (string.IsNullOrEmpty(displayName)) continue;

                // tag-113 block occupies (header=8 + data) bytes within ch118.Data
                int blockEnd = 8 + ch113.Data.Length;

                // Layout after block: 2 bytes | alias-string | 3 bytes | formula-text
                _ = ch118.ReadMutf8String(blockEnd + 2, out int aliasConsumed);
                string? formulaText = ch118.ReadMutf8String(blockEnd + 2 + aliasConsumed + 3, out _);

                if (string.IsNullOrEmpty(formulaText)) continue;

                // Every formula (including internal ones skipped below) is recorded by
                // name so section formula hooks (e.g. suppress) can resolve their text.
                report.FormulaTexts[displayName] = formulaText;

                // Route system formulas to the dedicated model properties
                if (displayName == "Record Selection")
                {
                    report.RecordSelectionFormula = formulaText;
                    continue;
                }
                if (displayName == "Group Selection")
                {
                    report.GroupSelectionFormula = formulaText;
                    continue;
                }

                // Skip internal/invisible formula fields (section visibility, sort keys, etc.)
                // These are identifiable by Crystal's internal naming pattern
                if (displayName.Contains("_Visibility") || displayName.StartsWith("Group #"))
                    continue;

                report.Fields.Add(new FormulaField
                {
                    Name = displayName,
                    FormulaText = formulaText,
                    Syntax = FormulaSyntax.Crystal
                });
            }
            else if (rec.Tag == TagParamFieldDef)
            {
                // tag-122 → tag-113 (name + type), rest of raw data contains prompt + pick-list strings
                var ch113 = rec.ParseChildren().FirstOrDefault(c => c.Tag == 113);
                if (ch113 == null) continue;
                string? name = ch113.ReadMutf8String(0, out int nc);
                if (string.IsNullOrEmpty(name)) continue;
                int typeCode = ch113.ReadInt16LE(nc);
                var (prompt, pickList) = ExtractParamPickList(rec.Data, name);
                report.Fields.Add(new ParameterField
                {
                    Name = name,
                    DataType = MapParamValueType(typeCode),
                    PromptText = prompt,
                    PickListValues = pickList
                });
            }
            else if (rec.Tag == TagRunningTotalFieldDef)
            {
                // tag-128 → tag-126 (SummaryFieldDefinitionBase) → tag-113 (FieldDefinition: name + type)
                // After the tag-113 block in ch126.Data: 4-byte FieldDefinitionLocator prefix,
                // then MUTF-8 "Table.ColumnName" (the summarized field), then 6 bytes of header,
                // then Int16 LE aggregate function code.
                var ch126 = rec.ParseChildren().FirstOrDefault(c => c.Tag == 126);
                if (ch126 == null) continue;
                var ch113 = ch126.ParseChildren().FirstOrDefault(c => c.Tag == 113);
                if (ch113 == null) continue;
                string? name = ch113.ReadMutf8String(0, out int consumed);
                if (string.IsNullOrEmpty(name)) continue;
                int typeCode = ch113.ReadInt16LE(consumed);

                int afterTag113 = ch113.StreamOffset + 8 + ch113.Data.Length;
                string summarizedField = string.Empty;
                AggregateFunction function = AggregateFunction.Sum;
                if (afterTag113 + 4 < ch126.Data.Length)
                {
                    string? summarizedFull = ch126.ReadMutf8String(afterTag113 + 4, out int nc2);
                    if (!string.IsNullOrEmpty(summarizedFull))
                    {
                        int dot = summarizedFull.IndexOf('.');
                        summarizedField = dot >= 0 ? summarizedFull[(dot + 1)..] : summarizedFull;
                    }
                    int fnOffset = afterTag113 + 4 + nc2 + 6;
                    if (fnOffset + 2 <= ch126.Data.Length)
                        function = MapAggregateFunction(ch126.ReadInt16LE(fnOffset));
                }

                report.Fields.Add(new RunningTotalField
                {
                    Name = name,
                    DataType = MapCrValueType(typeCode),
                    SummarizedFieldName = summarizedField,
                    Function = function
                });
            }
        }
    }

    private static void ExtractGroups(List<TslvRecord> records, ReportBuilder report)
    {
        int level = 0;
        foreach (var rec in records)
        {
            if (rec.Tag != TagGroupCondition) continue;

            // tag-229 is shared by the report's own groups AND by cross-tab/chart axis
            // definitions (marked "@Row #N Order" / "@Column #N Order" / "@Detail Value
            // Grid #N Order" instead of "@Group #N Order") — skip anything that isn't a
            // real report group, or those axis fields leak in as phantom groups.
            if (!ScanStrings(rec).Any(s => s.StartsWith("@Group #", StringComparison.Ordinal)))
                continue;

            // tag-229 layout: MUTF-8 "Table.FieldName" at offset 0,
            // then Int16 condition code (nc+0..nc+1), Int16 sort direction (nc+2..nc+3)
            string? tableField = rec.ReadMutf8String(0, out int nc);
            if (string.IsNullOrEmpty(tableField)) continue;

            int dot = tableField.IndexOf('.');
            string fieldName = dot >= 0 ? tableField[(dot + 1)..] : tableField;
            if (string.IsNullOrEmpty(fieldName)) continue;

            int condCode = (nc + 1 < rec.Data.Length) ? rec.ReadInt16BE(nc) : 0;
            int sortCode = (nc + 3 < rec.Data.Length) ? rec.ReadInt16BE(nc + 2) : 0;

            // Undocumented 2-byte slot immediately after the sort code, before the
            // "Others" strings — corpus-wide variance scan (crystalcli scan's
            // group-condition-tail detector) found only {0x0000, 0x0101, 0x0202} across
            // 3,350 real report groups, concentrated in multi-page financial-statement/
            // budget reports where repeating the group header on each page is a common
            // real-world need. Treated as a boolean (non-zero = repeat); the two distinct
            // non-zero values may be separate related options that happen to always be
            // set together in this corpus, so this could be conflating RepeatGroupHeader
            // with an adjacent option (e.g. "reprint after horizontal page break").
            int tailStart = nc + 4;
            bool repeatGroupHeader = tailStart + 1 < rec.Data.Length &&
                (rec.Data[tailStart] != 0 || rec.Data[tailStart + 1] != 0);

            GroupSortOrder sort = sortCode switch
            {
                1 => GroupSortOrder.Descending,
                2 => GroupSortOrder.OriginalOrder,
                3 => GroupSortOrder.Specified,
                _ => GroupSortOrder.Ascending
            };

            GroupCondition condition = condCode switch
            {
                1 => GroupCondition.Daily,
                2 or 3 => GroupCondition.Weekly,       // EveryWeek / EveryTwoWeeks → Weekly
                4 or 5 => GroupCondition.Monthly,      // EveryHalfMonth / EveryMonth → Monthly
                6 => GroupCondition.Quarterly,
                7 or 8 => GroupCondition.Annually,     // EveryHalfYear / EveryYear → Annually
                _ => GroupCondition.EachValue
            };

            report.Groups.Add(new GroupDefinition
            {
                Level     = level,
                FieldName = fieldName,
                SortOrder = sort,
                Condition = condition,
                RepeatGroupHeader = repeatGroupHeader,
            });
            level++;
        }
    }

    private static int ParseAreaPair(List<TslvRecord> records, int start, ReportBuilder report, List<string> warnings)
    {
        int endTag = records[start].Tag + 1;  // 130→131, 132→133, 134→135, 136→137
        int groupLevel = 0;  // set from AreaPairCode if present
        int i = start + 1;

        while (i < records.Count && records[i].Tag != endTag)
        {
            var rec = records[i];

            if (rec.Tag == TagAreaPairCode)
            {
                // AreaPairCode: int8u = AreaPairKind, int16u = group level
                if (rec.Data.Length >= 3)
                    groupLevel = rec.ReadInt16BE(1);
                i++;
                continue;
            }

            if (rec.Tag == TagAreaHeader)
            {
                i = ParseArea(records, i, report, warnings, records[start].Tag, groupLevel);
                continue;
            }

            i++;
        }

        return i + 1;  // skip end tag
    }

    private static int ParseArea(List<TslvRecord> records, int start, ReportBuilder report, List<string> warnings,
        int areaPairTag, int groupLevel)
    {
        var areaRec = records[start];

        int i = start + 1;

        // Skip AreaCode (tag 156) immediately after 138
        if (i < records.Count && records[i].Tag == TagAreaCode)
            i++;

        // SectionProperties (tag 255) at area level — e.g. a group footer area split
        // into multiple sub-sections carries "New Page After" once for the whole area,
        // not per sub-section (the per-section tag-255 hook table is empty in that case).
        // Falls back onto every section in the area when the section itself has none.
        var areaHooks = new Dictionary<int, string>();
        if (i < records.Count && records[i].Tag == TagSectionProperties)
        {
            areaHooks = ExtractFormulaHookEntries(records[i]);
            i++;
        }

        // Parse sections until tag 139 (area end)
        while (i < records.Count && records[i].Tag != TagAreaEnd)
        {
            var rec = records[i];
            if (TslvRecord.IsSectionStart(rec.Tag))
            {
                // The section wrapper tag encodes the type directly (141=RH, 143=RF, 145=PH, etc.)
                i = ParseSection(records, i, report, warnings, TslvRecord.SectionKindFromTag(rec.Tag), groupLevel, areaHooks);
                continue;
            }
            i++;
        }

        return i + 1;  // skip tag 139
    }

    private static int ParseSection(List<TslvRecord> records, int start, ReportBuilder report, List<string> warnings,
        SectionKind kind, int groupLevel, Dictionary<int, string>? areaHooks = null)
    {
        var wrapperRec = records[start];
        int endTag = wrapperRec.Tag + 1;

        // Extract nested tag-140 (Section header) from the section wrapper's decoded payload
        int heightTwips = 0;
        int objectCount = 0;
        string sectionName = string.Empty;
        bool suppress = false;

        var children = wrapperRec.ParseChildren();
        var sectionHeader = children.FirstOrDefault(r => r.Tag == 140);
        if (sectionHeader != null)
        {
            // tag-140 data: int32 height, int16u hasRuler, int16u objectCount, string name
            heightTwips = sectionHeader.ReadInt32BE(0);
            if (heightTwips < 0) heightTwips = -heightTwips;
            objectCount = sectionHeader.ReadInt16BE(6);
            sectionName = sectionHeader.ReadMutf8String(8, out _) ?? string.Empty;
        }

        var section = new SectionBuilder
        {
            Kind = kind,
            GroupLevel = groupLevel,
            HeightTwips = heightTwips,
            Suppress = suppress,
            Name = sectionName,
            RepeatGroupHeader = kind == SectionKind.GroupHeader &&
                report.Groups.FirstOrDefault(g => g.Level == groupLevel)?.RepeatGroupHeader == true
        };

        int i = start + 1;  // advance past section wrapper

        // Skip SectionCode (157); read SectionProperties (255) for flags
        while (i < records.Count && records[i].Tag is TagSectionCode or TagSectionProperties)
        {
            if (records[i].Tag == TagSectionProperties)
            {
                var (sup, npb, npa, rpn) = ExtractSectionFlags(records[i]);
                section.Suppress        = sup;
                section.NewPageBefore   = npb;
                section.NewPageAfter    = npa;
                section.ResetPageNumber = rpn;

                var hooks = ExtractFormulaHookEntries(records[i]);
                section.SuppressFormulaName      = hooks.GetValueOrDefault(0) ?? areaHooks?.GetValueOrDefault(0);
                section.NewPageBeforeFormulaName = hooks.GetValueOrDefault(2) ?? areaHooks?.GetValueOrDefault(2);
                section.NewPageAfterFormulaName  = hooks.GetValueOrDefault(3) ?? areaHooks?.GetValueOrDefault(3);
                section.BackColorFormulaName     = hooks.GetValueOrDefault(9) ?? areaHooks?.GetValueOrDefault(9);
            }
            i++;
        }

        // Read objectCount objects
        int parsed = 0;
        while (i < records.Count && records[i].Tag != endTag && parsed < objectCount + 100)
        {
            var rec = records[i];
            if (rec.Tag == TagFieldObjectStart)
            {
                var obj = ParseFieldObject(records, i, out int next, report);
                if (obj != null) section.Objects.Add(obj);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag == TagTextObjectStart)
            {
                var obj = ParseTextObject(records, i, out int next);
                if (obj != null) section.Objects.Add(obj);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag == TagPictureObjectStart)
            {
                var obj = ParsePictureObject(records, i, out int next);
                if (obj != null) section.Objects.Add(obj);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag == TagSubreportObjectStart)
            {
                var obj = ParseSubreportObject(records, i, out int next);
                if (obj != null) section.Objects.Add(obj);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag == TagCrossTabObjectStart)
            {
                var obj = ParseCrossTabObject(records, i, out int next);
                if (obj != null) section.Objects.Add(obj);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag == TagChartObjectStart)
            {
                var obj = ParseChartObject(records, i, out int next);
                if (obj != null) section.Objects.Add(obj);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag is TagLineObjectStart or TagBoxObjectStart)
            {
                // Lines drawn right-to-left / bottom-up carry negative extents in
                // tag-158; magnitude is what matters for the RDL item size.
                // Zero-extent records (no visible shape) are dropped.
                var shapeBounds = ExtractShapeBounds(rec);
                if (shapeBounds.Width > 0 || shapeBounds.Height > 0)
                {
                    Model.Objects.ReportObject shape = rec.Tag == TagLineObjectStart
                        ? new Model.Objects.LineObject { Name = ExtractObjectName(rec), Bounds = shapeBounds }
                        : new Model.Objects.BoxObject { Name = ExtractObjectName(rec), Bounds = shapeBounds };
                    section.Objects.Add(shape);
                }
                int shapeEnd = rec.Tag + 1;
                while (i < records.Count && records[i].Tag != shapeEnd && records[i].Tag != endTag)
                    i++;
                if (i < records.Count && records[i].Tag == shapeEnd) i++;
                parsed++;
                continue;
            }
            if (rec.Tag == TagBlobFieldObjectStart)
            {
                var obj = ParseBlobFieldObject(records, i, out int next);
                if (obj != null) section.Objects.Add(obj);
                i = next;
                parsed++;
                continue;
            }
            // Unknown object type — skip to its end tag
            if (rec.Tag is >= 159 and <= 200 && (rec.Tag % 2 == 1))
            {
                int objEnd = rec.Tag + 1;
                while (i < records.Count && records[i].Tag != objEnd && records[i].Tag != endTag)
                    i++;
                if (i < records.Count && records[i].Tag == objEnd) i++;
                parsed++;
                continue;
            }
            i++;
        }

        // Skip to and past the section end tag
        while (i < records.Count && records[i].Tag != endTag)
            i++;
        if (i < records.Count) i++;

        report.Sections.Add(section);
        return i;
    }

    private static Model.Objects.ReportObject? ParseFieldObject(List<TslvRecord> records, int start,
        out int nextIndex, ReportBuilder? report = null)
    {
        // FieldObject: tag-159 (wrapper containing nested 158), then field-specific records, then tag-160
        var wrapper = records[start];
        var bounds = ExtractObjectBounds(wrapper);
        string name = ExtractObjectName(wrapper);

        // The "Table.FieldName" field reference is embedded in the tag-159 wrapper's decoded
        // payload. Scanning for it gives the actual DB field name (e.g., "Customer Name")
        // rather than the internal Crystal object name (e.g., "TCustomerName1").
        // Summary fields carry a function prefix: "Sum of Table.Column".
        var (tableName, fieldRef) = ExtractFieldRefFull(wrapper);
        AggregateFunction? summaryFunction = null;
        if (tableName is not null && ParseSummaryPrefix(tableName) is var (fn, tableRemainder) && fn is not null)
        {
            summaryFunction = fn;
            tableName = tableRemainder;
        }
        if (!string.IsNullOrEmpty(fieldRef))
        {
            name = fieldRef;
            // Backfill TableName on the matching DatabaseField if we found a table prefix
            if (!string.IsNullOrEmpty(tableName) && report is not null)
            {
                var dbField = report.Fields.OfType<DatabaseField>()
                    .FirstOrDefault(f => string.Equals(f.ColumnName, fieldRef, StringComparison.OrdinalIgnoreCase));
                if (dbField is not null && string.IsNullOrEmpty(dbField.TableName))
                    dbField.TableName = tableName;
            }
        }

        nextIndex = start + 1;
        Model.Objects.ObjectFormat format = new();
        string? foreColor = null;
        HorizontalAlignment hAlign = HorizontalAlignment.Left;
        while (nextIndex < records.Count && records[nextIndex].Tag != TagFieldObjectEnd)
        {
            if (records[nextIndex].Tag == TagFont)
                format = ExtractFontFormat(records[nextIndex]);
            else if (records[nextIndex].Tag == TagFontColourProps)
                foreColor = ExtractForeColor(records[nextIndex]);
            else if (records[nextIndex].Tag == TagObjectProps)
                hAlign = ExtractHAlignment(records[nextIndex]);
            nextIndex++;
        }
        if (nextIndex < records.Count) nextIndex++;
        if (foreColor != null || hAlign != HorizontalAlignment.Left)
            format = new ObjectFormat { FontName = format.FontName, FontSize = format.FontSize, Bold = format.Bold, Italic = format.Italic, Underline = format.Underline, ForeColor = foreColor, HAlign = hAlign };

        return new Model.Objects.FieldObject
        {
            FieldName = name,
            SummaryFunction = summaryFunction,
            Bounds = bounds,
            Format = format
        };
    }

    // A summary field reference reads "<Function> of Table.Column"; because the split
    // in ExtractFieldRefFull happens at the first dot, the function prefix ends up at
    // the start of the table part (e.g. "Sum of Orders"). Observed prefixes: Sum,
    // Count, DistinctCount, Max, Min (plus Average/StdDev/Variance from the Crystal UI).
    // Non-English Crystal versions localize the prefix — unknown prefixes are ignored.
    //
    // Crystal's "Percentage of Total" summary is a *compound* prefix —
    // "Percentage of <Function> of Table.Column" (e.g. "Percentage of Sum of
    // Orders.Order_Amount") — so it's checked first and the inner function/table
    // chain is resolved recursively; the inner function itself is discarded (RDL
    // emission always divides by the DataSet-wide total, see AggregateFunction.Percentage).
    private static (AggregateFunction?, string) ParseSummaryPrefix(string tablePart)
    {
        const string percentPrefix = "Percentage of ";
        if (tablePart.StartsWith(percentPrefix, StringComparison.Ordinal))
        {
            var (_, remainder) = ParseSummaryPrefix(tablePart[percentPrefix.Length..]);
            return (AggregateFunction.Percentage, remainder);
        }

        int sep = tablePart.IndexOf(" of ", StringComparison.Ordinal);
        if (sep <= 0) return (null, tablePart);

        AggregateFunction? fn = tablePart[..sep] switch
        {
            "Sum" => AggregateFunction.Sum,
            "Count" => AggregateFunction.Count,
            "DistinctCount" or "Distinct Count" => AggregateFunction.DistinctCount,
            "Average" or "Avg" => AggregateFunction.Average,
            "Max" or "Maximum" => AggregateFunction.Maximum,
            "Min" or "Minimum" => AggregateFunction.Minimum,
            "StdDev" => AggregateFunction.StandardDeviation,
            "Variance" => AggregateFunction.Variance,
            _ => null
        };
        return fn is null ? (null, tablePart) : (fn, tablePart[(sep + 4)..]);
    }

    // PictureObject: tag-175 (wrapper containing nested 158), then a tag-189 OLE reference
    // whose Int32 BE at offset 0 is the index N of the "Embedding N" storage holding the
    // image bytes, then tag-176. Image data is resolved later when the OLE reader is in scope.
    private static Model.Objects.ReportObject? ParsePictureObject(List<TslvRecord> records, int start, out int nextIndex)
    {
        var wrapper = records[start];
        var bounds = ExtractObjectBounds(wrapper);
        string name = ExtractObjectName(wrapper);

        int embeddingIndex = 0;
        nextIndex = start + 1;
        while (nextIndex < records.Count && records[nextIndex].Tag != TagPictureObjectEnd)
        {
            if (records[nextIndex].Tag == TagOleObjectRef && records[nextIndex].Data.Length >= 4)
                embeddingIndex = records[nextIndex].ReadInt32BE(0);
            nextIndex++;
        }
        if (nextIndex < records.Count) nextIndex++;

        if (embeddingIndex <= 0) return null;
        return new Model.Objects.ImageObject
        {
            Name = name,
            Source = Model.Objects.ImageSourceKind.Embedded,
            EmbeddingIndex = embeddingIndex,
            Bounds = bounds
        };
    }

    // BlobFieldObject: tag-177 (wrapper containing nested 158 and the "Table.FieldName"
    // reference of the blob column), then tag-178. The image comes from the database at
    // render time, so only the field reference is captured.
    private static Model.Objects.ReportObject? ParseBlobFieldObject(List<TslvRecord> records, int start, out int nextIndex)
    {
        var wrapper = records[start];
        var bounds = ExtractObjectBounds(wrapper);
        string name = ExtractObjectName(wrapper);
        var (_, fieldRef) = ExtractFieldRefFull(wrapper);

        nextIndex = start + 1;
        while (nextIndex < records.Count && records[nextIndex].Tag != TagBlobFieldObjectEnd)
            nextIndex++;
        if (nextIndex < records.Count) nextIndex++;

        if (string.IsNullOrEmpty(fieldRef)) return null;
        return new Model.Objects.ImageObject
        {
            Name = name,
            Source = Model.Objects.ImageSourceKind.Database,
            FieldName = fieldRef,
            Bounds = bounds
        };
    }

    // SubreportObject: tag-163 wrapper. The nested tag-158 child gives bounds and the
    // subreport name; the Int32 BE immediately after the tag-158 block (8-byte header
    // + data) is the index N of the "Subdocument N" storage holding the inner report.
    private static Model.Objects.ReportObject? ParseSubreportObject(List<TslvRecord> records, int start, out int nextIndex)
    {
        var wrapper = records[start];
        var bounds = ExtractObjectBounds(wrapper);
        string name = ExtractObjectName(wrapper);

        int subdocIndex = 0;
        var ch158 = wrapper.ParseChildren().FirstOrDefault(r => r.Tag == TagReportObjectHeader);
        if (ch158 is not null)
            subdocIndex = wrapper.ReadInt32BE(8 + ch158.Data.Length);

        nextIndex = start + 1;
        while (nextIndex < records.Count && records[nextIndex].Tag != TagSubreportObjectEnd)
            nextIndex++;
        if (nextIndex < records.Count) nextIndex++;

        if (subdocIndex <= 0) return null;
        return new Model.Objects.SubreportObject
        {
            Name = name,
            SubreportName = name.Length > 0 ? name : $"Subreport{subdocIndex}",
            SubdocumentIndex = subdocIndex,
            Bounds = bounds
        };
    }

    // CrossTabObject: tag-185 wrapper (nested 158 gives bounds/name), then until tag-186:
    //   - tag-229 group records carrying the axis field ("Table.Column" at offset 0) and
    //     an axis marker string "Row #N Name" / "Column #N Name" further in the payload;
    //     records without a leading field reference are grand-total placeholders.
    //   - tag-161/162 cell objects wrapping a nested tag-159 whose field reference is
    //     either an axis placeholder ("Row #N Name") or a summary ("Sum of Table.Column").
    private static Model.Objects.ReportObject? ParseCrossTabObject(List<TslvRecord> records, int start, out int nextIndex)
    {
        var wrapper = records[start];
        var crossTab = new Model.Objects.CrossTabObject
        {
            Name = ExtractObjectName(wrapper),
            Bounds = ExtractObjectBounds(wrapper)
        };

        nextIndex = start + 1;
        while (nextIndex < records.Count && records[nextIndex].Tag != TagCrossTabObjectEnd)
        {
            var rec = records[nextIndex];
            if (rec.Tag == TagGroupCondition)
            {
                string? tableField = rec.ReadMutf8String(0, out _);
                if (!string.IsNullOrEmpty(tableField) && tableField.Contains('.'))
                {
                    string column = tableField[(tableField.IndexOf('.') + 1)..];
                    string? axis = ScanStrings(rec).FirstOrDefault(s =>
                        s.StartsWith("Row #", StringComparison.Ordinal) ||
                        s.StartsWith("Column #", StringComparison.Ordinal));
                    if (axis is not null && axis.StartsWith("Row #", StringComparison.Ordinal))
                        crossTab.RowGroupFields.Add(column);
                    else if (axis is not null)
                        crossTab.ColumnGroupFields.Add(column);
                }
            }
            else if (rec.Tag == TagCrossTabCellStart)
            {
                var ch159 = rec.ParseChildren().FirstOrDefault(c => c.Tag == TagFieldObjectStart);
                if (ch159 is not null)
                {
                    var (table, column) = ExtractFieldRefFull(ch159);
                    if (table is not null && column is not null &&
                        ParseSummaryPrefix(table) is var (fn, _) && fn is not null)
                    {
                        var cell = new Model.Objects.CrossTabCell(column, fn.Value);
                        if (!crossTab.Cells.Contains(cell))   // the same cell repeats for total rows/columns
                            crossTab.Cells.Add(cell);
                    }
                }
                // skip the cell object's interior records
                while (nextIndex < records.Count && records[nextIndex].Tag != TagCrossTabCellEnd)
                    nextIndex++;
            }
            nextIndex++;
        }
        if (nextIndex < records.Count) nextIndex++;

        return crossTab.RowGroupFields.Count > 0 || crossTab.ColumnGroupFields.Count > 0 || crossTab.Cells.Count > 0
            ? crossTab
            : null;
    }

    // ChartObject: tag-180 wrapper, bounds/name nested three levels deep (179 → 174 → 158,
    // one level deeper than every other object type), then flat sibling records until
    // tag-181. tag-284 (5 bytes) byte[2] is the chart-type discriminator: 0x01 was seen for
    // every confirmed pie chart across the corpus (15 independent samples); no other value
    // has a second confirmed sample, so anything else defaults to Column.
    //
    // Two distinct chart data-source modes exist:
    //   Field-bound: tag-289 holds the chart's title (first MUTF-8 string), bare category
    //     field name (second string), and an unqualified "<Function> of Column" series
    //     reference (third string, fallback only). tag-287, when present, holds the
    //     fully-qualified "<Function> of Table.Column" series reference and is preferred.
    //   On-change-of-group ("Detail Value Grid"): one or more flat tag-229 group-condition
    //     records — same record cross-tabs use for their row/column axes — each carrying a
    //     "Table.Column" field reference and an "@Detail Value Grid #N Order" marker string
    //     (distinguishing it from the report's own unrelated groups, which are marked
    //     "@Group #N Order" instead); these become the category axis levels, outermost
    //     first in document order. The series is an *unaggregated* per-row field or formula
    //     reference nested tag-127 → tag-126 (analogous to the tag-128 → tag-126 running-total
    //     chain, but without a function code — Crystal charts the raw detail value, not a
    //     summary). The reference is either "Table.Column" or "@FormulaName".
    private static Model.Objects.ReportObject? ParseChartObject(List<TslvRecord> records, int start, out int nextIndex)
    {
        var wrapper = records[start];
        var inner179 = wrapper.ParseChildren().FirstOrDefault(c => c.Tag == 179);
        var inner174 = inner179?.ParseChildren().FirstOrDefault(c => c.Tag == 174);
        var bounds = inner174 is not null ? ExtractObjectBounds(inner174) : new ObjectBounds(0, 0, 0, 0);
        string name = inner174 is not null ? ExtractObjectName(inner174) : string.Empty;

        string title = string.Empty;
        string fieldBoundCategory = string.Empty;
        var groupCategoryFields = new List<string>();
        string seriesField = string.Empty;
        AggregateFunction seriesFunction = AggregateFunction.Sum;
        Model.Objects.ChartKind kind = Model.Objects.ChartKind.Column;
        bool haveQualifiedSeries = false;

        nextIndex = start + 1;
        while (nextIndex < records.Count && records[nextIndex].Tag != TagChartObjectEnd)
        {
            var rec = records[nextIndex];
            if (rec.Tag == TagChartTypeRecord && rec.Data.Length > 2)
            {
                kind = rec.Data[2] == 1 ? Model.Objects.ChartKind.Pie : Model.Objects.ChartKind.Column;
            }
            else if (rec.Tag == TagChartSeriesFieldRecord)
            {
                string? qualified = ScanStrings(rec).FirstOrDefault(s => s.Contains(" of "));
                if (qualified is not null && ParseSummaryPrefix(qualified) is var (fn, remainder) && fn is not null)
                {
                    seriesFunction = fn.Value;
                    int dot = remainder.IndexOf('.');
                    seriesField = dot > 0 ? remainder[(dot + 1)..] : remainder;
                    haveQualifiedSeries = true;
                }
            }
            else if (rec.Tag == TagChartDefinitionRecord)
            {
                // On-change-of-group charts (tag-229 already seen by this point in
                // document order) put only a redundant axis-label string here when no
                // custom title was set — not a real title — so a lone string is treated
                // as a title only in field-bound mode or when a second string confirms
                // the first one really is a title (as in the two-string case).
                var strings = ScanStrings(rec).ToList();
                if (strings.Count > 1 || (strings.Count > 0 && groupCategoryFields.Count == 0))
                    title = strings[0];
                if (strings.Count > 1) fieldBoundCategory = strings[1];
                if (!haveQualifiedSeries && strings.Count > 2 &&
                    ParseSummaryPrefix(strings[2]) is var (fn2, remainder2) && fn2 is not null)
                {
                    seriesFunction = fn2.Value;
                    seriesField = remainder2;
                }
            }
            else if (rec.Tag == TagGroupCondition)
            {
                string? tableField = rec.ReadMutf8String(0, out _);
                bool isChartAxis = ScanStrings(rec).Any(s => s.Contains("Detail Value Grid"));
                if (isChartAxis && !string.IsNullOrEmpty(tableField) && tableField.Contains('.'))
                    groupCategoryFields.Add(tableField[(tableField.IndexOf('.') + 1)..]);
            }
            else if (rec.Tag == TagChartDetailFieldDef && !haveQualifiedSeries)
            {
                // The series can be a plain "Table.Column" field or an "@FormulaName"
                // reference to a calculated field (e.g. "@amt pos").
                var ch126 = rec.ParseChildren().FirstOrDefault(c => c.Tag == 126);
                string? fieldRef = ch126 is not null
                    ? ScanStrings(ch126).FirstOrDefault(s => s.Contains('.') || s.StartsWith('@'))
                    : null;
                if (fieldRef is not null)
                {
                    if (fieldRef.StartsWith('@'))
                    {
                        seriesField = fieldRef[1..];
                    }
                    else
                    {
                        int dot = fieldRef.IndexOf('.');
                        seriesField = dot > 0 ? fieldRef[(dot + 1)..] : fieldRef;
                    }
                }
            }
            nextIndex++;
        }
        if (nextIndex < records.Count) nextIndex++;

        var categoryFields = groupCategoryFields.Count > 0
            ? groupCategoryFields
            : fieldBoundCategory.Length > 0 ? [fieldBoundCategory] : [];

        if (categoryFields.Count == 0 || seriesField.Length == 0)
            return null;   // not enough to build a usable chart

        return new Model.Objects.ChartObject
        {
            Name = name,
            Bounds = bounds,
            Title = title,
            Kind = kind,
            CategoryFields = categoryFields,
            SeriesField = seriesField,
            SeriesFunction = seriesFunction
        };
    }

    // All MUTF-8 strings locatable in a record's decoded payload (brute-force offsets).
    private static IEnumerable<string> ScanStrings(TslvRecord rec)
    {
        for (int offset = 0; offset + 8 < rec.Data.Length; offset++)
        {
            string? s = rec.ReadMutf8String(offset, out int consumed);
            if (consumed > 0 && !string.IsNullOrEmpty(s) && s.Length >= 3 && s.All(c => c >= 0x20 && c < 0x7F))
                yield return s;
        }
    }

    /// <summary>Sniff the MIME type of raw image bytes; null when the format is unrecognized.</summary>
    private static string? SniffImageMime(byte[] data) => data switch
    {
        [0x42, 0x4D, ..] => "image/bmp",
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        _ => null
    };


    // Resolve embedded picture objects against their "Embedding N/CONTENTS" OLE streams.
    // storagePrefix is "" for the root report and "Subdocument N/" inside subreports.
    private static void ResolveEmbeddedImages(OleReader ole, ReportBuilder report, List<string> warnings,
        string storagePrefix = "")
    {
        foreach (var img in report.Sections
                     .SelectMany(s => s.Objects)
                     .OfType<Model.Objects.ImageObject>()
                     .Where(i => i.Source == Model.Objects.ImageSourceKind.Embedded))
        {
            byte[]? bytes = null;
            int wmfOffset = 0;
            try
            {
                bytes = ole.ReadStreamAt($"{storagePrefix}Embedding {img.EmbeddingIndex}/CONTENTS");
            }
            catch
            {
                // Non-picture OLE embeddings (packages) have no CONTENTS; their
                // \x02OlePres000 presentation stream holds a WMF after a small header.
                try
                {
                    byte[] pres = ole.ReadStreamAt($"{storagePrefix}Embedding {img.EmbeddingIndex}/\x02OlePres000");
                    int found = WmfRasterizer.FindMetafileOffset(pres);
                    if (found >= 0) { bytes = pres; wmfOffset = found; }
                }
                catch { /* fall through to the warning below */ }
            }

            if (bytes is null)
            {
                warnings.Add($"Embedded object {img.EmbeddingIndex} has no CONTENTS or presentation image stream — image skipped.");
                continue;
            }

            string? mime = SniffImageMime(bytes);
            if (mime is not null)
            {
                img.ImageData = bytes;
                img.MimeType = mime;
                continue;
            }

            int metafileOffset = wmfOffset > 0 ? wmfOffset : WmfRasterizer.FindMetafileOffset(bytes);
            if (metafileOffset >= 0)
            {
                byte[]? png = WmfRasterizer.TryRasterizeToPng(bytes, metafileOffset);
                if (png is not null)
                {
                    img.ImageData = png;
                    img.MimeType = "image/png";
                    continue;
                }
                warnings.Add($"Embedded image {img.EmbeddingIndex} is a WMF/EMF metafile that could not be rasterized — image skipped.");
                continue;
            }

            warnings.Add($"Embedded image {img.EmbeddingIndex} has an unrecognized format — image skipped.");
        }
    }

    // Parse each subreport object's "Subdocument N/Contents" stream into a nested
    // ReportDefinition. Subreports can themselves contain images and subreports;
    // recursion is capped defensively.
    private static void ResolveSubreports(OleReader ole, ReportBuilder report, List<string> warnings,
        string storagePrefix = "", int depth = 0)
    {
        foreach (var sub in report.Sections
                     .SelectMany(s => s.Objects)
                     .OfType<Model.Objects.SubreportObject>())
        {
            if (depth >= 3)
            {
                warnings.Add($"Subreport '{sub.SubreportName}': nesting deeper than 3 levels — skipped.");
                continue;
            }

            string innerPrefix = $"{storagePrefix}Subdocument {sub.SubdocumentIndex}/";
            try
            {
                byte[] contents = ole.ReadStreamAt($"{innerPrefix}Contents");
                byte[] inflated = ContentDecryptor.Decrypt(contents);
                var innerRecords = TslvReader.ReadAll(inflated);
                var innerBuilder = BuildReport(innerRecords, warnings);
                ResolveEmbeddedImages(ole, innerBuilder, warnings, innerPrefix);
                ResolveSubreports(ole, innerBuilder, warnings, innerPrefix, depth + 1);
                if (string.IsNullOrEmpty(innerBuilder.ReportTitle))
                    innerBuilder.ReportTitle = sub.SubreportName;
                sub.Report = innerBuilder.ToModel();
            }
            catch (Exception ex)
            {
                warnings.Add($"Subreport '{sub.SubreportName}': failed to parse 'Subdocument {sub.SubdocumentIndex}' — {ex.Message}");
            }
        }
    }

    // Scan the tag-159 wrapper's decoded payload for a "Table.FieldName" MUTF-8 string.
    // Returns (tableName, columnName) — tableName may be null for special/formula fields.
    // Falls back to (null, displayName) for the first meaningful non-dot string.
    private static (string? Table, string? Column) ExtractFieldRefFull(TslvRecord wrapper)
    {
        var data = wrapper.Data;
        string? fallback = null;
        for (int offset = 16; offset + 8 < data.Length; offset++)
        {
            string? s = wrapper.ReadMutf8String(offset, out int nc);
            if (nc <= 0 || string.IsNullOrEmpty(s)) continue;
            int dot = s.IndexOf('.');
            if (dot > 0 && dot < s.Length - 1 && s.Length >= 5)
                return (s[..dot], s[(dot + 1)..]);
            if (fallback is null && s.Length >= 5 && s.All(c => c >= 32 && c < 127))
                fallback = s;
        }
        return (null, fallback);
    }

    private static string? ExtractFieldRef(TslvRecord wrapper) =>
        ExtractFieldRefFull(wrapper).Column;

    private static Model.Objects.ReportObject? ParseTextObject(List<TslvRecord> records, int start, out int nextIndex)
    {
        // TextObject: tag-165 (wrapper containing nested 158), then text paragraph records, then tag-166
        var wrapper = records[start];
        var bounds = ExtractObjectBounds(wrapper);

        // Text content comes from tag-194 (static text) and tag-196 (field reference) records
        // in the flat stream between the 165 wrapper and the 166 end tag.
        // tag-192 = paragraph start; tag-195 = text-section end (no content).
        var text = new System.Text.StringBuilder();
        nextIndex = start + 1;
        Model.Objects.ObjectFormat format = new();
        string? foreColor = null;
        HorizontalAlignment hAlign = HorizontalAlignment.Left;
        while (nextIndex < records.Count && records[nextIndex].Tag != TagTextObjectEnd)
        {
            if (records[nextIndex].Tag == TagFont)
                format = ExtractFontFormat(records[nextIndex]);
            else if (records[nextIndex].Tag == TagFontColourProps)
                foreColor = ExtractForeColor(records[nextIndex]);
            else if (records[nextIndex].Tag == TagObjectProps)
                hAlign = ExtractHAlignment(records[nextIndex]);
            else if (records[nextIndex].Tag == TagTextStaticSection)
            {
                var s = records[nextIndex].ReadMutf8String(0, out _);
                if (!string.IsNullOrEmpty(s))
                    text.Append(s.TrimEnd('\t', '\n', '\r'));
            }
            else if (records[nextIndex].Tag == TagTextFieldSection)
            {
                var s = records[nextIndex].ReadMutf8String(0, out _);
                if (!string.IsNullOrEmpty(s))
                    text.Append($"{{{s}}}");
            }
            nextIndex++;
        }
        if (nextIndex < records.Count) nextIndex++;
        if (foreColor != null || hAlign != HorizontalAlignment.Left)
            format = new ObjectFormat { FontName = format.FontName, FontSize = format.FontSize, Bold = format.Bold, Italic = format.Italic, Underline = format.Underline, ForeColor = foreColor, HAlign = hAlign };

        string name = ExtractObjectName(wrapper);
        return new Model.Objects.TextObject { Text = text.Length > 0 ? text.ToString() : name, Bounds = bounds, Format = format };
    }

    // tag-8 (Font) layout after MUTF-8 name (consumed=nc):
    //   nc+0..nc+3  = charset/pitch flags (e.g. 0x10, 0x00, 0x01, 0x00)
    //   nc+4        = font size in points (1 byte, e.g. 10 = 10pt)
    //   nc+5..nc+8  = E-field int32 BE: bit-1=strikeout, bit-2=italic, bit-4=underline
    //                 (FontDesc.E from Crystal Java source; all-zero = normal)
    //   nc+9..nc+12 = font weight as int32 BE (700=bold, 400=normal)
    private static Model.Objects.ObjectFormat ExtractFontFormat(TslvRecord fontRec)
    {
        if (fontRec.Data.Length < 14) return new();
        string? fontName = fontRec.ReadMutf8String(0, out int nc);
        if (nc <= 0 || fontRec.Data.Length < nc + 13) return new();

        int fontSize = fontRec.Data[nc + 4];
        int eFlags   = fontRec.Data.Length >= nc + 9 ? fontRec.ReadInt32BE(nc + 5) : 0;
        int weight   = fontRec.ReadInt32BE(nc + 9);
        bool italic    = (eFlags & 0x02) != 0;
        bool underline = (eFlags & 0x04) != 0;
        bool bold      = weight >= 600;

        return new Model.Objects.ObjectFormat
        {
            FontName  = fontName,
            FontSize  = fontSize > 0 ? fontSize : null,
            Bold      = bold,
            Italic    = italic,
            Underline = underline,
        };
    }

    // tag-257 (FontColourProperties) wraps a tag-256 child with 4 bytes: A,R,G,B.
    // A=0 means opaque in Crystal's convention. Returns "#RRGGBB" or null if default/black.
    private static string? ExtractForeColor(TslvRecord fontColourProps)
    {
        var ch = fontColourProps.ParseChildren().FirstOrDefault(c => c.Tag == TagFontColour && c.Data.Length >= 4);
        if (ch is null) return null;
        byte r = ch.Data[1], g = ch.Data[2], b = ch.Data[3];
        return (r == 0 && g == 0 && b == 0) ? null : $"#{r:X2}{g:X2}{b:X2}";
    }

    // tag-253 (ReportObjectProperties) → tag-252 child:
    //   data[0..1] = f() lockToSection (Int16 BE bool)
    //   data[2]    = case() alignment code (0/1=left, 2=center, 3=right, 4=justify)
    private static HorizontalAlignment ExtractHAlignment(TslvRecord objProps)
    {
        var ch = objProps.ParseChildren().FirstOrDefault(c => c.Tag == TagObjectPropsInner && c.Data.Length >= 3);
        if (ch is null) return HorizontalAlignment.Left;
        return ch.Data[2] switch
        {
            2 => HorizontalAlignment.Center,
            3 => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
    }

    // tag-255 SectionProperties contains a tag-254 child (53 bytes) with section flags.
    // Layout decoded from Crystal Java SectionProperties.l(ITslvInputRecordArchive):
    //   case() = Int8:
    //     [0]     = AreaPairKind (1=Page, 2=Report, 3=Group, 4=Detail)
    //   f() = Int16 BE for each bool:
    //     [1..2]  = mP (isHeader: 1=header area, 0=footer area)
    //     [3..4]  = mQ (isSection: non-zero=section-level, 0=area-level)
    //     [5..6]  = !suppress (1=visible, 0=suppressed)
    //     [7..8]  = !hideArea (1=visible, 0=hidden)
    //     [9..10] = newPageBefore (non-zero=true)
    //     [11..12]= newPageAfter (non-zero=true)
    //     [13..14]= keepTogether
    //     [15..16]= suppressBlankSection
    //     [17..18]= resetPageNAfter
    //     [19..20]= printAtBottomOfPage
    //     [21..22]= underlay
    //   c() = Int32:
    //     [23..26]= indentAmount/backColour (0xFFFFFFFF = none)
    //   f() = Int16 BE:
    //     [27..28]= freeFormPlacement
    private static (bool suppress, bool newPageBefore, bool newPageAfter, bool resetPageNumber) ExtractSectionFlags(TslvRecord sectionProps)
    {
        var ch = sectionProps.ParseChildren().FirstOrDefault(c => c.Tag == 254);
        if (ch is null || ch.Data.Length < 13) return (false, false, false, false);

        var d = ch.Data;
        bool isSection   = (d[3] != 0 || d[4] != 0);   // mQ non-zero → section-level
        if (!isSection) return (false, false, false, false);    // area-level record has no per-section flags

        bool suppress        = (d[5] == 0 && d[6] == 0);   // !suppress == 0 → suppressed
        bool newPageBefore   = (d[9] != 0 || d[10] != 0);
        bool newPageAfter    = (d[11] != 0 || d[12] != 0);
        bool resetPageNumber = d.Length > 18 && (d[17] != 0 || d[18] != 0);  // resetPageNAfter
        return (suppress, newPageBefore, newPageAfter, resetPageNumber);
    }

    // After the tag-254 child block, the tag-255 payload holds a sequence of formula
    // hook entries — one per formula-drivable section property, in tag-254 flag order.
    // Each entry is a MUTF-8 formula name (empty when no formula is attached, referenced
    // with an '@' prefix) plus 3 trailer bytes. Crystal names these formulas after the
    // property they drive, confirmed corpus-wide via crystalcli scan's
    // suppress-formula-candidate detector: entry 0 = @Section_Visibility (suppress),
    // 2 = @New_Page_Before, 3 = @New_Page_After, 5 = @Suppress_Blank_Section,
    // 6 = @Reset_Page_N_After, 8 = @Underlay_Section, 9 = @Section_Back_Color,
    // 12 = @New_Page_After_N_Records. Only 0, 2, 3, and 9 are wired to model properties;
    // 5/6/8/12 are identified but not yet emitted. Returns all entries in one pass,
    // keyed by index, formula names only (empty entries omitted).
    private static Dictionary<int, string> ExtractFormulaHookEntries(TslvRecord sectionProps)
    {
        var result = new Dictionary<int, string>();
        var ch254 = sectionProps.ParseChildren().FirstOrDefault(c => c.Tag == 254);
        if (ch254 is null) return result;

        int pos = 8 + ch254.Data.Length;
        for (int entry = 0; ; entry++)
        {
            string? name = sectionProps.ReadMutf8String(pos, out int consumed);
            if (consumed <= 0 || name is null) break;   // out of entry space
            pos += consumed + 3;   // 3 trailer bytes per entry
            if (name.Length > 0) result[entry] = name.TrimStart('@');
        }
        return result;
    }

    // Extract ObjectBounds from the nested tag-158 within an object wrapper record
    private static ObjectBounds ExtractObjectBounds(TslvRecord wrapper)
    {
        var children = wrapper.ParseChildren();
        var objHeader = children.FirstOrDefault(r => r.Tag == TagReportObjectHeader);
        if (objHeader == null || objHeader.Data.Length < 16)
            return new ObjectBounds(0, 0, 0, 0);

        // tag-158 data layout:
        //   [0-3]  int32 = object width in twips
        //   [4-7]  int32 = object height in twips
        //   [8-11] int32 = left (X position)
        //   [12-15] int32 = top (Y position)
        //   [16+]  MUTF-8 string = object name
        int width  = objHeader.ReadInt32BE(0);
        int height = objHeader.ReadInt32BE(4);
        int left   = objHeader.ReadInt32BE(8);
        int top    = objHeader.ReadInt32BE(12);
        if (width < 0) width = 0;
        if (height < 0) height = 0;
        if (left < 0) left = 0;
        if (top < 0) top = 0;
        return new ObjectBounds(left, top, width, height);
    }

    // Like ExtractObjectBounds but keeps the magnitude of negative extents
    // (lines/boxes drawn from the far corner) instead of clamping to zero.
    // Shape wrappers nest one level deeper than other objects:
    // tag-170/172 → tag-169 drawing header → tag-158 bounds.
    private static ObjectBounds ExtractShapeBounds(TslvRecord wrapper)
    {
        var children = wrapper.ParseChildren();
        var objHeader = children.FirstOrDefault(r => r.Tag == TagReportObjectHeader)
                        ?? children.SelectMany(c => c.ParseChildren())
                            .FirstOrDefault(r => r.Tag == TagReportObjectHeader);
        if (objHeader == null || objHeader.Data.Length < 16)
            return new ObjectBounds(0, 0, 0, 0);
        int width  = Math.Abs(objHeader.ReadInt32BE(0));
        int height = Math.Abs(objHeader.ReadInt32BE(4));
        int left   = Math.Max(0, objHeader.ReadInt32BE(8));
        int top    = Math.Max(0, objHeader.ReadInt32BE(12));
        return new ObjectBounds(left, top, width, height);
    }

    private static string ExtractObjectName(TslvRecord wrapper)
    {
        var children = wrapper.ParseChildren();
        var objHeader = children.FirstOrDefault(r => r.Tag == TagReportObjectHeader);
        if (objHeader == null || objHeader.Data.Length < 16) return string.Empty;
        // Name starts at offset 16 (after the 4 int32s: width, height, left, top)
        return objHeader.ReadMutf8String(16, out _) ?? string.Empty;
    }

    /// <summary>
    /// Scan the raw tag-122 data for the prompt text and any static pick-list entries.
    ///
    /// Layout (empirically derived from Boyum IT and benbrahim777 corpus files):
    ///   The data is a mix of TSLV child records and raw MUTF-8 strings.
    ///   MUTF-8 strings: BE-Int32 length (including null) + UTF-8 bytes + null.
    ///   The pick-list entries are the longest run of BACK-TO-BACK consecutive strings;
    ///   scattered/isolated strings are internal labels, range refs, or COM refs.
    ///   The prompt text ends with ':' and appears elsewhere in the data.
    ///   Some parameters store value+label pairs (short value, longer label);
    ///   those are detected and paired so SSRS gets both <Value> and <Label>.
    /// </summary>
    private static (string Prompt, System.Collections.Generic.IReadOnlyList<(string Value, string Label)> PickList)
        ExtractParamPickList(byte[] data, string paramName)
    {
        // Pass 1: collect all MUTF-8 strings with their byte offsets
        var all = new System.Collections.Generic.List<(int Start, int End, string Text)>();
        for (int i = 0; i < data.Length - 6; i++)
        {
            int n = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i));
            if (n < 2 || n > 200 || i + 4 + n > data.Length) continue;
            bool ok = true;
            for (int j = i + 4; j < i + 4 + n - 1; j++)
                if (data[j] < 0x20) { ok = false; break; }
            if (!ok || data[i + 4 + n - 1] != 0) continue;
            string s = System.Text.Encoding.UTF8.GetString(data, i + 4, n - 1);
            if (s.Length >= 2 && s.Any(c => char.IsLetter(c)))
                all.Add((i, i + 4 + n, s));
        }

        // Extract prompt text (ends with ':') from anywhere in the data
        string prompt = string.Empty;
        foreach (var (_, _, text) in all)
        {
            if (text.EndsWith(':') && prompt.Length == 0)
                prompt = text[..^1].Trim();
        }

        // Pass 2: find the longest run of CONSECUTIVE strings (each starts exactly where prev ended)
        string nameCore = paramName.Trim('@', '$', '[', ']').Trim();
        var best = new System.Collections.Generic.List<string>();
        var cur  = new System.Collections.Generic.List<string>();
        int prevEnd = -1;
        foreach (var (start, end, text) in all)
        {
            // Skip non-value strings even inside a run
            if (text == paramName || text == nameCore ||
                text.StartsWith("crobj://") || text.EndsWith(':') ||
                text.Contains('.') && text.Contains(' ') == false) // "Table.Column" refs
            {
                prevEnd = -1; cur.Clear();
                continue;
            }
            if (start == prevEnd)
                cur.Add(text);
            else
            {
                cur.Clear();
                cur.Add(text);
            }
            prevEnd = end;
            if (cur.Count > best.Count)
                best = [.. cur];
        }

        if (best.Count < 2)
            return (prompt, System.Array.Empty<(string, string)>());

        // Detect value+label pairs: even count, first half are word-prefixes of second half
        var pickList = new System.Collections.Generic.List<(string Value, string Label)>();
        int half = best.Count / 2;
        bool isPaired = best.Count % 2 == 0 && half >= 2 &&
            Enumerable.Range(0, half).All(i =>
                best[i + half].StartsWith(best[i], System.StringComparison.OrdinalIgnoreCase) &&
                best[i + half].Length > best[i].Length);

        if (isPaired)
        {
            for (int i = 0; i < half; i++)
                pickList.Add((best[i], best[i + half]));
        }
        else
        {
            foreach (var v in best)
                pickList.Add((v, v));
        }

        return (prompt, pickList);
    }

    private static SectionKind DecodeAreaKind(int areaPairTag, TslvRecord areaCodeRec)
    {
        // AreaCode (tag 156): AreaPairCode data + bool isHeader
        // AreaPairCode: int8u = AreaPairKind value, int16u = group level
        if (areaCodeRec.Data.Length < 4) return SectionKind.Unknown;
        bool isHeader = areaCodeRec.ReadInt16BE(3) != 0;  // bool as int16u after AreaPairCode (3 bytes)

        return areaPairTag switch
        {
            TagReportAreaStart => isHeader ? SectionKind.ReportHeader : SectionKind.ReportFooter,
            TagPageAreaStart => isHeader ? SectionKind.PageHeader : SectionKind.PageFooter,
            TagDetailAreaStart => SectionKind.Detail,
            TagGroupAreaStart => isHeader ? SectionKind.GroupHeader : SectionKind.GroupFooter,
            _ => SectionKind.Unknown
        };
    }

    // --- Builder types ---

    private sealed class ReportBuilder
    {
        public string ReportTitle { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string ReportComments { get; set; } = string.Empty;
        public PageLayoutBuilder Page { get; } = new();
        public List<DataSource> DataSources { get; } = [];
        public List<ReportField> Fields { get; } = [];
        public List<GroupDefinition> Groups { get; } = [];
        public List<SortField> SortFields { get; } = [];
        public List<SectionBuilder> Sections { get; } = [];
        public string? RecordSelectionFormula { get; set; }
        public string? GroupSelectionFormula { get; set; }

        /// <summary>All formula display names → Crystal text, including internal formulas
        /// (e.g. section visibility) that are not exposed as report fields.</summary>
        public Dictionary<string, string> FormulaTexts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ReportDefinition ToModel() => new()
        {
            ReportTitle = ReportTitle,
            Author = Author,
            ReportComments = ReportComments,
            CrVersion = 0,  // version extraction not yet implemented
            Page = Page.ToModel(),
            DataSources = DataSources,
            Fields = Fields,
            Groups = Groups,
            SortFields = SortFields,
            Sections = Sections.Select(s => s.ToModel(FormulaTexts)).ToList(),
            RecordSelectionFormula = RecordSelectionFormula,
            GroupSelectionFormula = GroupSelectionFormula
        };
    }

    private sealed class PageLayoutBuilder
    {
        public int WidthTwips { get; set; } = 12240;
        public int HeightTwips { get; set; } = 15840;
        public int TopMarginTwips { get; set; } = 720;
        public int BottomMarginTwips { get; set; } = 720;
        public int LeftMarginTwips { get; set; } = 720;
        public int RightMarginTwips { get; set; } = 720;
        public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;

        public PageLayout ToModel() => new()
        {
            WidthTwips = WidthTwips,
            HeightTwips = HeightTwips,
            TopMarginTwips = TopMarginTwips,
            BottomMarginTwips = BottomMarginTwips,
            LeftMarginTwips = LeftMarginTwips,
            RightMarginTwips = RightMarginTwips,
            Orientation = Orientation
        };
    }

    private sealed class SectionBuilder
    {
        public SectionKind Kind { get; init; }
        public int GroupLevel { get; init; }
        public int HeightTwips { get; set; }
        public bool Suppress { get; set; }
        public bool NewPageBefore { get; set; }
        public bool NewPageAfter { get; set; }
        public bool ResetPageNumber { get; set; }
        public bool RepeatGroupHeader { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? SuppressFormulaName { get; set; }
        public string? NewPageBeforeFormulaName { get; set; }
        public string? NewPageAfterFormulaName { get; set; }
        public string? BackColorFormulaName { get; set; }
        public List<Model.Objects.ReportObject> Objects { get; } = [];

        public Section ToModel(Dictionary<string, string>? formulaTexts = null) => new()
        {
            Type = KindToSectionType(Kind),
            GroupLevel = GroupLevel,
            HeightTwips = HeightTwips,
            Suppress = Suppress,
            NewPageBefore = NewPageBefore,
            NewPageAfter = NewPageAfter,
            ResetPageNumber = ResetPageNumber,
            RepeatGroupHeader = RepeatGroupHeader,
            SuppressFormula = ResolveFormulaText(SuppressFormulaName, formulaTexts),
            NewPageBeforeFormula = ResolveFormulaText(NewPageBeforeFormulaName, formulaTexts),
            NewPageAfterFormula = ResolveFormulaText(NewPageAfterFormulaName, formulaTexts),
            BackColorFormula = ResolveFormulaText(BackColorFormulaName, formulaTexts),
            Objects = Objects
        };

        private static string? ResolveFormulaText(string? name, Dictionary<string, string>? formulaTexts) =>
            name is not null && formulaTexts is not null && formulaTexts.TryGetValue(name, out var text)
                ? text
                : null;

        private static SectionType KindToSectionType(SectionKind k) => k switch
        {
            SectionKind.ReportHeader => SectionType.ReportHeader,
            SectionKind.ReportFooter => SectionType.ReportFooter,
            SectionKind.PageHeader => SectionType.PageHeader,
            SectionKind.PageFooter => SectionType.PageFooter,
            SectionKind.Detail => SectionType.Details,
            SectionKind.GroupHeader => SectionType.GroupHeader,
            SectionKind.GroupFooter => SectionType.GroupFooter,
            _ => SectionType.Details
        };
    }
}
