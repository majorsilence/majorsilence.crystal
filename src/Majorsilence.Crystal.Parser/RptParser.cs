using System.Buffers.Binary;
using System.Text.RegularExpressions;
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
/// The inflated TSLV stream layout:
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
///   int32 width, int32 height, int32 (zero), int32 (zero), string name
///   (all big-endian, from DataInputStream convention). Size only - no position.
/// Tag-190 (ObjectPlacement, the record immediately after each object wrapper):
///   uint16 left, uint16 top, in twips, relative to the section.
/// </summary>
public sealed class RptParser
{
    // FieldManager / field-definition tags
    /// <summary>
    /// Crystal's per-object and per-section format formulas. These arrive through the
    /// same record tag as user formulas but are not fields: they drive the Format Editor
    /// (a tooltip, a font size, a page break) rather than producing a row value, and one
    /// report carries as many copies as it has formatted objects - which is how they were
    /// found, as thousands of "Field X has duplicates" errors across 969 corpus files.
    ///
    /// Named rather than detected structurally because nothing in the record separates
    /// them from a user formula. The list is safe against catching a real one: across
    /// every occurrence of these names in both corpora, not one was referenced by another
    /// formula or placed as an object, so none could have contributed to a rendered
    /// report. It is not claimed to be complete - the last two were found in the public
    /// corpus after the rest came from the private one - but an unlisted hook cannot
    /// produce a duplicate field, because the converter refuses to emit a name twice. It
    /// merely leaks one unused field into the DataSet.
    /// </summary>
    private static readonly HashSet<string> ObjectFormatHookNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tool_Tip_Text", "Hyperlink_Text", "Display_String",
        "Font_Style", "Font_Size", "Font_Color", "Font_Name",
        "Top_Line_Style", "Bottom_Line_Style",
        "Currency_Symbol", "Currency_Symbol_Type", "Negative_Type",
        "Date_Order", "Date_First_Separator", "Date_Second_Separator",
        "Suppress_If_Zero", "Suppress_Blank_Section", "Section_Back_Color",
        "New_Page_Before", "New_Page_After", "New_Page_After_N_Records",
        "Reset_Page_N_After",
        "Running Total Condition Formula", "Group Sort Order Formula",
    };

    private const int TagDbFieldDef = 115;
    private const int TagFormulaFieldDef = 119;
    private const int TagParamFieldDef = 122;
    private const int TagCustomFunctionDef = 335;
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

    // Crystal Reports field value type codes (Little-Endian Int16 in TSLV tag-113).
    // Empirically derived, and codes 8 and 15 confirmed against the column *names* they
    // carry across a 120-file sample: every code-8 column is a yes/no flag (active,
    // approved, isEft, sendEftEmail, namealtered) and every code-15 one is a date
    // (asOfDate, dueDate, chqDate, changeDate). Code 8 previously said DateTime, which
    // left boolean columns non-boolean to the engine — "AND/OR operations require both
    // sides to be boolean expressions" for `{active} And {other}` — and code 15 fell
    // through to String, hiding real dates from the date-function overloads.
    private static string MapCrValueType(int code) => code switch
    {
        1  => "Boolean",
        2  => "Int16",
        3  => "Int32",
        4  => "Float32",
        5  => "Float64",
        6  => "Currency",
        7  => "Float64",   // "Number" in Crystal UI (double-precision float)
        8  => "Boolean",
        9  => "DateTime",  // Date-only
        10 => "DateTime",
        11 => "String",
        12 => "String",    // Memo
        13 => "String",    // Memo
        14 => "String",    // Blob (treat as string for display)
        15 => "DateTime",
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
    /// <summary>
    /// Object placement. Follows every object wrapper record as its immediate sibling,
    /// and carries the position the wrapper itself does not: two big-endian UInt16s,
    /// left then top, in twips, relative to the section's own top-left corner.
    /// </summary>
    private const int TagObjectPlacement = 190;
    private const int TagObjectBorder = 237;         // wraps a tag-236 child - see ExtractBorders

    /// <summary>
    /// Page setup. Two big-endian Int32s, page width then height in twips, already
    /// resolved for orientation - a landscape report stores the wider value first, so
    /// there is no separate flag to read.
    /// </summary>
    private const int TagPageSetup = 398;
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
    /// <summary>
    /// Paragraph start within a TextObject. Always 23 bytes, and data[12] is the
    /// paragraph's horizontal alignment using the same case() codes as the object-level
    /// record: 1=left, 2=center, 3=right, 4=justify.
    ///
    /// This is where a text object's alignment actually lives. The object-level record
    /// reads 0 - unset - for four fifths of the text objects in the corpus, and where
    /// the two are both set and disagree the real engine follows the paragraph.
    /// </summary>
    private const int TagTextParagraph = 192;
    private const int TagTextStaticSection = 194;   // static text run within a TextObject
    private const int TagTextFieldSection  = 196;   // field/special-field embed within a TextObject
    private const int TagFontColourProps   = 257;   // wrapper record whose tag-256 child holds ARGB foreground color
    private const int TagFontColour        = 256;   // 4-byte ARGB child: byte[0]=A, [1]=R, [2]=G, [3]=B
    private const int TagGroupCondition    = 229;   // group condition field: MUTF-8 "Table.FieldName" at offset 0
    /// <summary>
    /// Date format. Exactly one per field object, wrapping a tag-242 child whose
    /// data[0] is the date order and data[17] the separator character:
    ///   0 = year-month-day, 1 = whatever the machine's own short date is, 2 = month-day-year.
    /// Order 1 is the common case and is not a format at all - it defers to Windows, which
    /// is why the same report renders 2000-12-09 here and 12/09/2000 elsewhere.
    /// data[4] (numeric month) and data[6] (four-digit year) are the same in every
    /// explicitly-ordered record in either corpus, so nothing else needs reading yet.
    /// </summary>
    private const int TagDateFormat        = 243;
    private const int TagDateFormatInner   = 242;
    private const int TagNumericFormat     = 249;
    private const int TagNumericFormatInner = 248;
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

        ExtractPageSetup(records, report);
        ExtractFields(records, report);
        ExpandCustomFunctionCalls(records, report);
        BackfillTableNamesFromFormulas(report);
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

    // Page size was hardcoded to US Letter portrait before this record was identified,
    // which put every landscape report - 17 of the 88 sample files, and about one in five
    // of the larger set - on a page of the wrong shape, and every A4 or Legal report on one
    // of the wrong size. Object positions are relative to the page body, so getting the
    // body wrong misplaces everything inside it.
    //
    // Only the two dimensions are taken. The 32 bytes that follow them are byte-for-byte
    // identical in every file that carries the record, which is what a "use the printer's
    // defaults" sentinel looks like and not what per-report margins would look like, so
    // margins keep their default. Not every file carries the record either; those keep
    // the default page as well.
    private static void ExtractPageSetup(List<TslvRecord> records, ReportBuilder report)
    {
        var rec = records.FirstOrDefault(r => r.Tag == TagPageSetup);
        if (rec is null || rec.Data.Length < 8) return;

        int width = rec.ReadInt32BE(0);
        int height = rec.ReadInt32BE(4);

        // A plausibility floor rather than a range: label stock as small as one inch
        // square is in the corpus, so only nonsense is rejected.
        if (width < 720 || height < 720) return;

        report.Page.WidthTwips = width;
        report.Page.HeightTwips = height;
        report.Page.Orientation = width > height
            ? PageOrientation.Landscape
            : PageOrientation.Portrait;
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

                // Layout after the block is a 2-byte big-endian count of the fields this
                // formula depends on, that many length-prefixed dependency strings, and
                // then the formula text itself. The count is genuinely 0 for a formula
                // that references nothing else — "whileprintingrecords; ..." counters,
                // pure literal labels, and the SAP "switch({@X_Language} = 'DK', ...)"
                // localization formulas whose only reference is to another *formula*.
                // This used to assume exactly one dependency string plus a fixed 3-byte
                // gap, so in the zero-dependency case it read the formula body itself as
                // the dependency, found nothing after it, and dropped the formula
                // entirely (45 of 76 formulas in boyum__ProductionOrder.rpt, including
                // every Title_* label; anything referencing them then failed to resolve).
                // The 3-byte gap only follows a non-empty dependency list.
                int off = blockEnd;
                int depCount = ch118.ReadInt16BE(off);
                off += 2;
                for (int i = 0; i < depCount; i++)
                {
                    _ = ch118.ReadMutf8String(off, out int depConsumed);
                    if (depConsumed <= 0) break;
                    off += depConsumed + 3;   // 3 filler bytes follow each dependency entry
                }

                string? formulaText = ch118.ReadMutf8String(off, out _);

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
                if (displayName.Contains("_Visibility") || displayName.StartsWith("Group #")
                    || ObjectFormatHookNames.Contains(displayName))
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

    // {Table.Column} / bare Table.Column, same two shapes FormulaTranspiler resolves at
    // convert time. Bare identifiers only — Crystal field names with spaces always need
    // the {...} wrapper to parse, so a bare match can't accidentally span into ordinary
    // formula text like "a.b" from unrelated syntax.
    private static readonly Regex BracedTableColumn =
        new(@"\{([A-Za-z_][A-Za-z0-9_ ]*)\.([A-Za-z_][A-Za-z0-9_ ]*)\}", RegexOptions.Compiled);
    private static readonly Regex BareTableColumn =
        new(@"(?<![{@#?.\w])([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)(?!\w)", RegexOptions.Compiled);
    private static readonly Regex BracedParameterRef =
        new(@"\{\?([^}\r\n]+)\}", RegexOptions.Compiled);

    // DatabaseField.TableName is normally backfilled from a placed FieldObject's
    // "Table.Column" reference (see ParseFieldObject) — but a column that's only ever
    // reached *indirectly*, through a formula (e.g. formula "Status" with body
    // "{Header.Status}", with only the formula placed on the report, never the raw
    // column), never goes through that path and TableName stays permanently empty.
    // WriteDataSets needs at least one real table name to build a usable query instead
    // of the unresolved "SELECT * FROM <TableName>" placeholder, so scan formula bodies
    // for the same "Table.Column" shape too.
    // ── Custom functions (tag 335) ──────────────────────────────────────────────
    // A report-embedded custom function lives in a tag-335 record with the same
    // 118>113 name layout as a tag-119 formula. The *source* is stored XOR-obfuscated
    // with key 0x76 (a 0x76 byte therefore decodes to NUL and terminates the text;
    // there is also a separate XOR-0x07 copy of the name, unused here). The source is
    // a complete "Function (StringVar a, NumberVar b, ...) <body>" declaration in
    // ordinary Crystal syntax. Since RDL has no per-report function library, calls are
    // *inlined*: each call site in every formula body is replaced with the function
    // body wrapped in parens, with argument text substituted for parameter names —
    // after which the normal transpiler pipeline handles the result like any other
    // hand-written formula (including degrading variable-using bodies to "").

    private static readonly Regex CustomFnSignature = new(
        @"^\s*Function\s*\((?<params>[^)]*)\)\s*(?<body>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CustomFnParam = new(
        @"(\w+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void ExpandCustomFunctionCalls(List<TslvRecord> records, ReportBuilder report)
    {
        var functions = new Dictionary<string, ((string Name, string? Default)[] Params, string Body)>(StringComparer.OrdinalIgnoreCase);
        byte[] needle = "Function".Select(c => (byte)(c ^ 0x76)).ToArray();

        foreach (var rec in records.Where(r => r.Tag == TagCustomFunctionDef))
        {
            var ch118 = rec.ParseChildren().FirstOrDefault(c => c.Tag == 118);
            var ch113 = ch118?.ParseChildren().FirstOrDefault(c => c.Tag == 113);
            string? name = ch113?.ReadMutf8String(0, out _);
            if (string.IsNullOrEmpty(name)) continue;

            var d = rec.Data;
            int start = -1;
            for (int i = 0; i + needle.Length <= d.Length && start < 0; i++)
            {
                bool hit = true;
                for (int k = 0; k < needle.Length; k++)
                    if (d[i + k] != needle[k]) { hit = false; break; }
                if (hit) start = i;
            }
            if (start < 0) continue;

            var sb = new System.Text.StringBuilder();
            for (int i = start; i < d.Length && d[i] != 0x76; i++)
                sb.Append((char)(d[i] ^ 0x76));

            var m = CustomFnSignature.Match(sb.ToString());
            if (!m.Success) continue;
            // A parameter is "[Optional] TypeVar [range] name [:= default]" — the name is
            // the last identifier before any ":=" default expression.
            var paramList = m.Groups["params"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p =>
                {
                    int def = p.IndexOf(":=", StringComparison.Ordinal);
                    string? defaultText = def >= 0 ? p[(def + 2)..].Trim() : null;
                    string decl = def >= 0 ? p[..def].Trim() : p;
                    return (Name: CustomFnParam.Match(decl).Groups[1].Value, Default: defaultText);
                })
                .Where(p => p.Name.Length > 0)
                .ToArray();
            functions[name] = (paramList, m.Groups["body"].Value.Trim());
        }

        if (functions.Count == 0) return;

        foreach (var formula in report.Fields.OfType<FormulaField>().ToList())
        {
            if (string.IsNullOrEmpty(formula.FormulaText)) continue;
            string text = formula.FormulaText;
            // Nested/repeated calls: keep expanding until stable, with a hard cap so a
            // self-referencing function can't loop forever.
            for (int pass = 0; pass < 10; pass++)
            {
                string next = ExpandOnce(text, functions);
                if (next == text) break;
                text = next;
            }
            formula.FormulaText = text;
        }
    }

    private static string ExpandOnce(string text, Dictionary<string, ((string Name, string? Default)[] Params, string Body)> functions)
    {
        foreach (var (name, fn) in functions)
        {
            int idx = 0;
            while ((idx = text.IndexOf(name, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                // Whole-identifier match followed by an argument list
                bool leftOk = idx == 0 || (!char.IsLetterOrDigit(text[idx - 1]) && text[idx - 1] != '_');
                int p = idx + name.Length;
                while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
                if (!leftOk || p >= text.Length || text[p] != '(') { idx += name.Length; continue; }

                // Balanced-paren argument extraction (quote-aware)
                int depth = 0, argStart = p + 1, end = -1;
                var args = new List<string>();
                bool inStr = false; char q = '"';
                for (int i = p; i < text.Length; i++)
                {
                    char c = text[i];
                    if (inStr) { if (c == q) inStr = false; continue; }
                    if (c == '"' || c == '\'') { inStr = true; q = c; continue; }
                    if (c == '(') depth++;
                    else if (c == ')')
                    {
                        depth--;
                        if (depth == 0) { args.Add(text[argStart..i]); end = i; break; }
                    }
                    else if (c == ',' && depth == 1) { args.Add(text[argStart..i]); argStart = i + 1; }
                }
                if (end < 0 || args.Count > fn.Params.Length) { idx += name.Length; continue; }
                // Omitted trailing arguments take the signature's Optional defaults.
                bool defaultsOk = true;
                for (int a = args.Count; a < fn.Params.Length; a++)
                {
                    if (fn.Params[a].Default is null) { defaultsOk = false; break; }
                    args.Add(fn.Params[a].Default!);
                }
                if (!defaultsOk) { idx += name.Length; continue; }

                // A body that *assigns* (":=") mutates its parameters/locals — that's a
                // procedure, not an expression, and textual substitution would produce
                // nonsense like "(2) := 2". Degrade the call to its first argument
                // (identity beats blank for the format-style functions this shape is,
                // and both beat a fatal error); no-arg procedures degrade to "".
                string body = fn.Body.Contains(":=")
                    ? (args.Count > 0 ? args[0].Trim() : "\"\"")
                    : fn.Body;
                if (!fn.Body.Contains(":="))
                    for (int a = 0; a < fn.Params.Length; a++)
                        body = Regex.Replace(body, $@"(?<![\w])({Regex.Escape(fn.Params[a].Name)})(?![\w])",
                            $"({args[a].Trim()})", RegexOptions.IgnoreCase);

                text = text[..idx] + "(" + body + ")" + text[(end + 1)..];
                idx += body.Length + 2;
            }
        }
        return text;
    }

    private static void BackfillTableNamesFromFormulas(ReportBuilder report)
    {
        var dbFields = report.Fields.OfType<DatabaseField>().ToList();
        var allColumns = dbFields.Select(f => f.ColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var synthesizedFields = new List<DatabaseField>();

        // ToList: Backfill adds synthesized fields to report.Fields mid-loop.
        foreach (var formula in report.Fields.OfType<FormulaField>().ToList())
        {
            if (string.IsNullOrEmpty(formula.FormulaText)) continue;

            foreach (Match m in BracedTableColumn.Matches(formula.FormulaText))
                Backfill(m.Groups[1].Value, m.Groups[2].Value, canSynthesize: true);
            foreach (Match m in BareTableColumn.Matches(formula.FormulaText))
                Backfill(m.Groups[1].Value, m.Groups[2].Value, canSynthesize: false);
        }

        // {?Name} parameter references get the same treatment as missing columns: SAP
        // Business One injects some parameters at print time ({?ObjectId@} most
        // prominently) without ever declaring them in the report's own parameter list,
        // and RDL rejects a Parameters! reference with no matching ReportParameter
        // outright. Synthesize a declaration for any referenced-but-undeclared name.
        var paramNames = report.Fields.OfType<ParameterField>()
            .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var synthesizedParams = new List<ParameterField>();
        foreach (var formula in report.Fields.OfType<FormulaField>().ToList())
        {
            if (string.IsNullOrEmpty(formula.FormulaText)) continue;
            foreach (Match m in BracedParameterRef.Matches(formula.FormulaText))
            {
                string pname = m.Groups[1].Value.Trim();
                if (paramNames.Contains(pname)) continue;
                var sp = new ParameterField { Name = pname, DataType = "String" };
                report.Fields.Add(sp);
                paramNames.Add(pname);
                synthesizedParams.Add(sp);
            }
        }

        void Backfill(string table, string column, bool canSynthesize)
        {
            var dbField = dbFields.FirstOrDefault(f =>
                string.Equals(f.ColumnName, column, StringComparison.OrdinalIgnoreCase));
            if (dbField is not null)
            {
                if (string.IsNullOrEmpty(dbField.TableName))
                    dbField.TableName = table;
                return;
            }

            // A formula can reference a column the report's own field dictionary never
            // lists (the SAP CompanyInfo_* blocks are all like this — the *formula* is in
            // the dictionary, the raw {CompanyInfo.AddressFull} column behind it isn't).
            // Crystal treats those as ordinary database columns, so synthesize a
            // DatabaseField for each: without it the transpiled Fields!AddressFull.Value
            // has nothing to resolve against and the whole expression goes fatal (~239
            // occurrences across ~19 corpus files once formula extraction was fixed).
            // Braced references only — {Table.Column} is unambiguous, while the bare
            // shape can also match ordinary dotted text, good enough for backfilling an
            // existing field's table name but too loose to invent new fields from.
            // String is the only honest type guess; the real one isn't recoverable here.
            if (!canSynthesize || allColumns.Contains(column)) return;
            var synthesized = new DatabaseField
            {
                Name = column,
                ColumnName = column,
                TableName = table,
                DataType = "String"
            };
            report.Fields.Add(synthesized);
            dbFields.Add(synthesized);
            allColumns.Add(column);
            synthesizedFields.Add(synthesized);
        }

        // The String default above is fatal the moment a synthesized column or parameter
        // is used arithmetically — the engine type-checks '-' at parse time ("'-'
        // operator works only on numbers", e.g. {Journal.Line_Debit} -
        // {Journal.Line_Credit}). Upgrade a synthesized field/parameter to Float64 (a
        // Double TypeName / Float parameter type in the RDL) when it appears:
        //   - adjacent to unambiguous arithmetic (-, *, /), or
        //   - adjacent to '+' in a formula with no string literal and no '&' — Crystal
        //     overloads '+' for concatenation, but concatenation needs a string somewhere,
        //     and these corpora write pure-numeric sums as (({X.A}) + ({X.B})) - ({X.C}),
        //     where the '-' is adjacent only to the parens, never the refs themselves.
        // Scans FormulaTexts, not report.Fields: it holds *every* formula in the report,
        // including the section hooks (suppress, page-break, sort) that the field list
        // deliberately skips as internal. Those hooks are where page-count arithmetic
        // lives — "{?NumRecPerPage} - 1" in a table's suppress formula — so a parameter
        // used numerically only there was previously never inferred as numeric.
        bool UsedNumerically(string refPattern)
        {
            // Case-insensitive, like the boolean inference below: Crystal resolves a
            // "{?Name}" reference without regard to case, so a report can declare
            // "UpToYear" and subtract from "{?UptoYear}". Matching exactly left that
            // parameter String and the subtraction was rejected outright.
            var strict = new Regex($@"[-*/]\s*\(*\s*{refPattern}|{refPattern}\s*\)*\s*[-*/]", RegexOptions.IgnoreCase);
            var plus = new Regex($@"\+\s*\(*\s*{refPattern}|{refPattern}\s*\)*\s*\+", RegexOptions.IgnoreCase);
            foreach (var text in report.FormulaTexts.Values)
            {
                if (string.IsNullOrEmpty(text)) continue;
                if (strict.IsMatch(text)) return true;
                if (plus.IsMatch(text)
                    && !text.Contains('"') && !text.Contains('\'')
                    && !text.Contains('&'))
                    return true;
            }
            return false;
        }

        // Synthesized columns only. Extending this to *declared* String columns was
        // tried and measurably regressed both corpora: a column Crystal types String and
        // uses numerically in one formula is routinely used as a genuine string in
        // another, and a global retype breaks the string uses (the honest fix is a
        // CDbl() wrap at each arithmetic reference site, which needs field-type
        // knowledge inside the transpiler — future work). Parameters are different:
        // every observed String-typed-but-subtracted parameter (page numbers, years) is
        // numeric in all its uses, so declared parameters get the inference too.
        foreach (var f in synthesizedFields)
            if (UsedNumerically(Regex.Escape($"{{{f.TableName}.{f.ColumnName}}}")))
                f.DataType = "Float64";
        foreach (var p in report.Fields.OfType<ParameterField>()
                     .Where(p => p.DataType is "String" or ""))
            if (UsedNumerically(Regex.Escape($"{{?{p.Name}}}")))
                p.DataType = "Float64";

        // The same reasoning for boolean context: a parameter written as the operand of
        // Not/And/Or, or compared against True/False, is a flag in every observed use, and
        // left String the engine rejects the whole expression ("NOT requires boolean
        // expression"). Applied only to parameters, not columns — the declared-column
        // retype above regressed both corpora and there is no reason to expect boolean to
        // behave differently there.
        bool UsedAsBoolean(string refPattern)
        {
            var boolContext = new Regex(
                $@"\bNot\s*\(*\s*{refPattern}"                       // Not {?Flag}
                + $@"|{refPattern}\s*\)*\s*(?:And|Or)\b"             // {?Flag} And ...
                + $@"|\b(?:And|Or)\s*\(*\s*{refPattern}"             // ... And {?Flag}
                + $@"|{refPattern}\s*=\s*(?:True|False)\b",          // {?Flag} = True
                RegexOptions.IgnoreCase);
            return report.FormulaTexts.Values.Any(t => !string.IsNullOrEmpty(t) && boolContext.IsMatch(t));
        }

        foreach (var p in report.Fields.OfType<ParameterField>()
                     .Where(p => p.DataType is "String" or ""))
            if (UsedAsBoolean(Regex.Escape($"{{?{p.Name}}}")))
                p.DataType = "Boolean";
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

        // Objects carry no position in their own record; it arrives in the tag-190
        // that follows the wrapper. Applying it here rather than in each object parser
        // keeps the one rule in one place - it is the same record in the same position
        // for every object type.
        void AddPlaced(Model.Objects.ReportObject? obj, int wrapperIndex)
        {
            if (obj is null) return;
            obj.Bounds = ApplyPlacement(records, wrapperIndex, obj.Bounds);
            section.Objects.Add(obj);
        }

        // Read objectCount objects
        int parsed = 0;
        while (i < records.Count && records[i].Tag != endTag && parsed < objectCount + 100)
        {
            var rec = records[i];
            if (rec.Tag == TagFieldObjectStart)
            {
                var obj = ParseFieldObject(records, i, out int next, report);
                AddPlaced(obj, i);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag == TagTextObjectStart)
            {
                var obj = ParseTextObject(records, i, out int next);
                AddPlaced(obj, i);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag == TagPictureObjectStart)
            {
                var obj = ParsePictureObject(records, i, out int next);
                AddPlaced(obj, i);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag == TagSubreportObjectStart)
            {
                var obj = ParseSubreportObject(records, i, out int next);
                AddPlaced(obj, i);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag == TagCrossTabObjectStart)
            {
                var obj = ParseCrossTabObject(records, i, out int next);
                AddPlaced(obj, i);
                i = next;
                parsed++;
                continue;
            }
            if (rec.Tag == TagChartObjectStart)
            {
                var obj = ParseChartObject(records, i, out int next, report);
                AddPlaced(obj, i);
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
                    AddPlaced(shape, i);
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
                AddPlaced(obj, i);
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
        string objectName = ExtractObjectName(wrapper);
        string name = objectName;

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
        string? dateFormat = null;
        (int Decimals, string Thousands, string DecimalSep, string Currency)? numericFormat = null;
        (byte L, byte R, byte T, byte B, bool Shadow, string? BackColor, int WidthTwips)? borders = null;
        HorizontalAlignment hAlign = HorizontalAlignment.Left;
        while (nextIndex < records.Count && records[nextIndex].Tag != TagFieldObjectEnd)
        {
            if (records[nextIndex].Tag == TagFont)
                format = ExtractFontFormat(records[nextIndex]);
            else if (records[nextIndex].Tag == TagFontColourProps)
                foreColor = ExtractForeColor(records[nextIndex]);
            else if (records[nextIndex].Tag == TagObjectProps)
                hAlign = ExtractHAlignment(records[nextIndex]);
            else if (records[nextIndex].Tag == TagDateFormat)
                dateFormat ??= ExtractDateFormat(records[nextIndex]);
            else if (records[nextIndex].Tag == TagNumericFormat)
                // Deliberately last-wins, unlike the date format above: an object carries
                // two of these and the second is the one the engine honours.
                numericFormat = ExtractNumericFormat(records[nextIndex]) ?? numericFormat;
            else if (records[nextIndex].Tag == TagObjectBorder)
                borders ??= ExtractBorders(records[nextIndex]);
            nextIndex++;
        }
        if (nextIndex < records.Count) nextIndex++;

        // Every field object carries a date-format record, including the ones showing a
        // string or a number, where it holds whatever the object was last defaulted to.
        // So the format is only worth anything on a field the report itself calls a date.
        bool isDateField = report is not null && !string.IsNullOrEmpty(name)
            && report.Fields.OfType<DatabaseField>().Any(f =>
                string.Equals(f.ColumnName, name, StringComparison.OrdinalIgnoreCase)
                && f.DataType == "DateTime");
        if (!isDateField) dateFormat = null;

        // The numeric record is carried by every field object, string ones included, where
        // it holds whatever the object was last defaulted to - Customer Name's says two
        // decimals. Applying a numeric format to a string would corrupt it, so this asks the
        // report what the field is rather than trusting the record's presence. A summary
        // field is formatted as whatever it summarises, which is the same column name.
        bool isNumericField = report is not null && !string.IsNullOrEmpty(name)
            && report.Fields.OfType<DatabaseField>().Any(f =>
                string.Equals(f.ColumnName, name, StringComparison.OrdinalIgnoreCase)
                && f.DataType is "Int16" or "Int32" or "Float32" or "Float64" or "Currency");
        string? numberFormat = isNumericField && !isDateField && numericFormat is { } n
            ? BuildNumericFormat(n.Decimals, n.Thousands, n.DecimalSep, n.Currency)
            : null;
        dateFormat ??= numberFormat;

        if (foreColor != null || hAlign != HorizontalAlignment.Left || dateFormat != null || borders is not null)
            format = new ObjectFormat
            {
                FontName = format.FontName, FontSize = format.FontSize, Bold = format.Bold,
                Italic = format.Italic, Underline = format.Underline, ForeColor = foreColor,
                HAlign = hAlign, FormatString = dateFormat,
                BorderLeft = borders?.L ?? 0, BorderRight = borders?.R ?? 0,
                BorderTop = borders?.T ?? 0, BorderBottom = borders?.B ?? 0,
                DropShadow = borders?.Shadow ?? false, BackColor = borders?.BackColor,
                BorderWidthTwips = borders?.WidthTwips ?? 0,
            };

        return new Model.Objects.FieldObject
        {
            Name = objectName,
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
        // Bounds/name are nested one level deeper than most object types: tag-175
        // wrapper -> tag-174 drawing header -> tag-158 (same 174->158 step ChartObject
        // uses one level further down from its own tag-179 layer). Reading tag-158
        // directly off the tag-175 wrapper misses it entirely, yielding an all-zero
        // (invisible) image.
        var wrapper = records[start];
        var inner174 = wrapper.ParseChildren().FirstOrDefault(c => c.Tag == 174);
        var bounds = inner174 is not null ? ExtractObjectBounds(inner174) : new ObjectBounds(0, 0, 0, 0);
        string name = inner174 is not null ? ExtractObjectName(inner174) : string.Empty;

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
    private static Model.Objects.ReportObject? ParseChartObject(List<TslvRecord> records, int start,
        out int nextIndex, ReportBuilder? report = null)
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

        // A chart's category has to be a field this report actually has. ScanStrings
        // brute-forces every MUTF-8 string out of the definition record, and that record
        // ends with the chart's font block - eight or more names like "Arial" or
        // "MS Shell Dlg". A chart that carries no category string of its own (a Gantt, or
        // one grouped by an on-change-of field) leaves those fonts as the only strings
        // after the title, so strings[1] was a typeface: the converter emitted
        // Fields!MS_Shell_Dlg.Value and a ChartCategory_MS_Shell_Dlg grouping, which the
        // engine rejects outright - "Field 'MS_Shell_Dlg' not found", and five reports in
        // one corpus family then collided on the duplicate grouping name. Asking the
        // report what its fields are separates the two cases without having to know which
        // typefaces exist.
        if (fieldBoundCategory.Length > 0 && !IsKnownFieldName(fieldBoundCategory, report))
            fieldBoundCategory = string.Empty;

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

    /// <summary>
    /// Whether a name is one of the report's own fields, by the spellings Crystal uses for a
    /// chart's group-by: the bare column, the "TableName ColumnName" display form a
    /// table-qualified field is stored under, or a formula field's name. Used to tell a real
    /// category apart from a string that merely happens to sit in the same record.
    /// </summary>
    private static bool IsKnownFieldName(string name, ReportBuilder? report)
    {
        if (report is null) return true;   // nothing to check against; keep prior behaviour

        // The parser stores field names as the file spells them, so normalise only the two
        // decorations a reference can carry: a formula's "@" and a "Table." qualifier.
        string n = name.TrimStart('@');
        int dot = n.IndexOf('.');
        if (dot > 0) n = n[(dot + 1)..];

        foreach (var f in report.Fields.OfType<DatabaseField>())
        {
            if (f.ColumnName.Length == 0) continue;
            if (string.Equals(f.ColumnName, n, StringComparison.OrdinalIgnoreCase)) return true;
            if (f.TableName.Length > 0 &&
                string.Equals($"{f.TableName} {f.ColumnName}", n, StringComparison.OrdinalIgnoreCase))
                return true;
            // Crystal stores a chart's group-by under its display name, which for a
            // table-qualified field is "TableName ColumnName" with a space -
            // Top3-Employee-Sales stores "Employee Last Name" for the column "Last Name".
            // Matching on the qualified pair alone is not enough, because TableName is not
            // always populated by the time a chart is parsed, so the suffix is checked too.
            // A typeface does not end in one of the report's own column names.
            if (n.Length > f.ColumnName.Length
                && n.EndsWith(f.ColumnName, StringComparison.OrdinalIgnoreCase)
                && n[n.Length - f.ColumnName.Length - 1] == ' ')
                return true;
        }
        return report.Fields.OfType<FormulaField>()
            .Any(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase));
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
        (byte L, byte R, byte T, byte B, bool Shadow, string? BackColor, int WidthTwips)? borders = null;
        HorizontalAlignment hAlign = HorizontalAlignment.Left;
        HorizontalAlignment? paragraphAlign = null;
        while (nextIndex < records.Count && records[nextIndex].Tag != TagTextObjectEnd)
        {
            if (records[nextIndex].Tag == TagFont)
                format = ExtractFontFormat(records[nextIndex]);
            else if (records[nextIndex].Tag == TagFontColourProps)
                foreColor = ExtractForeColor(records[nextIndex]);
            else if (records[nextIndex].Tag == TagObjectBorder)
                borders ??= ExtractBorders(records[nextIndex]);
            else if (records[nextIndex].Tag == TagObjectProps)
                hAlign = ExtractHAlignment(records[nextIndex]);
            else if (records[nextIndex].Tag == TagTextParagraph)
                paragraphAlign ??= ExtractParagraphAlignment(records[nextIndex]);
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
        // The paragraph wins outright, not merely when the object-level record is unset.
        // Where the two are both set and disagree, the real engine renders the
        // paragraph's alignment.
        hAlign = paragraphAlign ?? hAlign;
        if (foreColor != null || hAlign != HorizontalAlignment.Left || borders is not null)
            format = new ObjectFormat
            {
                FontName = format.FontName, FontSize = format.FontSize, Bold = format.Bold,
                Italic = format.Italic, Underline = format.Underline, ForeColor = foreColor,
                HAlign = hAlign,
                BorderLeft = borders?.L ?? 0, BorderRight = borders?.R ?? 0,
                BorderTop = borders?.T ?? 0, BorderBottom = borders?.B ?? 0,
                DropShadow = borders?.Shadow ?? false, BackColor = borders?.BackColor,
                BorderWidthTwips = borders?.WidthTwips ?? 0,
            };

        string name = ExtractObjectName(wrapper);
        return new Model.Objects.TextObject { Name = name, Text = text.Length > 0 ? text.ToString() : name, Bounds = bounds, Format = format };
    }

    // tag-8 (Font) layout after MUTF-8 name (consumed=nc):
    //   nc+0..nc+3  = charset/pitch flags (e.g. 0x10, 0x00, 0x01, 0x00)
    //   nc+4        = font size in points (1 byte, e.g. 10 = 10pt)
    //   nc+5..nc+8  = style flags, int32 BE: 0x00000001 = underline, 0x00010000 = italic,
    //                 zero = normal. Those are the only bits either corpus uses: across
    //                 2,324 private and 88 public files the field only ever holds 0, 1,
    //                 0x10000, or 0x10001 - the last being both at once, which is what
    //                 makes them independent flags rather than one enumeration.
    //
    //                 This was previously read as 0x02 = italic and 0x04 = underline.
    //                 Neither bit is ever set in either corpus, so both attributes were
    //                 dead: no report emitted an italic or an underline. The values here
    //                 are the ones the files actually carry - the column headings of
    //                 CustomerList (6), Country-Region-Sort (4) and boyum__SampleReport (2)
    //                 are underlined in the real engine's output and hold exactly that many
    //                 records with 0x1, and SalesByCustomer-Grouped's one italic title is
    //                 its one record with 0x10000.
    //   nc+9..nc+12 = font weight as int32 BE (700=bold, 400=normal)
    private static Model.Objects.ObjectFormat ExtractFontFormat(TslvRecord fontRec)
    {
        if (fontRec.Data.Length < 14) return new();
        string? fontName = fontRec.ReadMutf8String(0, out int nc);
        if (nc <= 0 || fontRec.Data.Length < nc + 13) return new();

        int fontSize = fontRec.Data[nc + 4];
        int eFlags   = fontRec.Data.Length >= nc + 9 ? fontRec.ReadInt32BE(nc + 5) : 0;
        int weight   = fontRec.ReadInt32BE(nc + 9);
        bool underline = (eFlags & 0x00000001) != 0;
        bool italic    = (eFlags & 0x00010000) != 0;
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

    // tag-257 (FontColourProperties) wraps a tag-256 child with 4 bytes: flag, B, G, R.
    // Byte 0 = 0 means a colour is set. Returns "#RRGGBB" or null if default/black.
    //
    // The channel order is BLUE first. This was read as R,G,B, and on the greys and blacks
    // the fixture-backed reports use the two orders agree, so nothing caught it - but the
    // real engine renders {Top5USA}'s fore colour 00A5795A as (0.353, 0.475, 0.647) and
    // 00800000 as (0, 0, 0.502): steel blue and navy, the B,G,R reading, where R,G,B gives
    // brown and maroon. Same order as the border record's background colour, whose tan
    // 007DA5BF fill the real engine paints as (0.749, 0.647, 0.49).
    private static string? ExtractForeColor(TslvRecord fontColourProps)
    {
        var ch = fontColourProps.ParseChildren().FirstOrDefault(c => c.Tag == TagFontColour && c.Data.Length >= 4);
        if (ch is null) return null;
        byte b = ch.Data[1], g = ch.Data[2], r = ch.Data[3];
        return (r == 0 && g == 0 && b == 0) ? null : $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>
    /// tag-237 wraps a tag-236 child holding the object's border and background:
    ///   data[0..3]   = edge style codes, left, right, top, bottom (0 none, 1 single,
    ///                  2 double, 3 dashed, 4 dotted)
    ///   data[9]      = drop shadow flag
    ///   data[14..17] = background colour: flag byte (0 = set, 0xFF = none), then B, G, R
    ///   data[18..21] = border line width, big-endian twips (20 = 1pt, the default)
    ///
    /// Bottom is proven by the two column labels whose only decoration is the rule under
    /// them; top by a balance sheet whose sixteen edges=0010 objects are the totals with a
    /// line above; the background byte order by the real engine painting 007DA5BF as
    /// (0.749, 0.647, 0.49) - tan, the B,G,R reading. Left/right cannot be told apart by
    /// any corpus file (every box sets both), so that half of the order is convention.
    /// Style codes 2-4 follow Crystal's line-style list; only 1 is corpus-verified.
    /// </summary>
    private static (byte L, byte R, byte T, byte B, bool Shadow, string? BackColor, int WidthTwips)?
        ExtractBorders(TslvRecord borderProps)
    {
        var ch = borderProps.ParseChildren().FirstOrDefault(c => c.Tag == TagObjectBorder - 1 && c.Data.Length >= 22);
        if (ch is null) return null;
        var d = ch.Data;
        string? back = d[14] == 0 ? $"#{d[17]:X2}{d[16]:X2}{d[15]:X2}" : null;
        int width = (d[18] << 24) | (d[19] << 16) | (d[20] << 8) | d[21];
        if (d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] == 0 && d[9] == 0 && back is null)
            return null;
        return (d[0], d[1], d[2], d[3], d[9] != 0, back, width);
    }

    // tag-253 (ReportObjectProperties) → tag-252 child:
    //   data[0..1] = f() lockToSection (Int16 BE bool)
    //   data[2]    = case() alignment code (0=unset, 1=left, 2=center, 3=right, 4=justify)
    private static HorizontalAlignment ExtractHAlignment(TslvRecord objProps)
    {
        var ch = objProps.ParseChildren().FirstOrDefault(c => c.Tag == TagObjectPropsInner && c.Data.Length >= 3);
        if (ch is null) return HorizontalAlignment.Left;
        return AlignmentFromCode(ch.Data[2]);
    }

    // Both the object-level record and the paragraph record spell alignment the same
    // way. 0 means unset, and only the object-level record ever uses it.
    private static HorizontalAlignment AlignmentFromCode(byte code) => code switch
    {
        2 => HorizontalAlignment.Center,
        3 => HorizontalAlignment.Right,
        4 => HorizontalAlignment.Justify,
        _ => HorizontalAlignment.Left,
    };

    // A text object's alignment, read from the first tag-192 paragraph record inside it.
    // Nearly every text object holds one paragraph; where there are several they almost
    // always share an alignment, and the model has one alignment per object, so the rest
    // are ignored rather than reconciled.
    private static HorizontalAlignment? ExtractParagraphAlignment(TslvRecord paragraph) =>
        paragraph.Data.Length >= 13 ? AlignmentFromCode(paragraph.Data[12]) : null;

    // A date field's format, as a .NET format string, or null to leave it to the renderer.
    //
    // Only an explicit order is honoured. Order 1 means "use the machine's short date",
    // which is what the renderer already does when given no format at all, so the faithful
    // thing is to emit nothing rather than to bake this machine's locale into the report.
    /// <summary>
    /// tag-249 → tag-248 (NumericFormat) gives a field's decimal places and its separator
    /// and currency strings:
    ///   data[8]  = decimal places (0-5 across both corpora)
    ///   data[9]  = 11 minus the decimal places in all 162,082 private-corpus records, so
    ///              it is derived rather than independent and is not read
    ///   from data[17] = a run of [1-byte length][that many bytes, null-terminated] slots.
    ///              Zero-length slots are padding; the non-empty ones are, in order, the
    ///              thousands separator, the decimal separator, the currency symbol, and
    ///              then the format's own name ("&lt;Default Format&gt;").
    ///
    /// An object carries two of these records and the SECOND is the effective one, which is
    /// what settles the currency symbol: Country-Region-Sort's Customer ID has "$" in its
    /// first record and an empty symbol in its second, and the real engine renders it as a
    /// bare "158".
    /// </summary>
    private static (int Decimals, string Thousands, string DecimalSep, string Currency)?
        ExtractNumericFormat(TslvRecord numericFormat)
    {
        var ch = numericFormat.ParseChildren()
            .FirstOrDefault(c => c.Tag == TagNumericFormatInner && c.Data.Length >= 20);
        if (ch is null) return null;
        var d = ch.Data;

        var slots = new List<string>();
        int pos = 17;
        while (pos < d.Length && slots.Count < 4)
        {
            int len = d[pos];
            if (len == 0) { pos++; continue; }
            if (pos + 1 + len > d.Length) break;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < len; i++)
            {
                byte b = d[pos + 1 + i];
                if (b != 0) sb.Append((char)b);
            }
            slots.Add(sb.ToString());
            pos += 1 + len;
        }

        string thousands = slots.Count > 0 ? slots[0] : string.Empty;
        string decimalSep = slots.Count > 1 ? slots[1] : string.Empty;
        // The third slot is only a currency symbol when there is one; with no symbol the
        // format name slides into its place.
        string currency = slots.Count > 2 && !slots[2].StartsWith('<') ? slots[2] : string.Empty;

        return (d[8], thousands, decimalSep, currency);
    }

    /// <summary>
    /// A .NET numeric format string for what the record describes, or null when it cannot be
    /// expressed.
    ///
    /// .NET substitutes the *rendering culture's* separators for "," and "." in a format
    /// string, so a report whose separators are the European pair ("." for thousands, ","
    /// for decimals — 3,190 of the 4,762 public-corpus records, all of them Danish) cannot be
    /// honoured this way and is left unformatted rather than rendered with the wrong
    /// separators. Reports using the en-US pair, which is 161,000 of the 162,000 private
    /// records, are formatted.
    ///
    /// A "%" symbol is skipped: it is a percentage format rather than a currency, it belongs
    /// after the number rather than before, and Crystal treats it as a separate feature. A
    /// symbol stored with a leading space (" kr.") is a suffix; otherwise it is a prefix,
    /// which is how the file itself carries the spacing.
    /// </summary>
    private static string? BuildNumericFormat(int decimals, string thousands, string decimalSep,
        string currency)
    {
        if (decimalSep.Length > 0 && decimalSep != ".") return null;
        if (thousands.Length > 0 && thousands != ",") return null;
        if (decimals is < 0 or > 9) return null;
        if (currency == "%") return null;

        string number = (thousands == "," ? "#,##0" : "0")
                      + (decimals > 0 ? "." + new string('0', decimals) : string.Empty);
        if (currency.Length == 0) return number;

        // Quoted so a letter symbol ("kr", "Rs") is a literal rather than a format specifier.
        string symbol = "\"" + currency + "\"";
        return currency.StartsWith(' ') ? number + symbol : symbol + number;
    }

    private static string? ExtractDateFormat(TslvRecord dateFormat)
    {
        var ch = dateFormat.ParseChildren()
            .FirstOrDefault(c => c.Tag == TagDateFormatInner && c.Data.Length >= 18);
        if (ch is null) return null;
        char sep = (char)ch.Data[17];
        if (sep is < ' ' or > '~' or '\'') return null;
        // Quoted, because .NET reads a bare "/" in a format string as "whatever this
        // machine's date separator is" rather than as a slash. Crystal means the
        // character it stored: it renders 05/26/2001 on a machine whose own separator is
        // a dash, and an unquoted MM/dd/yyyy renders 05-26-2001 there.
        string q = $"'{sep}'";
        return ch.Data[0] switch
        {
            0 => $"yyyy{q}MM{q}dd",
            2 => $"MM{q}dd{q}yyyy",
            _ => null,
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

    // Apply the tag-190 placement record that follows an object wrapper.
    //
    // The wrapper's own nested tag-158 carries size but no position - its two spare
    // int32 slots are zero in every object of every corpus file. Position lives here
    // instead, in the record immediately after the wrapper: UInt16 left, UInt16 top,
    // twips, relative to the section. UInt16 rather than int32 is what the record's
    // four-byte length forces, and it is not a limit worth worrying about: it tops out
    // at just over 45 inches, and the largest offset seen anywhere is 20.
    //
    // Every object wrapper examined is followed by one, so a missing record means the
    // caller is looking at the wrong index rather than at an object without a position;
    // the bounds are returned unchanged in that case, which puts the object at the
    // section origin instead of somewhere arbitrary.
    private static ObjectBounds ApplyPlacement(List<TslvRecord> records, int wrapperIndex, ObjectBounds bounds)
    {
        if (wrapperIndex + 1 >= records.Count) return bounds;
        var placement = records[wrapperIndex + 1];
        if (placement.Tag != TagObjectPlacement || placement.Data.Length < 4) return bounds;
        int left = (placement.Data[0] << 8) | placement.Data[1];
        int top  = (placement.Data[2] << 8) | placement.Data[3];
        return bounds with { Left = left, Top = top };
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
        //   [8-11] int32 = always zero
        //   [12-15] int32 = always zero
        //   [16+]  MUTF-8 string = object name, then a colour/flag trailer
        //
        // The record holds size only. The two zero slots were once read as left/top, but
        // they are zero in every object of every corpus file, and dumping the whole
        // payload shows everything after the name is identical across every object in a
        // report - a colour and flag trailer. Position comes from the tag-190 that
        // follows the wrapper; see ApplyPlacement.
        int width  = objHeader.ReadInt32BE(0);
        int height = objHeader.ReadInt32BE(4);
        if (width < 0) width = 0;
        if (height < 0) height = 0;
        return new ObjectBounds(0, 0, width, height);
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
        return new ObjectBounds(0, 0, width, height);
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
        // 240 twips is a sixth of an inch, which is the inset the real engine renders
        // these reports into - its PDF clips the page to twelve points on every side. The
        // file itself says nothing: the page-setup record's margin block is byte-for-byte
        // identical in every file of both corpora, a "use the printer's defaults"
        // sentinel rather than per-report margins, so one default has to serve.
        //
        // Half an inch was assumed before, and it is not a harmless difference. Object
        // positions are relative to the page body, so too generous a margin narrows the
        // body until content that Crystal fits on the page no longer fits on ours. The
        // reference report's own columns need 10.45 inches, which half-inch margins do
        // not leave room for on a landscape page.
        public int TopMarginTwips { get; set; } = 240;
        public int BottomMarginTwips { get; set; } = 240;
        public int LeftMarginTwips { get; set; } = 240;
        public int RightMarginTwips { get; set; } = 240;
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
