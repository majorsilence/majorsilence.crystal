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
                var ch = rec.ParseChildren().FirstOrDefault();
                if (ch == null) continue;
                var ch113 = ch.ParseChildren().FirstOrDefault(c => c.Tag == 113);
                if (ch113 == null) continue;
                string? name = ch113.ReadMutf8String(0, out int nc);
                if (!string.IsNullOrEmpty(name))
                {
                    int typeCode = ch113.ReadInt16LE(nc);
                    report.Fields.Add(new ParameterField { Name = name, DataType = MapCrValueType(typeCode) });
                }
            }
            else if (rec.Tag == TagRunningTotalFieldDef)
            {
                // tag-128 → tag-126 (SummaryFieldDefinitionBase) → tag-113 (FieldDefinition: name + type)
                var ch126 = rec.ParseChildren().FirstOrDefault(c => c.Tag == 126);
                if (ch126 == null) continue;
                var ch113 = ch126.ParseChildren().FirstOrDefault(c => c.Tag == 113);
                if (ch113 == null) continue;
                string? name = ch113.ReadMutf8String(0, out int consumed);
                if (string.IsNullOrEmpty(name)) continue;
                int typeCode = ch113.ReadInt16LE(consumed);
                report.Fields.Add(new RunningTotalField
                {
                    Name = name,
                    DataType = MapCrValueType(typeCode)
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

            // tag-229 layout: MUTF-8 "Table.FieldName" at offset 0,
            // then Int16 condition code (nc+0..nc+1), Int16 sort direction (nc+2..nc+3)
            string? tableField = rec.ReadMutf8String(0, out int nc);
            if (string.IsNullOrEmpty(tableField)) continue;

            int dot = tableField.IndexOf('.');
            string fieldName = dot >= 0 ? tableField[(dot + 1)..] : tableField;
            if (string.IsNullOrEmpty(fieldName)) continue;

            int sortCode = (nc + 3 < rec.Data.Length) ? rec.ReadInt16BE(nc + 2) : 0;
            GroupSortOrder sort = sortCode switch
            {
                1 => GroupSortOrder.Descending,
                2 => GroupSortOrder.OriginalOrder,
                3 => GroupSortOrder.Specified,
                _ => GroupSortOrder.Ascending
            };

            report.Groups.Add(new GroupDefinition
            {
                Level     = level,
                FieldName = fieldName,
                SortOrder = sort,
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

        // Skip SectionProperties (tag 255) at area level
        if (i < records.Count && records[i].Tag == TagSectionProperties)
            i++;

        // Parse sections until tag 139 (area end)
        while (i < records.Count && records[i].Tag != TagAreaEnd)
        {
            var rec = records[i];
            if (TslvRecord.IsSectionStart(rec.Tag))
            {
                // The section wrapper tag encodes the type directly (141=RH, 143=RF, 145=PH, etc.)
                i = ParseSection(records, i, report, warnings, TslvRecord.SectionKindFromTag(rec.Tag), groupLevel);
                continue;
            }
            i++;
        }

        return i + 1;  // skip tag 139
    }

    private static int ParseSection(List<TslvRecord> records, int start, ReportBuilder report, List<string> warnings,
        SectionKind kind, int groupLevel)
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
            Name = sectionName
        };

        int i = start + 1;  // advance past section wrapper

        // Skip SectionCode (157); read SectionProperties (255) for flags
        while (i < records.Count && records[i].Tag is TagSectionCode or TagSectionProperties)
        {
            if (records[i].Tag == TagSectionProperties)
            {
                var (sup, npb, npa) = ExtractSectionFlags(records[i]);
                section.Suppress       = sup;
                section.NewPageBefore  = npb;
                section.NewPageAfter   = npa;
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
        var (tableName, fieldRef) = ExtractFieldRefFull(wrapper);
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

        return new Model.Objects.FieldObject { FieldName = name, Bounds = bounds, Format = format };
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
    private static (bool suppress, bool newPageBefore, bool newPageAfter) ExtractSectionFlags(TslvRecord sectionProps)
    {
        var ch = sectionProps.ParseChildren().FirstOrDefault(c => c.Tag == 254);
        if (ch is null || ch.Data.Length < 13) return (false, false, false);

        var d = ch.Data;
        bool isSection   = (d[3] != 0 || d[4] != 0);   // mQ non-zero → section-level
        if (!isSection) return (false, false, false);    // area-level record has no per-section flags

        bool suppress      = (d[5] == 0 && d[6] == 0);   // !suppress == 0 → suppressed
        bool newPageBefore = (d[9] != 0 || d[10] != 0);
        bool newPageAfter  = (d[11] != 0 || d[12] != 0);
        return (suppress, newPageBefore, newPageAfter);
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

    private static string ExtractObjectName(TslvRecord wrapper)
    {
        var children = wrapper.ParseChildren();
        var objHeader = children.FirstOrDefault(r => r.Tag == TagReportObjectHeader);
        if (objHeader == null || objHeader.Data.Length < 16) return string.Empty;
        // Name starts at offset 16 (after the 4 int32s: width, height, left, top)
        return objHeader.ReadMutf8String(16, out _) ?? string.Empty;
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
            Sections = Sections.Select(s => s.ToModel()).ToList(),
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
        public string Name { get; set; } = string.Empty;
        public List<Model.Objects.ReportObject> Objects { get; } = [];

        public Section ToModel() => new()
        {
            Type = KindToSectionType(Kind),
            GroupLevel = GroupLevel,
            HeightTwips = HeightTwips,
            Suppress = Suppress,
            NewPageBefore = NewPageBefore,
            NewPageAfter = NewPageAfter,
            Objects = Objects
        };

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
