using System.Text;
using System.Xml;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;

namespace Majorsilence.Crystal.Converter;

/// <summary>
/// Converts a <see cref="ReportDefinition"/> into an RDL XML string compatible
/// with Majorsilence Reporting (SSRS 2005 schema).
/// </summary>
/// <remarks>
/// Not safe for concurrent use on the same instance — create one per thread or per conversion.
/// </remarks>
public sealed class RdlConverter
{
    private const string RdlNs = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    // Monotonically increasing counter reset per Convert call — gives deterministic Textbox names
    private int _textboxCounter;

    // Every Matrix grouping name written so far in this report; see UniqueNames.
    private readonly HashSet<string> _matrixGroupingNames = new(StringComparer.OrdinalIgnoreCase);

    // Lines and boxes Crystal draws from one report header down into a later one, drawn as a
    // piece in each header they cross (see ShapePiecesAcrossReportHeaders) rather than whole.
    private Dictionary<Section, List<(ShapeObject Shape, ObjectBounds Bounds, bool OpenTop, bool OpenBottom)>> _shapePieces = [];
    private readonly HashSet<ShapeObject> _shapesInPieces = [];

    // A group section set to underlay the sections after it, written into their rows rather
    // than as a row of its own (see PlaceGroupUnderlays): the pieces each row draws first,
    // with how far each is moved up, and the sections that now take no row.
    private readonly Dictionary<Section, List<(Section Piece, int TopShiftTwips)>> _underlayInto = [];
    private readonly HashSet<Section> _underlaid = [];

    // Prefix prepended to subreport ReportName references so they match the
    // companion .rdl filenames a batch caller writes (e.g. "ParentStem_").
    private string _subreportNamePrefix = string.Empty;

    /// <summary>
    /// The BCP 47 tag carrying this report's number separators, for the styles that need it.
    /// See <see cref="NeedsSeparatorCulture"/>; null when the file records no separators.
    /// </summary>
    private string? _separatorCulture;

    /// <summary>
    /// Each formula's calculated-field expression as WriteDataSets finally writes it, keyed by
    /// its field name, for <see cref="InlineFormulasOutsideDataRegions"/>.
    /// </summary>
    private readonly Dictionary<string, string> _formulaExpressions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A footer formula's value written as the aggregate its variables amount to, keyed by the
    /// formula's field name; see <see cref="PrintTimeVariables"/>. Used where the formula is
    /// placed, in place of its calculated field.
    /// </summary>
    private readonly Dictionary<string, string> _footerFormulaExpressions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The background colour of the section whose table row is being written, or null.
    /// Set by <see cref="StartTableRow"/> at the start of every row, so it never outlives
    /// the row it belongs to; read by <see cref="WriteTableCell"/> and the row Rectangles.
    /// </summary>
    private string? _bandBackColor;

    // Starts a table row for a section: its height, and the section's background colour
    // for every cell written until the next row starts. Every row in the converter goes
    // through here, so a row of a section with no colour clears the previous one's.
    private void StartTableRow(XmlWriter w, string height, Section? section)
    {
        _bandBackColor = section is null ? null : RowBackColor(section);
        w.WriteStartElement("TableRow", RdlNs);
        w.WriteElementString("Height", RdlNs, height);
    }

    /// <summary>
    /// A table row's background: the section's colour formula when it has one the
    /// transpiler can read, otherwise its fixed colour. The formula was never applied, so a
    /// band coloured "IF RecordNumber MOD 2 &lt;&gt; 0 THEN crSilver ELSE crNoColor" -
    /// boyum__ServiceContract's item rows, and many a document template's - painted every
    /// row in the designer's silver. Crystal's colour constants are already colour names to
    /// the transpiler; Color(r, g, b) with literal arguments becomes "#RRGGBB" first, and
    /// DefaultAttribute the section's own colour. Only rows: a page band has no record for
    /// a formula like this to read.
    /// </summary>
    private static string? RowBackColor(Section section)
    {
        if (string.IsNullOrWhiteSpace(section.BackColorFormula)) return section.BackColor;
        string text = ColorCall.Replace(section.BackColorFormula, m =>
            int.TryParse(m.Groups[1].Value, out int r) && int.TryParse(m.Groups[2].Value, out int g)
            && int.TryParse(m.Groups[3].Value, out int b) && r is >= 0 and <= 255 && g is >= 0 and <= 255 && b is >= 0 and <= 255
                ? $"\"#{r:X2}{g:X2}{b:X2}\""
                : m.Value);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\bDefaultAttribute\b",
            $"\"{section.BackColor ?? "Transparent"}\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string? expr = TranspileBackColorFormula(text);
        // A colour formula that degraded to an empty string, or still names something the
        // transpiler did not read, keeps the fixed colour rather than painting nothing.
        return expr is null || expr.Contains("Color(", StringComparison.OrdinalIgnoreCase) ? section.BackColor : expr;
    }

    private static readonly System.Text.RegularExpressions.Regex ColorCall =
        new(@"\b(?:Color|RGB)\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // A section's colour on a container that fills its band: a Rectangle, a page band.
    private static void WriteBackgroundStyle(XmlWriter w, string? color)
    {
        if (color is null) return;
        w.WriteStartElement("Style", RdlNs);
        w.WriteElementString("BackgroundColor", RdlNs, color);
        w.WriteEndElement();
    }

    /// <summary>
    /// Write every data source with an empty connection string. A template names its own
    /// server, DSN and database, and by default those are copied into the RDL so an authored
    /// report can run its query; a host that renders uploaded templates with pushed data must
    /// never open a connection a template asks for. The query text stays: with no connection
    /// it is inert, and it documents what the report expects.
    /// </summary>
    public bool OmitConnections { get; init; }

    /// <summary>
    /// What the RDL's report Description says; the report title when null. The engine exposes
    /// it while a subreport loads, so a renderer that must tell its subreports apart there
    /// writes a key of its own here. Nothing prints it.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The report a subreport being converted belongs to, when it is one. A Shared variable
    /// is one variable across a report and its subreports, so whether a formula is the only
    /// one to assign it can only be told from the whole tree (see VariableWriters); without
    /// this, the report being converted is taken as the root.
    /// </summary>
    public ReportDefinition? Root { get; init; }

    /// <summary>
    /// Write a second dataset, <see cref="RowSentinelDataSetName"/>, that no data region reads.
    /// The engine draws a subreport's body only when one of its datasets returned a row, so
    /// a subreport given no rows printed nothing at all, where Crystal prints its headers and
    /// footers and its details zero times. A renderer that gives this dataset one row makes
    /// the body print whatever the subreport's own data holds. For subreport companions.
    /// </summary>
    public bool RowSentinel { get; init; }

    /// <summary>The name of the dataset <see cref="RowSentinel"/> writes.</summary>
    public const string RowSentinelDataSetName = "CrystalSubreportRow";

    /// <summary>
    /// The name a parameter is declared under in the RDL, and so the key a runtime value
    /// for it must be supplied by: the SAP "$[Id]" wrapper stripped, then sanitized.
    /// </summary>
    public static string ParameterRdlName(ParameterField p) =>
        SanitizeName(FormulaTranspiler.StripSapParamWrapper(p.Name));

    /// <summary>
    /// Builds the RDL ReportName / companion-filename stem for a placed subreport.
    /// </summary>
    public static string SubreportRdlName(string prefix, string subreportName) =>
        $"{prefix}{SanitizeName(subreportName)}";

    public string Convert(ReportDefinition report, string subreportNamePrefix = "")
    {
        _subreportNamePrefix = subreportNamePrefix;
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        _textboxCounter = 0;
        _matrixGroupingNames.Clear();
        _formulaExpressions.Clear();
        _footerFormulaExpressions.Clear();
        _shadowedColumns = ShadowedColumns(report);
        var outerColumnNames = FormulaTranspiler.ColumnFieldNames;
        FormulaTranspiler.ColumnFieldNames = _shadowedColumns.Count > 0 ? _shadowedColumns : null;
        try
        {
            using var writer = XmlWriter.Create(sb, settings);
            WriteReport(writer, report);
            writer.Flush();
        }
        finally
        {
            FormulaTranspiler.ColumnFieldNames = outerColumnNames;
        }
        string rdl = AddNoRowsPass(sb.ToString());
        return report.Fields.OfType<DatabaseField>().Any() ? InlineFormulasInPageBands(rdl) : InlineFormulasOutsideDataRegions(rdl);
    }

    /// <summary>
    /// A report that reads no table is written with no data region (see WriteBody), and a
    /// Fields! reference resolves only inside one, so a formula placed in its body or page
    /// bands fails to compile ("Field not found"). Such a formula can only depend on
    /// parameters, constants and other formulas, so its own expression stands in for the
    /// reference, repeated until no formula reference is left (bounded, against a cycle).
    /// The DataSets block is left alone: the calculated fields there are still correct.
    /// </summary>
    private string InlineFormulasOutsideDataRegions(string rdl)
    {
        if (_formulaExpressions.Count == 0) return rdl;
        int dataSetsEnd = rdl.IndexOf("</DataSets>", StringComparison.Ordinal);
        if (dataSetsEnd < 0) return rdl;
        dataSetsEnd += "</DataSets>".Length;
        string head = rdl[..dataSetsEnd];
        string rest = rdl[dataSetsEnd..];

        var reference = new System.Text.RegularExpressions.Regex(@"Fields!([A-Za-z_][A-Za-z0-9_]*)\.Value");
        for (int pass = 0; pass < 8; pass++)
        {
            bool changed = false;
            rest = reference.Replace(rest, m =>
            {
                if (!_formulaExpressions.TryGetValue(m.Groups[1].Value, out string? expr)) return m.Value;
                changed = true;
                string inner = expr.StartsWith('=') ? expr[1..] : expr;
                // Value elements are XML text: the expression goes in escaped, as the writer would.
                return "(" + System.Security.SecurityElement.Escape(inner) + ")";
            });
            if (!changed) break;
        }
        return head + rest;
    }

    /// <summary>
    /// Whether a formula's expression reads no data: it refers to no field but formulas that
    /// read none either, and counts no rows. Parameters, the page numbers and constants are
    /// all it can use, and it means the same anywhere, a page band included.
    /// </summary>
    private bool IsDataFreeFormula(string name, HashSet<string> visiting)
    {
        if (!_formulaExpressions.TryGetValue(name, out string? expr) || !visiting.Add(name))
            return false;
        if (expr.Contains("RowNumber(", StringComparison.Ordinal) || expr.Contains("RunningValue(", StringComparison.Ordinal)
            || expr.Contains("CountRows(", StringComparison.Ordinal))
        {
            visiting.Remove(name);
            return false;
        }
        // visiting holds the formulas being looked through, against a cycle; one read twice
        // (PageXofY reads X_Language in every case) is looked through twice.
        bool dataFree = FieldReference.Matches(expr).All(m => IsDataFreeFormula(m.Groups[1].Value, visiting));
        visiting.Remove(name);
        return dataFree;
    }

    /// <summary>
    /// A page header or footer is outside every data region, where a Fields! reference does
    /// not resolve. The formulas left there are the ones that read no data (see
    /// IsScopeFreeSpecialField), so each one's own expression stands in for its reference,
    /// as in a report that reads no table (InlineFormulasOutsideDataRegions).
    /// </summary>
    private string InlineFormulasInPageBands(string rdl)
    {
        foreach (string band in new[] { "PageHeader", "PageFooter" })
        {
            int start = rdl.IndexOf($"<{band}>", StringComparison.Ordinal);
            int end = start < 0 ? -1 : rdl.IndexOf($"</{band}>", start, StringComparison.Ordinal);
            if (end < 0) continue;
            string part = rdl[start..end];
            for (int pass = 0; pass < 8; pass++)
            {
                bool changed = false;
                part = FieldReference.Replace(part, m =>
                {
                    if (!IsDataFreeFormula(m.Groups[1].Value, []) || !_formulaExpressions.TryGetValue(m.Groups[1].Value, out string? expr))
                        return m.Value;
                    changed = true;
                    string inner = expr.StartsWith('=') ? expr[1..] : expr;
                    return "(" + System.Security.SecurityElement.Escape(inner) + ")";
                });
                if (!changed) break;
            }
            rdl = rdl[..start] + part + rdl[end..];
        }
        return rdl;
    }

    private void WriteReport(XmlWriter w, ReportDefinition report)
    {
        w.WriteStartElement("Report", RdlNs);
        w.WriteElementString("Description", RdlNs, Description ?? report.ReportTitle);
        w.WriteElementString("Author", RdlNs, report.Author);
        w.WriteElementString("Name", RdlNs, SanitizeName(report.ReportTitle));
        // ReportDefinition.Language carries the number separators the file records, and it
        // used to go out here, as RDL's report-level <Language>. That is too broad a place
        // for it. RDL's report Language is the whole report's culture, and it takes dates
        // with it: a report whose numbers use "," and "." was declared en-US, and every date
        // in it that Crystal leaves to the rendering machine was then rendered US-style.
        //
        // Country-Region-Sort is the measured case. Its print date is Format(ExecutionTime,
        // "d") - the machine's own short date pattern, which is what Crystal prints there -
        // and the real engine renders "2026-08-28" on this box while we rendered "9/11/2026"
        // from the same run. Neither number nor separator was involved.
        //
        // So the separators now go on the styles that actually need them and nowhere else,
        // and the report names no culture at all. With none named the engine falls back to
        // CultureInfo.CurrentCulture - Report.EvalLanguage - which is the machine, which is
        // what Crystal defers to. The two agree by deferring rather than by both being told.
        _separatorCulture = string.IsNullOrEmpty(report.Language) ? null : report.Language;

        WritePage(w, report.Page);
        WriteDataSources(w, report.DataSources);
        WriteDataSets(w, report);
        WriteEmbeddedImages(w, report);
        WriteReportParameters(w, report);
        var sectionsConsumedByTable = WriteBody(w, report);
        WritePageHeader(w, report, sectionsConsumedByTable);
        WritePageFooter(w, report, sectionsConsumedByTable);

        w.WriteEndElement(); // Report
    }

    /// <summary>
    /// How far Crystal draws a page above where its twips put it: the part of the page
    /// height that is not a whole number of points, in twips (0-19).
    ///
    /// Crystal lays a page out at its true height, anchors it from the bottom, and writes
    /// the page box truncated to whole points. On a page of whole points - Letter's 792 -
    /// that changes nothing. On A4's 841.8pt the box is 841 and everything, header to
    /// footer, lands 0.8pt higher than its twips say. That is boyum__SampleReport's
    /// constant offset, which a margin, a bound or a row pitch could not explain because
    /// the page number at the foot of the page was out by the same amount as the title at
    /// its head.
    ///
    /// Measured with Crystal's own PDFs against its own geometry, so it does not depend on
    /// this converter: the distance from a page-footer object's top to its text's top is
    /// +2.51pt on 26 whole-point pages, and SampleReport's (841.80) is +1.71, which is 2.51
    /// less its 0.80 to the hundredth. The Boyum footers of one design (Arial 8, a 240-twip
    /// box) sit 0.50pt apart between 595.20pt pages and 841.70pt ones - 0.50 being the
    /// difference in their fractions.
    ///
    /// This engine truncates the box the same way but places everything from the top, so
    /// the emulation is to lay the page out on the truncated height and lift the top margin
    /// by the fraction: every top-anchored position rises by it, the page footer rises with
    /// the shorter page, and the body keeps its height, so pagination does not change.
    /// </summary>
    private static int PageHeightFractionTwips(PageLayout page) => page.HeightTwips % 20;

    /// <summary>
    /// A whole number of points, written so that it survives the engine as that number.
    ///
    /// The engine holds a size as whole parts of 1/2540in (RSize) and takes the PDF page box
    /// from it with a truncating cast. Most whole-point heights are not a whole number of
    /// parts, and the nearest part can fall just short: 595pt becomes 20,990 parts, which is
    /// 594.992pt, and the box comes out 594. That is A4 landscape, 44 public reports, and it
    /// would put a page 1pt shorter than Crystal's. So the height is written as the first
    /// part at or above it - 20,991 parts, 595.0205pt - which truncates to the number meant
    /// and is 0.02pt of layout nothing can see. A height the engine's own rounding already
    /// keeps whole - Letter's 792 and 612, A4's 841 - is written exactly as before.
    /// </summary>
    private static string WholePointsForEngine(int points)
    {
        const double PartsPerPoint = 2540.0 / 72.0;
        if (Math.Round(points * PartsPerPoint) / PartsPerPoint >= points - 1e-9)
            return $"{points}pt";
        double parts = Math.Ceiling(points * PartsPerPoint);
        return $"{(parts / PartsPerPoint).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}pt";
    }

    private void WritePage(XmlWriter w, PageLayout page)
    {
        int fraction = PageHeightFractionTwips(page);
        w.WriteElementString("PageHeight", RdlNs, WholePointsForEngine((page.HeightTwips - fraction) / 20));
        w.WriteElementString("PageWidth", RdlNs, TwipsToRdl(page.WidthTwips));
        w.WriteElementString("TopMargin", RdlNs, TwipsToRdl(Math.Max(0, page.TopMarginTwips - fraction)));
        w.WriteElementString("BottomMargin", RdlNs, TwipsToRdl(page.BottomMarginTwips));
        w.WriteElementString("LeftMargin", RdlNs, TwipsToRdl(page.LeftMarginTwips));
        w.WriteElementString("RightMargin", RdlNs, TwipsToRdl(page.RightMarginTwips));
        // The body width. Without it the engine warns ("Width not specified for report,"
        // 3,058 times across the corpus) and assumes the full page, so body content is
        // allowed to run underneath the margins instead of being wrapped or paginated at
        // the printable edge. Crystal has no equivalent field - its sections are page
        // width less the margins by construction - so that is what is written here.
        int bodyWidth = page.WidthTwips - page.LeftMarginTwips - page.RightMarginTwips;
        w.WriteElementString("Width", RdlNs, TwipsToRdl(bodyWidth > 0 ? bodyWidth : page.WidthTwips));
    }

    private void WriteDataSources(XmlWriter w, List<DataSource> sources)
    {
        if (sources.Count == 0)
        {
            // Emit a placeholder DataSource that the report author can fill in
            w.WriteStartElement("DataSources", RdlNs);
            w.WriteStartElement("DataSource", RdlNs);
            w.WriteAttributeString("Name", "DataSource1");
            w.WriteStartElement("ConnectionProperties", RdlNs);
            w.WriteElementString("DataProvider", RdlNs, "SQL");
            w.WriteElementString("ConnectString", RdlNs, "");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            return;
        }

        w.WriteStartElement("DataSources", RdlNs);
        foreach (var ds in sources)
        {
            w.WriteStartElement("DataSource", RdlNs);
            w.WriteAttributeString("Name", SanitizeName(ds.Name));
            w.WriteStartElement("ConnectionProperties", RdlNs);
            w.WriteElementString("DataProvider", RdlNs, MapDataSourceKind(ds.Kind));
            // The element is required by the engine even when empty (ConnectionProperties).
            w.WriteElementString("ConnectString", RdlNs,
                OmitConnections ? "" :
                ds.OdbcDsn != null ? $"DSN={ds.OdbcDsn}" :
                ds.ServerName != null ? $"Data Source={ds.ServerName};Initial Catalog={ds.DatabaseName}" : "");
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    // The calculated field a per-record page break groups the details on.
    private const string DetailRowFieldName = "CrystalDetailRow";

    /// <summary>
    /// Whether the details break the page around every record. Crystal puts "New Page
    /// Before/After" on the Details section; RDL puts page breaks on a Grouping, and a Details
    /// grouping prints one row per group, so a per-record break needs a key that differs on
    /// every record - a calculated row number (DetailRowFieldName) - or records sharing a value
    /// would fold into one. Only the checkbox is expressed. A formula-driven break needs
    /// PageBreakCondition, which the engine ignores on a Details grouping, and breaking on every
    /// record instead would be wrong for a formula like "every 18th record". A suppressed
    /// section's own options do not apply.
    /// </summary>
    private static (bool Before, bool After) DetailPageBreaks(ReportDefinition report)
    {
        var shown = report.Sections.Where(s => s.Type == SectionType.Details && !s.Suppress).ToList();
        return (shown.Any(s => s.NewPageBefore && s.NewPageBeforeFormula is null),
                shown.Any(s => s.NewPageAfter && s.NewPageAfterFormula is null));
    }

    private void WriteDataSets(XmlWriter w, ReportDefinition report)
    {
        var ds = report.DataSources.FirstOrDefault() ?? new DataSource { Name = "DataSource1" };
        string dsName = SanitizeName(ds.Name.Length > 0 ? ds.Name : "DataSource1");

        w.WriteStartElement("DataSets", RdlNs);
        w.WriteStartElement("DataSet", RdlNs);
        w.WriteAttributeString("Name", "DataSet1");

        w.WriteStartElement("Query", RdlNs);
        w.WriteElementString("DataSourceName", RdlNs, dsName);
        string commandText = ds.SqlQuery ?? BuildSelectFromTables(ds, report.TableLinks);
        // A file whose QESession did not decode has no table list; fall back to the fields.
        if (commandText == "SELECT * FROM <TableName>")
            commandText = BuildSelectFromFields(report.Fields.OfType<DatabaseField>().ToList());
        w.WriteElementString("CommandText", RdlNs, commandText);
        w.WriteEndElement(); // Query

        var dbFields = report.Fields.OfType<DatabaseField>().ToList();
        var formulaFields = report.Fields.OfType<FormulaField>().ToList();
        var runningTotals = report.Fields.OfType<RunningTotalField>().ToList();

        if (dbFields.Count > 0 || formulaFields.Count > 0 || runningTotals.Count > 0)
        {
            // Every <Field Name> written below, so no name is emitted twice.
            var emittedFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            w.WriteStartElement("Fields", RdlNs);
            foreach (var f in dbFields)
            {
                // RDL has no duplicate <Field Name>, and the engine reports one as
                // "Field X has duplicates" and then drops all but the first - so a
                // duplicate is silent data loss rather than a cosmetic problem. Names
                // collide here for two reasons: real columns from different tables that
                // differ only in case (the engine matches names case-insensitively, so
                // it cannot tell them apart either), and Crystal format hooks, which the
                // parser filters. Neither can be represented twice; keep the first.
                if (!emittedFieldNames.Add(ColumnFieldName(f))) continue;
                w.WriteStartElement("Field", RdlNs);
                w.WriteAttributeString("Name", ColumnFieldName(f));
                // The DataField is the column the engine reads from the pushed table, and it
                // is qualified by table ("Orders.Customer ID") when the field's table is
                // known: a report joined in memory from per-table data names every column
                // that way, so two tables sharing a column name stay apart. A caller that
                // pushes one pre-joined table with bare column names still works: the
                // engine's RenderPrep adds the qualified columns before pushing.
                w.WriteElementString("DataField", RdlNs, QualifiedDataField(f));
                // Field.Type defaults to String when no TypeName is given (confirmed in
                // the engine's own Field.cs) — without this, a date/time column's real
                // runtime type never reaches the expression parser's per-argument type
                // inference (Parser.cs: GetTypeCode() on each arg), so a strongly-typed
                // function overload like Month(DateTime) never matches a call on that
                // field and fails with "Function Month is not known" even though the
                // method exists and the column genuinely is a date.
                if (RdlFieldTypeName(f.DataType) is string typeName)
                    w.WriteElementString("TypeName", RdlNs, typeName);
                w.WriteEndElement();
            }
            // A top or bottom N group's rank, a column the renderer adds to the data
            // (RptEngine's TableJoiner.AddTopNRanks); see TopNRankField.
            foreach (var g in report.Groups.Where(g => g.TopN is not null))
            {
                string rank = TopNRankField(g.Level);
                if (!emittedFieldNames.Add(rank)) continue;
                w.WriteStartElement("Field", RdlNs);
                w.WriteAttributeString("Name", rank);
                w.WriteElementString("DataField", RdlNs, rank);
                w.WriteElementString("TypeName", RdlNs, "Int32");
                w.WriteEndElement();
            }
            var dbFieldNames = dbFields.Select(ColumnFieldName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rtFieldNames = runningTotals.Select(f => SanitizeName(f.Name.Length > 0 ? f.Name : "RunTotal"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var numericFormulaNames = NumericallyUsedFormulaNames(report, formulaFields);
            // Every name this DataSet will actually declare a <Field> for — what a
            // Fields! reference in any of these expressions is allowed to resolve to.
            // Includes the formulas skipped by the collision guards above: those names
            // are still declared, by the database column or running total they collide with.
            var dataSetFieldNames = dbFieldNames
                .Concat(rtFieldNames)
                .Concat(formulaFields.Select(f => SanitizeName(f.Name.Length > 0 ? f.Name : "Formula")))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Columns the engine will treat as text, for the arithmetic coercion below.
            // A column with no RDL TypeName defaults to String there (see the DataField
            // loop above), so "String" and "no type at all" are the same case.
            var stringFieldNames = dbFields
                .Where(f => RdlFieldTypeName(f.DataType) is null)
                .Select(ColumnFieldName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Which formulas assign each variable, across the whole report tree.
            var variableWriters = Formula.VariableWriters.Collect(Root ?? report);
            var recordCounters = PrintTimeVariables.RecordCounters(report, variableWriters);
            var numericColumns = dbFields.Where(f => IsNumericType(f.DataType))
                .Select(f => $"{f.TableName}.{f.ColumnName}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in formulaFields)
            {
                string safeName = SanitizeName(f.Name.Length > 0 ? f.Name : "Formula");
                // Same collision rule for running totals as for DB columns below: Crystal
                // reports pair a running total "#X" with a display formula also named "X"
                // (body "{#X} + ...") — emitting both produces duplicate <Field Name="X">
                // entries, the engine drops the *second* (the running total), and the
                // surviving formula then references itself. That's not just wrong: a
                // self-reference inside a compound expression stack-overflows the
                // engine's IsConstant/ConstantOptimization recursion, killing the whole
                // process uncatchably. The RunningValue-bound entry is the real value
                // carrier — keep it, skip the formula.
                if (rtFieldNames.Contains(safeName)) continue;
                // A formula whose name collides with a real database column (common when
                // an author names a formula after the exact column it pulls, e.g. formula
                // "Status" with body "{Header.Status}") would emit a second <Field> with
                // the same Name — RDL doesn't support duplicate Field names, and this one
                // in particular is usually self-referential (its own transpiled Value
                // expression resolves back to "Fields!Status.Value", i.e. itself), so it
                // can never evaluate. The real DataField-bound entry above already covers
                // this name correctly; skip the redundant, broken duplicate.
                if (dbFieldNames.Contains(safeName)) continue;
                if (!emittedFieldNames.Add(safeName)) continue;
                string expr = recordCounters.Contains(f.Name.TrimStart('@'))
                    ? "=RowNumber(Nothing)"
                    : FormulaTranspiler.ToRdlExpression(f,
                        name => variableWriters.TryGetValue(name, out var w) && w.Count == 1 && w.Contains(f),
                        numericColumns.Contains);
                // Same self-reference shape as the dup-guard above, minus the DB column:
                // a formula named after the column it pulls ({SerialNumbers.SeriesName} in
                // a formula called "SeriesName") whose underlying column is NOT part of
                // this DataSet transpiles to exactly "=Fields!SeriesName.Value" — a field
                // whose Value is itself. That's not merely wrong, it's fatal: the engine's
                // Field.Type/FunctionField.GetTypeCode pair recurses A->A (or through any
                // longer cycle) with no guard, and the whole RDLParser.Parse call dies
                // with a StackOverflowException that can't even be caught. The column
                // isn't in the DataSet, so no faithful translation exists — emit an empty
                // string so the field and every reference to it stay valid.
                // ANY surviving self-reference — bare or buried inside a compound
                // expression — is the same uncatchable engine stack overflow; direct
                // A->A only (longer cycles would need a graph pass; none seen yet).
                if (System.Text.RegularExpressions.Regex.IsMatch(expr,
                        $@"Fields!{System.Text.RegularExpressions.Regex.Escape(safeName)}\.Value",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    expr = "=\"\"";
                // A transpiled formula can also reference a name that resolves to nothing
                // at all: a column from a table this DataSet doesn't include, or a
                // parameter the formula wrote as a plain field reference. The engine
                // reports "Field 'X' not found" and the report fails — and when the report
                // is a subreport, it takes its parent down with it ("Subreport
                // Report_Subreport1 failed to compile"). No faithful translation exists, so
                // degrade exactly as the self-reference guard above does.
                if (ReferencesUnknownField(expr, dataSetFieldNames))
                    expr = "=\"\"";
                // Every degrade above and inside FormulaTranspiler lands on the same empty
                // string, which is fine until another formula does arithmetic on this
                // field: the engine then rejects that whole expression ("'-' operator works
                // only on numbers") and the report fails to render. When the corpus shows
                // this field being used as a number, degrade to 0 instead — the value is
                // equally unknown either way, but the arithmetic stays well-typed.
                if (expr == "=\"\"" && numericFormulaNames.Contains(safeName))
                    expr = "=0";
                expr = CoerceStringFieldsForArithmetic(expr, stringFieldNames);
                expr = CoerceStringFieldsForBoolean(expr, stringFieldNames);
                _formulaExpressions[safeName] = expr;
                w.WriteStartElement("Field", RdlNs);
                w.WriteAttributeString("Name", safeName);
                w.WriteElementString("Value", RdlNs, expr);
                w.WriteEndElement();
            }
            foreach (var (name, footerExpr) in PrintTimeVariables.FooterExpressions(report, text =>
                     {
                         string e = FormulaTranspiler.ToRdlExpression(new FormulaField { Name = "_", FormulaText = text });
                         // The variables stand in as placeholder fields until they are replaced.
                         if (ReferencesUnknownField(PrintTimeVariables.WithoutPlaceholders(e), dataSetFieldNames)) return null;
                         return CoerceStringFieldsForBoolean(CoerceStringFieldsForArithmetic(e, stringFieldNames), stringFieldNames);
                     }))
                _footerFormulaExpressions[SanitizeName(name)] = footerExpr;
            foreach (var f in runningTotals)
            {
                // Running totals don't have formula text; emit as RunningValue over the summarized field.
                // SummarizedFieldName may be empty if not yet parsed — fall back to 0.
                string safeName = SanitizeName(f.Name.Length > 0 ? f.Name : "RunTotal");
                string innerExpr = string.IsNullOrEmpty(f.SummarizedFieldName)
                    ? "0"
                    // SummarizedFieldName can itself be a formula/running-total reference
                    // ("@Line_Credit") rather than a plain field — strip the marker like
                    // every other field reference does, or SanitizeName turns the leading
                    // "@" into a literal "_" that never matches a real DataSet field.
                    : $"Fields!{SanitizeName(NormalizeFieldName(f.SummarizedFieldName))}.Value";
                string aggFn = f.Function switch
                {
                    AggregateFunction.Count or AggregateFunction.DistinctCount => "Count",
                    AggregateFunction.Average => "Avg",
                    AggregateFunction.Maximum => "Max",
                    AggregateFunction.Minimum => "Min",
                    _ => "Sum"
                };
                // A record is added only where the evaluate formula is true: what the others add
                // is nothing to a sum and no record to a count. Other functions take every record,
                // as they did, since the engine's running maximum and minimum do not skip a
                // missing value.
                if (f.EvaluateOn == RunningTotalCondition.OnFormula && aggFn is "Sum" or "Count"
                    && EvaluateConditionExpression(f.EvaluateCondition, dataSetFieldNames, stringFieldNames) is { } condition)
                    innerExpr = $"IIf({condition}, {innerExpr}, {(aggFn == "Sum" ? "0" : "Nothing")})";
                // A total that starts again on a change of group runs within that group.
                string scope = f.ResetOn == RunningTotalCondition.OnChangeOfGroup
                    && f.ResetGroup >= 1 && f.ResetGroup <= report.Groups.Count
                    ? $"\"Group{f.ResetGroup}\""
                    : "Nothing";
                if (!emittedFieldNames.Add(safeName)) continue;
                w.WriteStartElement("Field", RdlNs);
                w.WriteAttributeString("Name", safeName);
                w.WriteElementString("Value", RdlNs, $"=RunningValue({innerExpr}, {aggFn}, {scope})");
                w.WriteEndElement();
            }
            var (breakBefore, breakAfter) = DetailPageBreaks(report);
            if ((breakBefore || breakAfter) && emittedFieldNames.Add(DetailRowFieldName))
            {
                w.WriteStartElement("Field", RdlNs);
                w.WriteAttributeString("Name", DetailRowFieldName);
                w.WriteElementString("Value", RdlNs, "=RowNumber(Nothing)");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        // Emit DataSet-level Filters from the RecordSelectionFormula
        if (!string.IsNullOrWhiteSpace(report.RecordSelectionFormula))
        {
            // Transpile the Crystal selection formula to an RDL boolean expression
            var filterField = new Majorsilence.Crystal.Model.Fields.FormulaField
            {
                Name        = "_RecordFilter",
                Syntax      = Majorsilence.Crystal.Model.Fields.FormulaSyntax.Crystal,
                FormulaText = report.RecordSelectionFormula
            };
            string filterExpr = FormulaTranspiler.ToRdlExpression(filterField);
            // Only emit a filter when the expression contains an actual boolean comparison
            // (not just a bare field/parameter reference). Strip the leading "=" before checking.
            string innerExpr = filterExpr.StartsWith('=') ? filterExpr[1..] : filterExpr;
            bool isBooleanExpr = !string.IsNullOrWhiteSpace(innerExpr) &&
                (innerExpr.Contains('=') || innerExpr.Contains('<') || innerExpr.Contains('>') ||
                 innerExpr.Contains("Like", StringComparison.OrdinalIgnoreCase) ||
                 innerExpr.Contains(" And ", StringComparison.OrdinalIgnoreCase) ||
                 innerExpr.Contains(" Or ", StringComparison.OrdinalIgnoreCase));
            // A selection formula that tests a report parameter cannot select anything when no
            // value for that parameter arrives, and none does unless a caller supplies one -
            // parameters are a render-time concern here, so the converter cannot know. Left
            // as a bare comparison the filter is false for every row and the report comes out
            // blank: three of this suite's fixture-backed reports rendered an empty page for
            // exactly this reason, their selection being Crystal's spelling of a range,
            // {Orders.Order Amount} = {?Order_Amt_Range}.
            //
            // So an unanswered parameter makes the filter inert rather than exclusive. This is
            // not "drop the filter": a caller that does supply a value still gets the
            // filtering, which is verified both ways - with no value all rows render, and with
            // one only the matching row does. If any referenced parameter is unanswered the
            // whole comparison is skipped, because a comparison against nothing has no
            // meaningful answer.
            if (!string.IsNullOrWhiteSpace(filterExpr) && filterExpr.Length > 1 && isBooleanExpr)
            {
                var paramRefs = System.Text.RegularExpressions.Regex
                    .Matches(innerExpr, @"Parameters!\w+\.Value")
                    .Select(m => m.Value)
                    .Distinct()
                    .ToList();
                if (paramRefs.Count > 0)
                    filterExpr = "=("
                        + string.Join(" OrElse ", paramRefs.Select(r => $"IsNothing({r})"))
                        + $" OrElse ({innerExpr}))";

                w.WriteStartElement("Filters", RdlNs);
                w.WriteStartElement("Filter", RdlNs);
                w.WriteElementString("FilterExpression", RdlNs, filterExpr);
                w.WriteElementString("Operator", RdlNs, "Equal");
                w.WriteStartElement("FilterValues", RdlNs);
                w.WriteElementString("FilterValue", RdlNs, "=true");
                w.WriteEndElement(); // FilterValues
                w.WriteEndElement(); // Filter
                w.WriteEndElement(); // Filters
            }
        }

        w.WriteEndElement(); // DataSet

        if (RowSentinel)
        {
            w.WriteStartElement("DataSet", RdlNs);
            w.WriteAttributeString("Name", RowSentinelDataSetName);
            w.WriteStartElement("Query", RdlNs);
            w.WriteElementString("DataSourceName", RdlNs, dsName);
            w.WriteElementString("CommandText", RdlNs, "SELECT 1 AS Row");
            w.WriteEndElement(); // Query
            w.WriteStartElement("Fields", RdlNs);
            w.WriteStartElement("Field", RdlNs);
            w.WriteAttributeString("Name", RowSentinelDataSetName);
            w.WriteElementString("DataField", RdlNs, "Row");
            w.WriteEndElement(); // Field
            w.WriteEndElement(); // Fields
            w.WriteEndElement(); // DataSet
        }

        w.WriteEndElement(); // DataSets
    }

    private void WriteReportParameters(XmlWriter w, ReportDefinition report)
    {
        var paramFields = report.Fields.OfType<ParameterField>().ToList();
        if (paramFields.Count == 0) return;

        w.WriteStartElement("ReportParameters", RdlNs);
        foreach (var p in paramFields)
        {
            w.WriteStartElement("ReportParameter", RdlNs);
            // SAP Business One parameters are named "$[InternalId]" rather than a plain
            // identifier — strip that wrapper so the declared Name here matches what
            // FormulaTranspiler/RdlEmitter produce at every reference site (?$[Id],
            // {?$[Id]}), or the parameter it declares never matches anything referencing it.
            w.WriteAttributeString("Name", ParameterRdlName(p));
            // Map Crystal data type to SSRS parameter type
            string rdlType = p.DataType switch
            {
                "Float64" or "Float32" or "Currency" or "Int16" or "Int32" => "Float",
                "Boolean" => "Boolean",
                "DateTime" => "DateTime",
                _ => "String"
            };
            w.WriteElementString("DataType", RdlNs, rdlType);
            // A converted report has no way to prompt, so every parameter has to be
            // renderable without a value. Declaring them nullable says so: the engine
            // then leaves an unsupplied parameter empty instead of failing the render
            // trying to convert nothing into the declared type - which took down whole
            // reports when the empty value was relayed on to a subreport.
            w.WriteElementString("Nullable", RdlNs, "true");
            // Same reasoning applies to an empty (non-null) string, a materially
            // different case the engine validates separately (ReportParameter.cs's
            // AllowBlank check, independent of Nullable): a String-typed parameter fed
            // by a degraded/unresolvable formula, or by a genuinely blank real field,
            // arrives as "" rather than null and AllowBlank defaults to false, so it
            // threw "Empty string isn't allowed for X" - the exact same class of
            // render-killing failure Nullable was already added to prevent, just for
            // "" instead of null. A converted report can't validate a value it never
            // prompted for either way.
            w.WriteElementString("AllowBlank", RdlNs, "true");
            // Crystal stores the value the report was last run with; it is the closest
            // thing to an intended default and beats rendering the parameter blank.
            if (!string.IsNullOrEmpty(p.DefaultValue))
            {
                w.WriteStartElement("DefaultValue", RdlNs);
                w.WriteStartElement("Values", RdlNs);
                w.WriteElementString("Value", RdlNs, p.DefaultValue);
                w.WriteEndElement(); // Values
                w.WriteEndElement(); // DefaultValue
            }
            if (!string.IsNullOrEmpty(p.PromptText))
                w.WriteElementString("Prompt", RdlNs, p.PromptText);
            else
                w.WriteElementString("Prompt", RdlNs, p.Name);
            // The pick list is read as the strings the file stores, which for a number or a
            // date are the descriptions alone ("English" for the value 3), not values of its type.
            if (p.PickListValues.Count > 0 && rdlType == "String")
            {
                // ValidValues recognizes exactly two direct children, DataSetReference or
                // ParameterValues (see the engine's own ValidValues.cs) — no NonQueried
                // wrapper (that's real SSRS 2008+ schema, not what this engine parses).
                // Wrapping it there means the switch never finds ParameterValues, leaves
                // both null, and the ctor logs a fatal (Severity 8) "neither specified"
                // error — which, like the empty-ReportItems case, cascades into null
                // reference exceptions on every later expression evaluation.
                w.WriteStartElement("ValidValues", RdlNs);
                w.WriteStartElement("ParameterValues", RdlNs);
                foreach (var (value, label) in p.PickListValues)
                {
                    w.WriteStartElement("ParameterValue", RdlNs);
                    w.WriteElementString("Value", RdlNs, value);
                    w.WriteElementString("Label", RdlNs, label);
                    w.WriteEndElement();
                }
                w.WriteEndElement(); // ParameterValues
                w.WriteEndElement(); // ValidValues
            }
            w.WriteEndElement(); // ReportParameter
        }
        w.WriteEndElement(); // ReportParameters
    }

    private static string EmbeddedImageName(int embeddingIndex) => $"EmbeddedImage{embeddingIndex}";

    private void WriteEmbeddedImages(XmlWriter w, ReportDefinition report)
    {
        var images = report.Sections
            .SelectMany(s => s.Objects)
            .OfType<ImageObject>()
            .Where(i => i.Source == ImageSourceKind.Embedded && i.ImageData is not null)
            .GroupBy(i => i.EmbeddingIndex)
            .OrderBy(g => g.Key)
            .ToList();
        if (images.Count == 0) return;

        w.WriteStartElement("EmbeddedImages", RdlNs);
        foreach (var group in images)
        {
            var img = group.First();
            w.WriteStartElement("EmbeddedImage", RdlNs);
            w.WriteAttributeString("Name", EmbeddedImageName(group.Key));
            w.WriteElementString("MIMEType", RdlNs, img.MimeType!);
            w.WriteElementString("ImageData", RdlNs, System.Convert.ToBase64String(img.ImageData!));
            w.WriteEndElement();
        }
        w.WriteEndElement(); // EmbeddedImages
    }

    // Returns the PageHeader/PageFooter section(s) consumed into the Details Table's own
    // Header/Footer bands, so the caller can skip re-emitting them as RDL's own
    // <PageHeader>/<PageFooter>.
    private List<Section> WriteBody(XmlWriter w, ReportDefinition report)
    {
        _shapePieces = ShapePiecesAcrossReportHeaders(report);
        _shapesInPieces.Clear();
        _shapesInPieces.UnionWith(_shapePieces.Values.SelectMany(p => p).Select(p => p.Shape));

        var detailsSections = report.Sections
            .Where(s => s.Type == SectionType.Details)
            .ToList();

        var groupHeaders = report.Sections
            .Where(s => s.Type == SectionType.GroupHeader)
            .OrderBy(s => s.GroupLevel)
            .ToList();

        var groupFooters = report.Sections
            .Where(s => s.Type == SectionType.GroupFooter)
            .OrderBy(s => s.GroupLevel)
            .ToList();

        // Emit a Table if we have details and at least one column worth of objects
        var detailObjects = detailsSections.SelectMany(s => s.Objects).ToList();
        var consumedByTable = new List<ReportObject>();

        // Objects in Details alone aren't enough — the table also needs a column to build
        // from, or WriteDetailsTable writes nothing and the Body is left having promised a
        // table it never emits. Both sides ask DetailsTableHasColumns so they cannot drift.
        //
        // A report that reads no table at all has nothing for a details table to repeat
        // over: its dataset has no rows, so the details row would print zero times. Crystal
        // prints its Details section once, which is what the band below does instead.
        bool readsNoTable = !report.Fields.OfType<DatabaseField>().Any();
        bool hasTable = detailObjects.Count > 0 && !readsNoTable && DetailsTableHasColumns(detailsSections, report);

        // A free-standing section's FieldObjects need Fields! access that RDL can never
        // give them there. This isn't really a PageHeader/PageFooter-specific rule — it's
        // broader: Expression.cs's FinalPass only finds a `fields` collection by walking
        // up to an enclosing DataRegion/DataSetDefn, and NONE of the four "global" section
        // kinds (PageHeader, PageFooter, ReportHeader, ReportFooter) are ever inside one,
        // so a FieldObject placed directly in *any* of them hits the same "Field not
        // found" wall — confirmed the hard way: a subreport nested inside a PageHeader
        // (itself already routed below) turned out to have its own field-bound
        // ReportHeader, which fails identically. Route any of the four into a Table's own
        // Header/Footer band instead — the existing Details Table when there is one, or
        // (further below) a minimal synthetic Table created solely to host this content
        // when Details is empty. Crystal often splits one logical header into several
        // same-type sections (one per subreport strip) — collect every one that needs
        // Fields!, not just the first, or later field-bound sections still fail the exact
        // same way this fix is meant to close.
        // Subreports hit a related but distinct restriction: this engine explicitly
        // rejects one placed directly in PageHeader/PageFooter ("The Subreport 'X' is not
        // allowed in a PageHeader or PageFooter" — Subreport.cs's own FinalPass check),
        // regardless of whether that section also has FieldObjects. Route those the same
        // way — WriteFreeFormObjects (used by WriteTableFreeFormRow below) already knows
        // how to emit a Subreport correctly inside a TableCell. ReportHeader/ReportFooter
        // aren't covered by this particular restriction, only the Fields! one above.
        // Every name the DataSet or a formula already claims. A reference to one of these
        // resolves to Fields! whatever else it might look like, because the emitters check
        // them first — so a column genuinely called "Print Date" is not mistaken for
        // Crystal's special field of that name.
        var dataBoundNames = report.Fields
            .Select(f => NormalizeFieldName(f.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A Crystal special field becomes a Globals expression or a literal, and neither
        // needs an enclosing data region. RowNumber() is the exception in that list: it
        // counts the rows of one, so it does.
        bool IsScopeFreeSpecialField(string fieldName)
        {
            // A formula that reads no data - boyum__BOM's "Page: 1 of 1" in four languages,
            // from a parameter and the page numbers - is written in its place in a page band
            // (InlineFormulasInPageBands), so it needs no data scope either.
            if (fieldName.Trim().TrimStart('{').StartsWith('@')
                && IsDataFreeFormula(SanitizeName(NormalizeFieldName(fieldName.Trim().Trim('{', '}'))), []))
                return true;
            string norm = NormalizeFieldName(fieldName);
            int dot = norm.IndexOf('.');
            if (dataBoundNames.Contains(norm)
                || (dot >= 0 && dataBoundNames.Contains(norm[(dot + 1)..])))
                return false;

            string? expr = SpecialFieldExpression(fieldName, report.ReportComments, report.ReportTitle);
            return expr is not null
                && !expr.Contains("Fields!", StringComparison.Ordinal)
                && !expr.Contains("RowNumber(", StringComparison.Ordinal);
        }

        // Deliberately positive-only: a brace reference counts as scope-free just when it
        // is identified as a special field. A group name, a formula, a column, something
        // this converter cannot place at all — anything else keeps the section in the
        // table, which is where it already works.
        bool AllBraceRefsAreScopeFree(string text)
        {
            int pos = 0;
            while (true)
            {
                int open = text.IndexOf('{', pos);
                if (open < 0) return true;
                int close = text.IndexOf('}', open + 1);
                if (close < 0) return true;   // unterminated — emitted as a literal
                if (!IsScopeFreeSpecialField(text[(open + 1)..close])) return false;
                pos = close + 1;
            }
        }

        // Only a PageFooter is let off. RDL's own <PageFooter> renders at the foot of every
        // page, which is where Crystal puts one; routed into the table's footer band it
        // renders where the table ends instead, which on a short report is immediately
        // under the last detail row. Every other section type this function routes has
        // nowhere better to be, and a ReportFooter in particular is picked up from the
        // table's own footer band by joining its TextObjects — exempting one would drop it.
        bool ScopeFree(Section s, Func<bool> test) => s.Type == SectionType.PageFooter && test();

        bool NeedsTableRouting(Section s) =>
            s.Objects.OfType<FieldObject>()
                .Any(f => !ScopeFree(s, () => IsScopeFreeSpecialField(f.FieldName)))
            // A TextObject with embedded {Field} references resolves to the same Fields!
            // expressions a FieldObject does (ResolveTextWithFieldRefs), so it needs the
            // same data scope — iPayment's PageHeader "Credit Card Transaction Details
            // for {BusinessPartner.CardName}" failed exactly like a placed FieldObject.
            || s.Objects.OfType<TextObject>()
                .Any(t => t.Text.Contains('{') && !ScopeFree(s, () => AllBraceRefsAreScopeFree(t.Text)))
            || ((s.Type == SectionType.PageHeader || s.Type == SectionType.PageFooter)
                && s.Objects.OfType<SubreportObject>().Any(sub => sub.Report is not null))
            // A subreport passing a parent *field* as a parameter needs the data scope for
            // the same reason a placed FieldObject does, in any section — the section may
            // hold nothing else, so neither clause above catches it.
            || s.Objects.OfType<SubreportObject>().Any(sub => SubreportBindsParentFields(sub, report))
            // A database-bound image is a field reference too: its Source is the column,
            // emitted as Fields!X.Value (WriteImageSourceElements). Barcode columns placed
            // in a PageFooter are the common shape, and they fail on scope exactly like a
            // placed FieldObject does, even though the column is in the DataSet.
            || s.Objects.OfType<ImageObject>().Any(i => i.Source == ImageSourceKind.Database)
            // A ReportFooter is skipped by the free-form pass whenever a Details table
            // exists — its content is expected to land in the table's own footer band. But
            // that band is built by joining the section's *TextObjects*, so a subreport
            // placed there is silently dropped: Top5USAsubCanada's whole second page (its
            // Canada subreport) never appeared in the output at all. Routing the section
            // puts it in the band through the free-form row writer, which knows every kind.
            // A chart there is lost the same way: Top5USA-piechart's report-footer pie never
            // printed. Charts were once held back here over a "Duplicate Grouping name
            // 'ChartCategory_...'" error, which was not the chart printing twice but two
            // charts (that file's bar and pie) over the same field declaring the same
            // grouping name; groupings are now named per chart (WriteChartGrouping).
            || (s.Type == SectionType.ReportFooter
                && s.Objects.Any(o => o is SubreportObject { Report: not null } or ChartObject))
            // A section-level suppress formula becomes the Hidden expression on every item
            // this section emits, so a field-dependent one ("suppress unless this work
            // order is billable") needs the data scope just as much as the content does —
            // and the section can hold only objects none of the clauses above match.
            // Requires content: routing an empty section produces a free-form row whose
            // Rectangle has nothing in it, which is itself fatal ("At least one item must
            // be in the ReportItems"), and a suppress formula hiding nothing is moot.
            || (HasRenderableContent(s)
                && TranspileSuppressFormula(s.SuppressFormula, report)?.Contains("Fields!", StringComparison.Ordinal) == true)
            // An object's own Suppress formula reading a field needs the scope as much.
            || s.Objects.Any(o => o.SuppressOverride is null
                && ObjectSuppressExpression(o, report)?.Contains("Fields!", StringComparison.Ordinal) == true);

        // GroupHeader/GroupFooter normally need none of this: when a Details table exists
        // their content goes into TableGroup Header/Footer rows, which are already inside
        // the data region. But with an empty Details section (the cross-tab reports —
        // everything lives in a GroupHeader beside a Matrix) there are no TableGroup rows
        // to land in, so the section falls through to the same scope-less free-form Body
        // path, and a group-name FieldObject there fails identically. Route them too, but
        // only in that no-table case, so the normal tabular path is untouched.
        // Crystal prints the Report Header above the Page Header on page one, and the
        // Page Header again at the top of every page after it. RDL's PageHeader can only
        // do the second half of that: it is pinned to the very top of every page,
        // including page one, so a page header left there prints above the report header
        // instead of below it. On the one reference report that can be measured, that put
        // the column labels above the title and logo and left the whole header block
        // about half an inch out of place.
        //
        // The table's own Header band is the right home instead. It sits below whatever
        // the Body draws above the table - the report header - and RepeatOnNewPage gives
        // back the "every page" half. So a Page Header always routes here, not only when
        // it holds a field reference that needs a data scope, which is why this list is no
        // longer named for that.
        //
        // A Report Header that had to be routed does not go in that band: a header band is
        // only as wide as the table holding it and starts where that table starts. A
        // report header spans the page; a details table usually does not, and it is
        // indented to its first column. Left in there, the title and logo come out pushed
        // right and running off the end of a narrower table. Those get a full-width table
        // of their own where they belong among the report headers instead — see
        // WriteRoutedReportHeaders.
        var routedReportHeaders = report.Sections
            .Where(s => s.Type == SectionType.ReportHeader && NeedsTableRouting(s))
            .ToList();

        // A grouped report with nothing in Details still prints each group's header and
        // footer once per group, as a summary report does. With no detail columns there is
        // no details table to hold table groups, and its group sections went into the
        // header-only table's own header and footer, printing once: parking__orders, grouped
        // by month, printed "2024-01" and none of the groups after it. They become that
        // table's groups instead (WriteHeaderOnlyTable).
        bool groupsWithoutTable = !hasTable && !readsNoTable && report.Groups.Count > 0;
        var groupBands = groupsWithoutTable
            ? report.Sections.Where(s => s.Type is SectionType.GroupHeader or SectionType.GroupFooter).ToList()
            : [];

        // A routed report header gets a table of its own in place (WriteRoutedReportHeaders),
        // with or without a details table: in the shared table's header below every report
        // header it printed under the free-form ones that follow it in Crystal.
        var tableHeaderSections = Enumerable.Empty<Section>()
            // An empty Page Header is common - a band the report never put anything in -
            // and routing one here would emit a Rectangle wrapping an empty ReportItems,
            // which the engine treats as fatal and loses the whole report over.
            // One that is only a band of colour is drawn, as a row of that colour: a permit
            // list's blue rule above its column headings went missing, and the table with it
            // sat that much higher.
            .Concat(report.Sections.Where(s => s.Type == SectionType.PageHeader && !readsNoTable
                                               && (HasRenderableContent(s)
                                                   || s.BackColor is not null && !AlwaysSuppressed(s) && s.HeightTwips > 0)))
            .Concat(report.Sections.Where(s => !hasTable && !groupsWithoutTable && s.Type == SectionType.GroupHeader
                                               && NeedsTableRouting(s)))
            .ToList();
        var fieldBoundPageFooters = report.Sections
            .Where(s => (s.Type == SectionType.PageFooter || s.Type == SectionType.ReportFooter
                         || (!hasTable && !groupsWithoutTable && s.Type == SectionType.GroupFooter))
                        && NeedsTableRouting(s))
            .ToList();

        // Free-form text/field objects from non-detail body sections. When a Table was
        // emitted, GroupHeader/GroupFooter content is already inside the TableGroup
        // Header/Footer rows — exclude them to avoid duplication. ReportFooter likewise
        // moves into the Table's own top-level Footer (see WriteDetailsTable) so it prints
        // once after the last detail row instead of at this fixed Body position, which —
        // since Crystal's Report Footer is meant to print once at the very end of a
        // report that can span many pages — would otherwise land it on page 1, on top of
        // the Report Header. Sections already claimed by tableHeaderSections/Footers
        // above (a field-bound ReportHeader/ReportFooter, most commonly) are excluded here
        // too, for the same reason. When there is no table, include everything so
        // TextObjects still appear.
        var freeFormSections = report.Sections.Where(s =>
            s.Type != SectionType.Details &&
            s.Type != SectionType.PageHeader &&   // routed into the table's Header band
            s.Type != SectionType.PageFooter &&
            (s.Type != SectionType.GroupHeader || !hasTable) &&
            (s.Type != SectionType.GroupFooter || !hasTable) &&
            (s.Type != SectionType.ReportFooter || !hasTable) &&
            !tableHeaderSections.Contains(s) &&
            !routedReportHeaders.Contains(s) &&
            !fieldBoundPageFooters.Contains(s) &&
            !groupBands.Contains(s))
            .ToList();

        bool needsHeaderOnlyTable = !hasTable && (tableHeaderSections.Count > 0 || fieldBoundPageFooters.Count > 0
                                                  || groupBands.Count > 0);
        bool writesTable = hasTable || needsHeaderOnlyTable || routedReportHeaders.Count > 0;

        // A report that reads no table at all - text, parameters, special fields only -
        // has a Details section Crystal prints exactly once. With no columns there is no
        // details table to put it in, and it used to go nowhere: "Test Report" alone on a
        // page came out as a blank page. It is written once instead, as a band below the
        // report header. Only when nothing is read, because with a data source Crystal
        // prints Details once per record and once would be wrong.
        var detailsPrintedOnce = readsNoTable
            ? detailsSections.Where(s => !s.Suppress && HasRenderableContent(s)).ToList()
            : [];
        // Its Page Header cannot route into a table header either, for the same reason: a
        // table over a dataset with no rows prints nothing, header included. It goes in the
        // body as a band between the report header and Details, which is where Crystal
        // prints it on page one, and is handed back as consumed so it is not written again
        // as the RDL PageHeader.
        var pageHeadersInBody = readsNoTable
            ? report.Sections.Where(s => s.Type == SectionType.PageHeader && !s.Suppress && HasRenderableContent(s)).ToList()
            : [];
        // Every page header and Details section Crystal prints there takes its height, drawn
        // or not: an empty page header still pushes the Details below it down. Dropping the
        // empty ones put CrystalCmd's plain sample's text 0.42in above Crystal's. A section
        // suppressed outright is neither drawn nor spaced, as in Crystal.
        var bandsInBody = readsNoTable
            ? report.Sections.Where(s => s.Type == SectionType.PageHeader && !s.Suppress)
                .Concat(detailsSections.Where(s => !s.Suppress)).ToList()
            : [];

        w.WriteStartElement("Body", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(
            detailsSections.Sum(s => s.HeightTwips) + 720));

        // A Table guarantees at least one item once emitted, but a Body with neither a
        // Table nor any free-form content with something real to show would otherwise
        // emit an empty <ReportItems> — fatal (Severity 8) to the engine, same class of
        // bug as the PageHeader/PageFooter case (see HasRenderableContent). ReportItems is
        // optional under Body too, so omit it entirely rather than write an empty shell.
        if (writesTable || freeFormSections.Any(HasRenderableContent) || detailsPrintedOnce.Count > 0
            || pageHeadersInBody.Count > 0)
        {
            w.WriteStartElement("ReportItems", RdlNs);

            WriteRoutedReportHeaders(w, report, routedReportHeaders);

            if (hasTable || needsHeaderOnlyTable)
            {
                // The Table is a normal absolutely-positioned Body item like everything
                // else — it doesn't auto-flow below whatever else is in the Body. Report
                // Header content (title/logo/tagline) is written elsewhere in this same
                // Body at Top=0 (correctly — it's meant to be the first thing on the
                // page), so without an explicit Top here the Table starts at that same
                // Top=0 and renders directly on top of it. Push it down by the Report
                // Header area's total height (0 when there isn't one).
                int reportHeaderHeightTwips = ReportHeaderSpaceTwips(report.Sections);

                if (hasTable)
                {
                    WriteDetailsTable(w, report, detailsSections, groupHeaders, groupFooters, consumedByTable,
                        reportHeaderHeightTwips + EmptyPageHeaderTwips(report, tableHeaderSections),
                        tableHeaderSections, fieldBoundPageFooters);
                }
                else
                    WriteHeaderOnlyTable(w, report, tableHeaderSections, fieldBoundPageFooters,
                        reportHeaderHeightTwips + EmptyPageHeaderTwips(report, tableHeaderSections),
                        groupBands: groupBands, detailsSections: detailsSections);
            }

            foreach (var section in freeFormSections)
            {
                // A report header below another (Report Header a, b, ...) starts where the ones
                // above it end; object positions are relative to their own section.
                int top = section.Type == SectionType.ReportHeader
                    ? ReportHeaderSpaceTwips(report.Sections.TakeWhile(s => s != section))
                    : 0;
                if (section.BackColor is not null && section.HeightTwips > 0)
                    WriteSectionBackdrop(w, report, section, top);
                // A group section written free-form prints once, so it cannot print per
                // group; with no rows at all Crystal prints it once too, in the pass it makes
                // over the group sections (see AddNoRowsPass).
                WriteFreeFormObjects(w, section, report, topOffsetTwips: top,
                    liftedToBody: section.Type is SectionType.GroupHeader or SectionType.GroupFooter);
            }

            // Below the report header, the page header and then each Details section in
            // turn, each in a Rectangle of its own so its objects keep their positions.
            int detailsTop = ReportHeaderSpaceTwips(report.Sections);
            foreach (var section in bandsInBody)
            {
                if (pageHeadersInBody.Contains(section) || detailsPrintedOnce.Contains(section))
                    WriteBandRectangle(w, section, report, detailsTop);
                detailsTop += section.HeightTwips;
            }

            // Non-field objects (subreports, images, charts) — and Percentage-of-total
            // FieldObjects, which collide with their base summary field's column slot —
            // placed in group sections that the table rows had no empty cell for: emit as
            // positioned body items so they are not silently dropped.
            if (hasTable)
            {
                foreach (var section in report.Sections.Where(s =>
                    s.Type is SectionType.GroupHeader or SectionType.GroupFooter))
                {
                    var leftovers = section.Objects
                        .Where(o => o is SubreportObject { Report: not null } or ImageObject or ChartObject
                                      or FieldObject { SummaryFunction: AggregateFunction.Percentage })
                        // A database-bound image's Source is a Fields! reference, which
                        // cannot resolve out here at Body level — no DataRegion ancestor —
                        // so emitting one here is a guaranteed fatal costing the whole
                        // report. Group-section images now get rows inside the band that
                        // owns them (WriteQueuedExtrasRows) and so never reach this list;
                        // this stays as a backstop for any that still would.
                        .Where(o => o is not ImageObject { Source: ImageSourceKind.Database })
                        .Where(o => !consumedByTable.Contains(o))
                        .ToList();
                    if (leftovers.Count == 0) continue;
                    WriteFreeFormObjects(w, new Section
                    {
                        Type = section.Type,
                        Suppress = section.Suppress,
                        SuppressFormula = section.SuppressFormula,
                        Objects = leftovers
                    }, report, liftedToBody: true);
                }
            }

            w.WriteEndElement(); // ReportItems
        }
        w.WriteEndElement(); // Body

        // Callers only ever filter this by SectionType (WritePageHeader/WritePageFooter
        // each look for their own kind), so one combined list is enough.
        return tableHeaderSections.Concat(fieldBoundPageFooters).Concat(pageHeadersInBody).ToList();
    }

    private void WriteDetailsTable(XmlWriter w, ReportDefinition report,
        List<Section> detailsSections, List<Section> groupHeaders, List<Section> groupFooters,
        List<ReportObject> consumedExtras, int topOffsetTwips = 0, List<Section>? tableHeaderSections = null,
        List<Section>? fieldBoundPageFooters = null)
    {
        var dbFields = report.Fields.OfType<DatabaseField>().ToList();
        var formulaFieldNames = report.Fields.OfType<FormulaField>()
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Sorted left to right, because the order these are recorded in is the order the
        // report was authored in and has nothing to do with where the fields sit. Half the
        // public corpus and three quarters of the private one record at least one detail
        // field out of position - ProductPriceList-xs puts its rightmost column, Price, in
        // front of the one to its left - and everything below builds columns, widths and
        // cells from this list in the order it arrives in.
        //
        // Sorting within each section rather than across all of them: a report with stacked
        // detail bands has its objects grouped by band already, and flattening those into
        // one left-to-right sequence would interleave bands that are drawn on separate
        // lines. OrderBy is stable, so two objects sharing a Left keep the order they were
        // recorded in.
        var detailFieldObjects = detailsSections
            .SelectMany(s => s.Objects.OfType<FieldObject>().OrderBy(o => o.Bounds.Left))
            .ToList();

        // Image objects in detail sections become extra table columns after the field columns
        var detailImageObjects = detailsSections
            .SelectMany(s => s.Objects.OfType<ImageObject>())
            .Where(i => i.Source == ImageSourceKind.Database || i.ImageData is not null)
            .ToList();

        if (!DetailsTableHasColumns(detailsSections, report)) return;
        PlaceGroupUnderlays(groupHeaders.Concat(groupFooters));

        // Several Details sections print one line each under every record, so each gets a
        // row of its own over a column grid built from all of them - see BuildDetailBandGrid.
        // An image in one of them is placed where it is drawn, in its band's row, rather
        // than as a column after everything else.
        var bandGrid = BuildDetailBandGrid(detailsSections);
        bool gridColumns = bandGrid is { Starts.Count: > 0 };
        if (bandGrid is not null) detailImageObjects = [];

        // Maps a DataSet field's name (any casing) to its *actual* declared casing, used
        // to guard detail-row cell references — same reasoning as BuildKnownFieldsMap: a
        // detail FieldObject's own FieldName doesn't always match the DB column's stored
        // case exactly, and this engine's Fields lookup is case-sensitive, so emitting
        // Fields!X.Value with the FieldObject's casing instead of the declared <Field
        // Name>'s can fail even when the field is indisputably the right one.
        var runningTotalNames = report.Fields.OfType<RunningTotalField>()
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dataSetFieldsByName = dbFields.Select(f => f.ColumnName)
            .Concat(formulaFieldNames)
            .Concat(runningTotalNames)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Build column list from detail FieldObjects (preserves visual order, includes formula fields).
        // @-prefixed formula field references (e.g. @Discount) strip the @ so they match the DataSet.
        // Fall back to DB field ColumnNames when there are no detail FieldObjects.
        var columns = gridColumns
            ? bandGrid!.Names
            : detailFieldObjects.Count > 0
            ? detailFieldObjects.Select(f => NormalizeFieldName(f.FieldName)).ToList()
            : dbFields.Select(f => f.ColumnName).ToList();

        // Where each detail column begins, in the coordinates the report itself uses. The
        // RDL table lays its columns out contiguously from the left edge, but objects in
        // the other bands carry their original positions, so sorting them into columns has
        // to be done against the detail objects they were drawn above or below.
        //
        // Only usable while the detail objects run left to right, which is now the order
        // the column list is built in - see the sort above. What still reaches this guard is
        // a report whose detail objects overlap or share a Left, and a report with stacked
        // detail bands, where each band is sorted but the concatenation of them need not
        // ascend. Those keep the old behaviour: the list is left empty and every band falls
        // back to filling cells in declaration order, as it did before positions could be
        // read at all.
        var columnStarts = gridColumns
            ? new List<int>(bandGrid!.Starts)
            : detailFieldObjects.Select(f => f.Bounds.Left).ToList();
        for (int ci = 1; ci < columnStarts.Count; ci++)
        {
            if (columnStarts[ci] > columnStarts[ci - 1]) continue;
            columnStarts.Clear();
            break;
        }

        int defaultColWidth = report.Page.WidthTwips > 0
            ? report.Page.WidthTwips / Math.Max(1, columns.Count + detailImageObjects.Count)
            : 1440;

        // Build column-width lookup from detail FieldObject bounds (normalized names, twips)
        var colWidthByName = detailFieldObjects
            .Where(fo => fo.Bounds.Width > 0)
            .GroupBy(fo => NormalizeFieldName(fo.FieldName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Bounds.Width, StringComparer.OrdinalIgnoreCase);

        // Precompute each column's width so we can sum them for the Table element.
        //
        // A column is as wide as the distance to the next one, not as wide as the object
        // in it. Crystal leaves gaps between columns; an RDL table's columns are
        // contiguous, so taking each object's own width closes every gap and drags
        // everything to its right leftwards, cumulatively. On the grouped reference report
        // that is a 968-twip gap before the last column, which lands two thirds of an inch
        // left of where Crystal puts it - the values are in the right cells, the cells are
        // in the wrong places.
        //
        // The last column keeps its own width, having nothing to its right to measure to.
        var colWidths = gridColumns
            ? new List<int>(bandGrid!.Widths)
            : columnStarts.Count == columns.Count && columnStarts.Count > 0
            ? columns.Select((col, ci) => ci + 1 < columnStarts.Count
                ? columnStarts[ci + 1] - columnStarts[ci]
                : colWidthByName.TryGetValue(col, out int lw) ? lw : defaultColWidth).ToList()
            : columns.Select(col =>
                colWidthByName.TryGetValue(col, out int bw) ? bw : defaultColWidth).ToList();
        colWidths.AddRange(detailImageObjects.Select(img =>
            img.Bounds.Width > 0 ? img.Bounds.Width : defaultColWidth));
        int totalTableWidthTwips = colWidths.Sum();
        int totalCols = columns.Count + detailImageObjects.Count;

        // The first column starts where its objects do. Without this the whole table is
        // pulled to the body's left edge, which shifts every row by that much.
        int firstColumnTwips = columnStarts.Count > 0 && columnStarts[0] > 0 ? columnStarts[0] : 0;

        // A band written into this table — a page header, a field-bound page footer —
        // carries absolute page positions and is re-based on the table's own left edge.
        // Anything further left than that edge cannot be expressed, because RDL has no
        // negative Left, so it used to be clamped to zero and land on top of whatever sits
        // at the table's first column. A print date at the page's left margin came out
        // printed over the first column's heading.
        //
        // So the table starts at the leftmost thing in it, not at its first data column,
        // and a leading spacer column carries the difference. It is a column rather than an
        // indent on the band because the data columns must stay where the report drew them:
        // moving the table left without it would drag every detail row left with it.
        static int LeftmostObjectTwips(IEnumerable<Section> sections) => sections
            .SelectMany(s => s.Objects)
            .Select(o => o.Bounds.Left)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        // A detail band written free-form is one of those bands too, and so is a group band
        // that prints. A bill whose details are suppressed prints everything from its group
        // footers, laid out across the whole page, while its hidden detail columns start
        // three inches in: re-based on those, the bill's left half was pushed over its right.
        int bandLeftTwips = Math.Min(Math.Min(LeftmostObjectTwips(tableHeaderSections ?? []),
                                              LeftmostObjectTwips(fieldBoundPageFooters ?? [])),
                                     Math.Min(LeftmostObjectTwips(bandGrid?.FreeFormBands ?? []),
                                              LeftmostObjectTwips(groupHeaders.Concat(groupFooters)
                                                  .Where(s => !AlwaysSuppressed(s)))));
        int leadWidthTwips = bandLeftTwips == int.MaxValue
            ? 0
            : Math.Max(0, firstColumnTwips - Math.Max(0, bandLeftTwips));
        int leadCols = leadWidthTwips > 0 ? 1 : 0;
        int tableLeftTwips = firstColumnTwips - leadWidthTwips;
        int tableCols = totalCols + leadCols;

        w.WriteStartElement("Table", RdlNs);
        w.WriteAttributeString("Name", "Table1");
        if (topOffsetTwips > 0)
            w.WriteElementString("Top", RdlNs, TwipsToRdl(topOffsetTwips));
        if (tableLeftTwips > 0)
            w.WriteElementString("Left", RdlNs, TwipsToRdl(tableLeftTwips));
        w.WriteElementString("DataSetName", RdlNs, "DataSet1");
        WriteTopNFilters(w, report);
        w.WriteElementString("Width", RdlNs, TwipsToRdl(totalTableWidthTwips + leadWidthTwips));

        // TableColumns — use measured widths when available, fall back to uniform split
        w.WriteStartElement("TableColumns", RdlNs);
        if (leadCols > 0)
        {
            w.WriteStartElement("TableColumn", RdlNs);
            w.WriteElementString("Width", RdlNs, TwipsToRdl(leadWidthTwips));
            w.WriteEndElement();
        }
        for (int ci = 0; ci < totalCols; ci++)
        {
            w.WriteStartElement("TableColumn", RdlNs);
            w.WriteElementString("Width", RdlNs, TwipsToRdl(colWidths[ci]));
            w.WriteEndElement();
        }
        w.WriteEndElement();

        // A row of the DataSet's column names used to be emitted here as the table's
        // header. Crystal has no such row: the labels above a column are ordinary text
        // objects in the page header or the group header, and they are already rendered
        // from there. Emitting one as well duplicated the labels on any report that has
        // them, invented a row of raw column names on any report that does not, and
        // pushed the whole table down the page either way.
        //
        // The Header element itself is only written when there is a real row to put in
        // it, because RDL has no empty Header.
        var headerRows = tableHeaderSections ?? [];
        if (headerRows.Count > 0)
        {
            w.WriteStartElement("Header", RdlNs);
            w.WriteElementString("RepeatOnNewPage", RdlNs, "true");
            w.WriteStartElement("TableRows", RdlNs);
            foreach (var headerSection in headerRows)
                WriteTableFreeFormRow(w, headerSection, report, tableCols, tableLeftTwips);
            w.WriteEndElement(); // TableRows
            w.WriteEndElement(); // Header
        }

        // Build helpers for TextObject field-ref resolution used in group rows.
        // Include both DB columns and formula field names so {@FormulaName} refs resolve.
        var knownFieldsForGroups = BuildKnownFieldsMap(report);
        var groupNameMapForTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int gi2 = 0; gi2 < report.Groups.Count; gi2++)
            groupNameMapForTable[$"Group #{gi2 + 1} Name"] =
                GroupNameExpression(report.Groups[gi2]);

        // DB-field lookup shared by group header/footer cells.
        // Multiple tables can expose the same column name — first one wins for lookups.
        var dbFieldMap = report.Fields.OfType<DatabaseField>()
            .GroupBy(f => f.ColumnName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // TableGroups — one per group in the report
        if (report.Groups.Count > 0)
        {
            w.WriteStartElement("TableGroups", RdlNs);
            for (int gi = 0; gi < report.Groups.Count; gi++)
            {
                var grp = report.Groups[gi];
                string grpFieldNorm = NormalizeFieldName(grp.FieldName);
                // A level's band section is found by its group level, not by its place in the
                // list: a level can have several sections, and counting through them handed
                // every later level the section of the one before it.
                var ghSection = groupHeaders.FirstOrDefault(s => s.GroupLevel == gi);

                w.WriteStartElement("TableGroup", RdlNs);

                string fieldRef = $"Fields!{SanitizeName(grpFieldNorm)}.Value";
                string groupExpr = DatePartGroupExpression(fieldRef, grp.Condition);

                w.WriteStartElement("Grouping", RdlNs);
                w.WriteAttributeString("Name", $"Group{gi + 1}");
                w.WriteStartElement("GroupExpressions", RdlNs);
                w.WriteElementString("GroupExpression", RdlNs, groupExpr);
                w.WriteEndElement(); // GroupExpressions
                WriteGroupPageBreaks(w, report, gi);
                w.WriteEndElement(); // Grouping

                // TableGroup's own sort key: <Sorting><SortBy><SortExpression>/<Direction></SortBy></Sorting>
                // (same shape as Details' <Sorting>, written by WriteDetailSortExpressions) — NOT
                // the schema-invalid <SortExpressions> directly under <TableGroup> this used to emit,
                // which the engine silently ignored (a Severity-4 "unknown element" warning, not an
                // Error/Fatal, so it was never caught by the Error/Fatal-only engine-compat checks).
                w.WriteStartElement("Sorting", RdlNs);
                // A top or bottom N group prints in rank order, best first; its own sort
                // order only breaks ties. With no rank column every rank is empty and the
                // group's own order is all that is left.
                if (grp.TopN is not null)
                {
                    w.WriteStartElement("SortBy", RdlNs);
                    w.WriteElementString("SortExpression", RdlNs, $"=Fields!{TopNRankField(grp.Level)}.Value");
                    w.WriteEndElement(); // SortBy
                }
                w.WriteStartElement("SortBy", RdlNs);
                w.WriteElementString("SortExpression", RdlNs, groupExpr);
                if (grp.SortOrder == GroupSortOrder.Descending)
                    w.WriteElementString("Direction", RdlNs, "Descending");
                w.WriteEndElement(); // SortBy
                w.WriteEndElement(); // Sorting

                if (ghSection is not null)
                {
                    // Use TextObject content from the GroupHeader section when available -
                    // but only a text object that actually sits in the first column. A group
                    // header commonly holds the group's own field at the left and column
                    // labels further across ("Order Amount", "Date"), and taking the first
                    // text object regardless of position put a label in the first cell and
                    // dropped the group name entirely, so every group rendered captioned
                    // with the wrong words and nameless.
                    var ghTextObj = ghSection.Objects.OfType<TextObject>()
                        .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text)
                            && ColumnIndexForLeft(t.Bounds.Left, columnStarts) == 0);
                    var ghFieldObj = ghSection.Objects.OfType<FieldObject>()
                        .FirstOrDefault(f => ColumnIndexForLeft(f.Bounds.Left, columnStarts) == 0);
                    // Crystal's default caption is the group's name, which for a group on a
                    // date is the interval it covers ("2024-01"), not the first record's date.
                    // A database field placed there instead prints its own value.
                    bool captionIsGroupName = ghFieldObj is null
                        || ghFieldObj.FieldName.StartsWith("Group #", StringComparison.OrdinalIgnoreCase);
                    // The caption is an object of the section's own; Crystal makes none up. A
                    // header holding only a picture, a box or a subreport prints no group name.
                    bool hasCaptionObject = ghSection.Objects.Any(o => o is TextObject or FieldObject);
                    string ghCellValue = ghTextObj is not null
                        ? ResolveTextWithFieldRefs(ghTextObj.Text, knownFieldsForGroups, groupNameMapForTable, report.ReportComments, report.ReportTitle, BuildParameterMap(report))
                        : !hasCaptionObject ? string.Empty
                        : captionIsGroupName ? "=" + GroupNameExpression(grp) : $"=Fields!{SanitizeName(grpFieldNorm)}.Value";
                    // A group header's caption is as often a bound field as a text object -
                    // Crystal's default caption IS the group's own field - and only the text
                    // object case was read here. With no text object in the first column the
                    // value fell back to the group field expression, which is right, and the
                    // format fell back to a synthesized { Bold = true }, which throws away
                    // the object that expression came from: its font name, its size, its
                    // colour and its alignment all went to the engine's defaults. On
                    // SalesByCustomer-Grouped that rendered the customer name 14% narrower
                    // than the real engine draws it - not a metrics disagreement but a font
                    // this converter never asked for.
                    ObjectFormat? ghFormat = ghTextObj?.Format ?? ghFieldObj?.Format;

                    // The lead column is not always padding. It exists because something in
                    // this table reaches left of the first data column, and a group header's
                    // caption is very often the thing that does: Crystal's default puts the
                    // group's own field at the band's left edge - 60 twips in on
                    // SalesByCustomer-Grouped, where the first detail column starts at 532 -
                    // and writing an empty lead cell unconditionally pushed that caption a
                    // third of an inch to the right, indenting it past the very detail rows
                    // it captions instead of letting it sit out to their left.
                    //
                    // The cell spans the lead column and the first data column together,
                    // because a caption confined to the lead column would be clipped to the
                    // lead column's width - which is the gap between the band's left edge
                    // and the first data column, narrower than the caption by construction.
                    // Crystal's caption object is as wide as the whole band and overlaps
                    // whatever labels sit further across; a table row cannot overlap, so the
                    // faithful compromise is to give the caption the columns before the
                    // first one that holds something of its own, and the guard below is what
                    // establishes that the first data column is one of them.
                    // A header of no height and nothing to caption takes no space.
                    int ghRowHeightTwips = ghSection.HeightTwips > 0 ? ghSection.HeightTwips : hasCaptionObject ? 240 : 0;
                    int captionLeftTwips = ghTextObj?.Bounds.Left ?? ghFieldObj?.Bounds.Left ?? int.MaxValue;
                    bool firstDataColumnFree = !ghSection.Objects.Any(o =>
                        !ReferenceEquals(o, ghTextObj) && !ReferenceEquals(o, ghFieldObj)
                        && ColumnIndexForLeft(o.Bounds.Left, columnStarts) == 0);
                    bool captionInLead = leadCols > 0 && firstDataColumnFree
                        && captionLeftTwips < firstColumnTwips;

                    // Which label belongs to which column, decided before the leftovers
                    // queue is built rather than inside the cell loop. A label placed in a
                    // cell is not a leftover, and the queue used to keep it anyway: the
                    // group header's labels were written into their columns and then
                    // written again underneath as an extra row, one column to the left.
                    var ghLabels = new Dictionary<int, TextObject>();
                    for (int ci = 1; ci < totalCols; ci++)
                    {
                        var label = ghSection.Objects.OfType<TextObject>()
                            .FirstOrDefault(lbl => !string.IsNullOrWhiteSpace(lbl.Text)
                                && !ReferenceEquals(lbl, ghTextObj)
                                && !ghLabels.ContainsValue(lbl)
                                && ColumnIndexForLeft(lbl.Bounds.Left, columnStarts) == ci);
                        if (label is not null) ghLabels[ci] = label;
                    }

                    // Everything in the group header the row's own cells do not write -
                    // subreports, images, and fields that are not a column's - gets placed
                    // into whatever cells would otherwise be empty.
                    var ghWritten = ghLabels.Values.Cast<ReportObject>()
                        .Concat(Enumerable.Range(1, Math.Max(0, totalCols - 1)).Select(HeaderColumnField).OfType<FieldObject>())
                        .Concat(ghTextObj is null && ghFieldObj is not null ? [ghFieldObj] : [])
                        .ToList();
                    var ghExtras = QueueGroupRowExtras(ghSection, ghTextObj, ghWritten);
                    // Written here, so not left over for the body to lift out of the table.
                    consumedExtras.AddRange(ghWritten);

                    // A summary field matched to a column this band writes itself.
                    FieldObject? HeaderColumnField(int ci)
                    {
                        var ghFo = ci < columns.Count
                            ? ghSection.Objects.OfType<FieldObject>().FirstOrDefault(f =>
                                string.Equals(NormalizeFieldName(f.FieldName), columns[ci], StringComparison.OrdinalIgnoreCase))
                            : null;
                        return ghFo is not null && dbFieldMap.ContainsKey(NormalizeFieldName(ghFo.FieldName)) ? ghFo : null;
                    }

                    // The cells the extras can fill: the columns after the caption that hold
                    // neither a summary field nor a label.
                    int ghFreeCells = Enumerable.Range(1, Math.Max(0, totalCols - 1))
                        .Count(ci => HeaderColumnField(ci) is null && !ghLabels.ContainsKey(ci));

                    // A summary field goes into the column of the detail field it summarises,
                    // whether or not Crystal draws it there. One drawn in another column -
                    // Top5USAunderlay's percentage, beside the group name where the details'
                    // Order Amount column is further right - is better placed free-form, where
                    // every object keeps its own position, as the group footer is.
                    bool ghFieldsOutOfPlace = Enumerable.Range(1, Math.Max(0, Math.Min(totalCols, columns.Count) - 1))
                        .Any(ci => HeaderColumnField(ci) is FieldObject f && ColumnIndexForLeft(f.Bounds.Left, columnStarts) != ci);

                    w.WriteStartElement("Header", RdlNs);
                    w.WriteElementString("RepeatOnNewPage", RdlNs, ghSection.RepeatGroupHeader ? "true" : "false");
                    w.WriteStartElement("TableRows", RdlNs);
                    if (ghExtras.Count > ghFreeCells || ghFieldsOutOfPlace || HoldsSeveralLines(ghSection)
                        || TakesUnderlay(ghSection))
                    {
                        WriteGroupBandFreeForm(w, ghSection, report, tableCols, tableLeftTwips, consumedExtras);
                    }
                    else
                    {
                        StartTableRow(w, TwipsToRdl(ghRowHeightTwips), ghSection);
                        WriteRowVisibility(w, ghSection, report);
                        w.WriteStartElement("TableCells", RdlNs);
                        var captionObj = (ReportObject?)ghTextObj ?? ghFieldObj;
                        if (captionInLead)
                        {
                            // One cell over the lead column and the first data column together.
                            var frame = captionObj is null
                                ? null
                                : CellFrameTwips(captionObj, tableLeftTwips);
                            WriteTableCell(w, ghCellValue, ghFormat ?? new ObjectFormat { Bold = true },
                                colSpan: leadCols + 1, frame: frame);
                        }
                        else
                        {
                            for (int si = 0; si < leadCols; si++)
                                WriteTableCell(w, string.Empty);
                            var frame = captionObj is null || columnStarts.Count == 0
                                ? null
                                : CellFrameTwips(captionObj, columnStarts[0]);
                            WriteTableCell(w, ghCellValue, ghFormat ?? new ObjectFormat { Bold = true },
                                frame: frame);
                        }
                        // Fill remaining columns from matching GroupHeader FieldObjects —
                        // Crystal often places group summaries (e.g. "Count of X") here.
                        for (int ci = 1; ci < totalCols; ci++)
                        {
                            if (HeaderColumnField(ci) is FieldObject ghFo)
                            {
                                string ghField = SanitizeName(NormalizeFieldName(ghFo.FieldName));
                                WriteTableCell(w, BuildSummaryExpression(ghFo.SummaryFunction, ghField, PercentageScope(ghFo, gi)), ghFo.Format,
                                    frame: BandCellFrame(ghFo, ci, columnStarts));
                                continue;
                            }
                            // A label belonging to this column - the other half of the caption
                            // problem above. Without this the labels the group header carries
                            // are dropped rather than merely misplaced.
                            if (ghLabels.TryGetValue(ci, out var ghLabel))
                            {
                                WriteTableCell(w, ResolveTextWithFieldRefs(ghLabel.Text, knownFieldsForGroups,
                                    groupNameMapForTable, report.ReportComments, report.ReportTitle,
                                    BuildParameterMap(report)), ghLabel.Format,
                                    frame: BandCellFrame(ghLabel, ci, columnStarts));
                            }
                            else if (!TryWriteQueuedObjectCell(w, ghExtras, report, consumedExtras))
                            {
                                WriteTableCell(w, string.Empty);
                            }
                        }
                        w.WriteEndElement(); // TableCells
                        w.WriteEndElement(); // TableRow
                        WriteQueuedExtrasRows(w, ghExtras, report, consumedExtras, totalCols, ghSection, leadCols);
                    }
                    // Crystal splits one group level across several sections (a header strip
                    // per subreport, say), but only one of them maps to this band's row.
                    // The rest used to reach the free-form Body path, where a database-bound
                    // image or field-bound subreport cannot resolve its Fields! reference —
                    // give them rows in the band that owns them instead.
                    foreach (var surplus in groupHeaders.Where(s =>
                                 s.GroupLevel == gi && !ReferenceEquals(s, ghSection)))
                        WriteSurplusGroupBand(w, surplus, report, consumedExtras, totalCols, leadCols,
                            tableCols, tableLeftTwips, columnStarts);
                    w.WriteEndElement(); // TableRows
                    w.WriteEndElement(); // Header
                }

                // Group footer row with aggregate expressions for known DB fields
                var gfSection = groupFooters.FirstOrDefault(s => s.GroupLevel == gi);
                if (gfSection is not null)
                {
                    // FooterCell is a local function, declared below and usable here.
                    var gfWritten = Enumerable.Range(0, columns.Count).Select(FooterCell)
                        .Where(c => c.Value.Length > 0 && c.Field is not null).Select(c => (ReportObject)c.Field!).ToList();
                    var gfExtras = QueueGroupRowExtras(gfSection, usedTextObject: null, gfWritten);
                    consumedExtras.AddRange(gfWritten);

                    (string Value, FieldObject? Field) FooterCell(int ci)
                    {
                        // Find the matching FieldObject from the group footer section (normalize @ prefix)
                        var fo = gfSection.Objects.OfType<FieldObject>()
                            .FirstOrDefault(f => string.Equals(NormalizeFieldName(f.FieldName), columns[ci], StringComparison.OrdinalIgnoreCase));
                        string foNorm = fo is not null ? NormalizeFieldName(fo.FieldName) : columns[ci];
                        if (fo is not null && dbFieldMap.ContainsKey(foNorm))
                            // Group footer fields are assumed summarized even without an explicit
                            // SummaryFunction (unrecognised tags default to Sum) — BuildSummaryExpression's
                            // "null = plain field" rule doesn't apply here, so only route through it for
                            // Percentage, which needs its two-part expression regardless.
                            return (fo.SummaryFunction == AggregateFunction.Percentage
                                ? BuildSummaryExpression(fo.SummaryFunction, SanitizeName(foNorm), PercentageScope(fo, gi))
                                : $"={RdlAggregateFunction(fo.SummaryFunction)}(Fields!{SanitizeName(foNorm)}.Value)", fo);
                        if (fo is not null && SpecialFieldExpression(fo.FieldName, report.ReportComments, report.ReportTitle) is string sfe)
                            return (sfe, fo);
                        // Fallback: a summary field the parser could not name (e.g. SumofXYZ) sitting in
                        // a numeric column is taken as that column's Sum. Only where such a field is in
                        // the column: a Sum in every numeric column the footer leaves empty printed
                        // totals Crystal does not, USA-Orders-RWB's 223,506.47 under each state.
                        if (dbFieldMap.TryGetValue(columns[ci], out var dbf) && IsNumericType(dbf.DataType)
                            && gfSection.Objects.OfType<FieldObject>().Any(f =>
                                ColumnIndexForLeft(f.Bounds.Left, columnStarts) == ci
                                && !knownFieldsForGroups.ContainsKey(NormalizeFieldName(f.FieldName))
                                && !f.FieldName.StartsWith("Group #", StringComparison.OrdinalIgnoreCase)
                                && SpecialFieldExpression(f.FieldName, report.ReportComments, report.ReportTitle) is null))
                            return ($"=Sum(Fields!{SanitizeName(columns[ci])}.Value)", fo);
                        return (string.Empty, fo);
                    }

                    // The cells the extras can fill: every column this footer puts nothing of
                    // its own in, and every column past the field columns.
                    int gfFreeCells = Enumerable.Range(0, columns.Count).Count(ci => FooterCell(ci).Value.Length == 0)
                                      + (totalCols - columns.Count);

                    // The extras fill free cells left to right, wherever they stood in Crystal. One
                    // that would land in another column, or that spans several, is better placed
                    // free-form, where every object keeps its own position: USA-Orders-RWB's
                    // %Amount, drawn across the Order Amount column, went into the first column.
                    var gfFreeColumns = Enumerable.Range(0, columns.Count).Where(ci => FooterCell(ci).Value.Length == 0).ToList();
                    bool gfExtrasOutOfPlace = gfExtras.Select((o, k) => (o, k)).Any(p =>
                        p.k >= gfFreeColumns.Count
                        || ColumnIndexForLeft(p.o.Bounds.Left, columnStarts) != gfFreeColumns[p.k]
                        || ColumnIndexForLeft(p.o.Bounds.Left + Math.Max(0, p.o.Bounds.Width - 1), columnStarts) != gfFreeColumns[p.k]);

                    w.WriteStartElement("Footer", RdlNs);
                    w.WriteStartElement("TableRows", RdlNs);
                    if (gfExtras.Count > gfFreeCells || gfExtrasOutOfPlace || HoldsSeveralLines(gfSection)
                        || TakesUnderlay(gfSection))
                    {
                        WriteGroupBandFreeForm(w, gfSection, report, tableCols, tableLeftTwips, consumedExtras);
                    }
                    else
                    {
                        int gfRowHeightTwips = gfSection.HeightTwips > 0 ? gfSection.HeightTwips : 240;
                        StartTableRow(w, TwipsToRdl(gfRowHeightTwips), gfSection);
                        WriteRowVisibility(w, gfSection, report);
                        w.WriteStartElement("TableCells", RdlNs);
                        for (int si = 0; si < leadCols; si++)
                            WriteTableCell(w, string.Empty);
                        for (int ci = 0; ci < columns.Count; ci++)
                        {
                            var (cellValue, fo) = FooterCell(ci);
                            if (cellValue.Length == 0 && TryWriteQueuedObjectCell(w, gfExtras, report, consumedExtras))
                                continue;
                            WriteTableCell(w, cellValue, fo?.Format,
                                frame: BandCellFrame(fo, ci, columnStarts));
                        }
                        for (int ci = columns.Count; ci < totalCols; ci++)
                        {
                            if (!TryWriteQueuedObjectCell(w, gfExtras, report, consumedExtras))
                                WriteTableCell(w, string.Empty);
                        }
                        w.WriteEndElement(); // TableCells
                        w.WriteEndElement(); // TableRow
                        WriteQueuedExtrasRows(w, gfExtras, report, consumedExtras, totalCols, gfSection, leadCols);
                    }
                    // Surplus footer sections at this level — see the header band above.
                    foreach (var surplus in groupFooters.Where(s =>
                                 s.GroupLevel == gi && !ReferenceEquals(s, gfSection)))
                        WriteSurplusGroupBand(w, surplus, report, consumedExtras, totalCols, leadCols,
                            tableCols, tableLeftTwips, columnStarts);
                    w.WriteEndElement(); // TableRows
                    w.WriteEndElement(); // Footer
                }

                w.WriteEndElement(); // TableGroup
            }
            w.WriteEndElement(); // TableGroups
        }

        // Detail row
        bool detailSuppressed = detailsSections.Any(s => s.Suppress);
        string? detailSuppressExpr = detailsSections
            .Select(s => TranspileSuppressFormula(s.SuppressFormula, report))
            .FirstOrDefault(e => e is not null);
        var (detailNewPageBefore, detailNewPageAfter) = DetailPageBreaks(report);

        w.WriteStartElement("Details", RdlNs);
        if (detailNewPageBefore || detailNewPageAfter)
        {
            // A page break per record: see DetailPageBreaks.
            w.WriteStartElement("Grouping", RdlNs);
            w.WriteAttributeString("Name", "CrystalDetailRecord");
            w.WriteStartElement("GroupExpressions", RdlNs);
            w.WriteElementString("GroupExpression", RdlNs, $"=Fields!{DetailRowFieldName}.Value");
            w.WriteEndElement(); // GroupExpressions
            if (detailNewPageBefore) w.WriteElementString("PageBreakAtStart", RdlNs, "true");
            if (detailNewPageAfter)  w.WriteElementString("PageBreakAtEnd",   RdlNs, "true");
            w.WriteEndElement(); // Grouping
        }
        WriteDetailSortExpressions(w, report.SortFields);
        w.WriteStartElement("TableRows", RdlNs);
        if (bandGrid is not null)
        {
            WriteDetailBandRows(w, bandGrid, report, dataSetFieldsByName, gridColumns, leadCols, tableCols,
                tableLeftTwips);
        }
        else
        {
            int detailRowHeightTwips = detailsSections.FirstOrDefault()?.HeightTwips ?? 0;
            StartTableRow(w, TwipsToRdl(detailRowHeightTwips > 0 ? detailRowHeightTwips : 240),
                detailsSections.FirstOrDefault());
            if (detailSuppressed || detailSuppressExpr is not null)
            {
                w.WriteStartElement("Visibility", RdlNs);
                // A suppress formula supersedes the static checkbox in Crystal — files
                // with a formula often still carry the static bit set. Crystal's
                // "formula true = suppressed" matches RDL Hidden semantics directly.
                w.WriteElementString("Hidden", RdlNs, detailSuppressExpr ?? "true");
                w.WriteEndElement();
            }
            w.WriteStartElement("TableCells", RdlNs);
            for (int si = 0; si < leadCols; si++)
                WriteTableCell(w, string.Empty);
            for (int ci = 0; ci < columns.Count; ci++)
            {
                var fo = detailFieldObjects.FirstOrDefault(f =>
                    string.Equals(NormalizeFieldName(f.FieldName), columns[ci], StringComparison.OrdinalIgnoreCase))
                    ?? (ci < detailFieldObjects.Count ? detailFieldObjects[ci] : null);
                string cellVal = dataSetFieldsByName.TryGetValue(columns[ci], out string? realColumnName)
                    ? $"=Fields!{SanitizeName(realColumnName)}.Value"
                    : string.Empty;  // running total / unrecognised field — no DataSet entry yet
                // Only where the column really is wider than the field: a fallback column width
                // is not a measurement of anything, and padding by a made-up difference would
                // move text that is currently in the right place.
                int padRight = fo is not null && ci < colWidths.Count && colWidths[ci] > fo.Bounds.Width
                    ? colWidths[ci] - fo.Bounds.Width
                    : 0;
                // The vertical half of the same idea, and the same guard. A detail band is a row
                // as tall as the whole section and its objects sit at their own Top inside it -
                // SalesByCustomer-Grouped draws its fields 15 twips down a 289-twip band - so a
                // plain cell flushes every one of them to the top of the row. The row height has
                // to be a real measurement for the remainder to mean anything: where the section
                // reports no height the 240-twip fallback is a guess, and padding against a guess
                // would move text that is currently in the right place.
                bool measurable = fo is not null && detailRowHeightTwips > 0;
                int padTop = measurable ? Math.Max(0, fo!.Bounds.Top) : 0;
                int padBottom = measurable
                    ? Math.Max(0, detailRowHeightTwips - padTop - fo!.Bounds.Height)
                    : 0;
                WriteTableCell(w, cellVal, fo?.Format,
                    inset: new CellInset(0, padRight, padTop, padBottom), boxHeightTwips: fo?.Bounds.Height);
            }
            foreach (var img in detailImageObjects)
                WriteImageTableCell(w, img);
            w.WriteEndElement(); // TableCells
            w.WriteEndElement(); // TableRow
        }
        w.WriteEndElement(); // TableRows
        w.WriteEndElement(); // Details

        WriteTableReportFooter(w, report, tableCols, fieldBoundPageFooters ?? new List<Section>(),
            tableLeftTwips);

        w.WriteEndElement(); // Table
    }

    /// <summary>
    /// The layout of a report with several Details sections: the bands that print, in
    /// order, and the column grid their objects sit on. <see cref="Starts"/> is every
    /// distinct Left among the grid bands' objects, so each object begins exactly on a
    /// column edge and runs, by ColSpan, to where the next object in its own band begins.
    /// <see cref="Names"/> gives each column the field that starts there, for the group
    /// bands, which match their summaries to detail columns by name.
    /// </summary>
    private sealed record DetailBandGrid(List<int> Starts, List<int> Widths, List<string> Names,
        List<Section> Bands, List<Section> FreeFormBands);

    /// <summary>
    /// Crystal prints each Details section as its own line under every record: Details a,
    /// then Details b, and so on, each at its own height and each suppressed on its own
    /// terms. This converter used to flatten them into one table row with a column per
    /// object across every band, so boyum__Documents' four bands and ten objects came out
    /// as ten columns of one line where Crystal prints four lines, and a band suppressed
    /// outright hid the whole row, because the row took its visibility from whichever band
    /// said so first.
    ///
    /// Now each band is a row. The columns are the union of every band's Lefts: on
    /// boyum__ServiceContract the item band's seven fields and the item-group band's four
    /// share three Lefts and make seven columns between them, and the item-group band's
    /// End Date spans two of them, from where it starts to where its own next field does.
    /// A band the grid cannot express - two objects sharing a Left, or anything that is
    /// not a field (a text, a line, a subreport, an image) - is written free-form instead,
    /// one cell across the table holding each object at its own position, which is how
    /// the header and footer bands of this table are written.
    ///
    /// A band suppressed outright, with no formula to say otherwise, is left out: Crystal
    /// neither draws it nor spaces it, and its objects would only add columns nothing
    /// prints in. So is a band with no height and nothing in it.
    ///
    /// Null for a report with one Details section, which keeps the single-row layout.
    /// </summary>
    private static DetailBandGrid? BuildDetailBandGrid(List<Section> detailsSections)
    {
        // One band that columns can hold keeps the single-row layout. One they cannot - a
        // card of labels and values stacked down the band, a coloured box behind a record's
        // fields - is written free-form like any other such band: the single-row layout kept
        // only its fields, one column each, so worrall__CountriesAndCapitolCitiesOfTheWorld's
        // labels and box were dropped and its stacked values strung out across the page.
        if (detailsSections.Count < 2
            && (detailsSections.Count == 0 || FitsDetailBandGrid(detailsSections[0])
                || !HasRenderableContent(detailsSections[0])))
            return null;

        var bands = detailsSections
            .Where(s => !(s.Suppress && s.SuppressFormula is null))
            .Where(s => s.Objects.Count > 0 || s.HeightTwips > 0)
            .ToList();
        var freeForm = bands.Where(b => !FitsDetailBandGrid(b)).ToList();
        var gridObjects = bands.Where(b => !freeForm.Contains(b))
            .SelectMany(b => b.Objects.OfType<FieldObject>())
            .ToList();

        var starts = gridObjects.Select(o => o.Bounds.Left).Distinct().Order().ToList();
        var widths = new List<int>(starts.Count);
        for (int ci = 0; ci < starts.Count; ci++)
        {
            // As in the single-band table, a column runs to the next one's start; the last
            // runs to the right edge of whatever reaches furthest.
            int right = ci + 1 < starts.Count
                ? starts[ci + 1]
                : gridObjects.Max(o => o.Bounds.Left + o.Bounds.Width);
            widths.Add(right > starts[ci] ? right - starts[ci] : 1440);
        }
        var names = starts.Select(left => gridObjects
                .Where(o => o.Bounds.Left == left)
                .Select(o => NormalizeFieldName(o.FieldName))
                .FirstOrDefault() ?? string.Empty)
            .ToList();

        return new DetailBandGrid(starts, widths, names, bands, freeForm);
    }

    // A band the grid can hold: fields only, no two starting at the same place, and none
    // carrying a suppression of its own, which only a positioned item can express.
    private static bool FitsDetailBandGrid(Section band) =>
        band.Objects.All(o => o is FieldObject { SuppressOverride: null })
        && band.Objects.Select(o => o.Bounds.Left).Distinct().Count() == band.Objects.Count;

    /// <summary>
    /// The Details rows of a report with several Details sections, one per band: see
    /// <see cref="BuildDetailBandGrid"/>. Each object's cell is inset to the object's own
    /// box within the span it was given, horizontally and vertically, exactly as the
    /// single-band row is.
    /// </summary>
    private void WriteDetailBandRows(XmlWriter w, DetailBandGrid grid, ReportDefinition report,
        Dictionary<string, string> dataSetFieldsByName, bool gridColumns, int leadCols, int tableCols,
        int tableLeftTwips)
    {
        if (grid.Bands.Count == 0)
        {
            // Every band is suppressed, and RDL's Details still needs a row.
            StartTableRow(w, TwipsToRdl(1), null);
            w.WriteStartElement("Visibility", RdlNs);
            w.WriteElementString("Hidden", RdlNs, "true");
            w.WriteEndElement();
            w.WriteStartElement("TableCells", RdlNs);
            WriteTableCell(w, string.Empty, colSpan: tableCols);
            w.WriteEndElement(); // TableCells
            w.WriteEndElement(); // TableRow
            return;
        }

        var knownFields = BuildKnownFieldsMap(report);
        var parameterMap = BuildParameterMap(report);
        var groupNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int gi = 0; gi < report.Groups.Count; gi++)
            groupNames[$"Group #{gi + 1} Name"] = GroupNameExpression(report.Groups[gi]);

        int gridRight = grid.Starts.Count > 0 ? grid.Starts[^1] + grid.Widths[^1] : 0;

        foreach (var band in grid.Bands)
        {
            int bottom = band.Objects.Select(o => o.Bounds.Top + o.Bounds.Height).DefaultIfEmpty(0).Max();
            int height = Math.Max(band.HeightTwips, bottom);

            // Free-form needs something to put in its Rectangle: an empty one is fatal to
            // the engine. A band with nothing to show is a blank row of its own height.
            if (HasRenderableContent(band) && (!gridColumns || grid.FreeFormBands.Contains(band)))
            {
                WriteTableFreeFormRow(w, band, report, tableCols, tableLeftTwips);
                continue;
            }

            StartTableRow(w, TwipsToRdl(height > 0 ? height : 240), band);
            WriteRowVisibility(w, band, report);
            w.WriteStartElement("TableCells", RdlNs);
            for (int si = 0; si < leadCols; si++)
                WriteTableCell(w, string.Empty);

            var fields = gridColumns && !grid.FreeFormBands.Contains(band)
                ? band.Objects.OfType<FieldObject>().OrderBy(o => o.Bounds.Left).ToList()
                : [];
            int col = 0;
            for (int k = 0; k < fields.Count; k++)
            {
                var fo = fields[k];
                int start = grid.Starts.IndexOf(fo.Bounds.Left);
                if (start > col)
                    WriteTableCell(w, string.Empty, colSpan: start - col);
                int end = k + 1 < fields.Count ? grid.Starts.IndexOf(fields[k + 1].Bounds.Left) : grid.Starts.Count;
                int spanWidth = (end < grid.Starts.Count ? grid.Starts[end] : gridRight) - grid.Starts[start];

                string norm = NormalizeFieldName(fo.FieldName);
                string value = dataSetFieldsByName.TryGetValue(norm, out string? realColumnName)
                    ? $"=Fields!{SanitizeName(realColumnName)}.Value"
                    : PlacedFieldValue(fo, report, knownFields, groupNames, parameterMap);
                // A name nothing resolves prints blank in a detail cell, as it does in the
                // single-band row, rather than as its own bracketed name on every record.
                if (value.StartsWith('[')) value = string.Empty;

                int padRight = spanWidth > fo.Bounds.Width ? spanWidth - fo.Bounds.Width : 0;
                int padTop = height > 0 ? Math.Max(0, fo.Bounds.Top) : 0;
                int padBottom = height > 0 ? Math.Max(0, height - padTop - fo.Bounds.Height) : 0;
                WriteTableCell(w, value, fo.Format, colSpan: end - start,
                    inset: new CellInset(0, padRight, padTop, padBottom), boxHeightTwips: fo.Bounds.Height);
                col = end;
            }
            int dataCols = tableCols - leadCols;
            if (col < dataCols)
                WriteTableCell(w, string.Empty, colSpan: dataCols - col);
            w.WriteEndElement(); // TableCells
            w.WriteEndElement(); // TableRow
        }
    }

    // Some Crystal "document card" templates (SAP Business One invoices, transfers, ...)
    // leave Details entirely empty — the whole per-record display lives in Page
    // Header/Footer FieldObjects instead (see tableHeaderSections/fieldBoundPageFooters
    // in WriteBody). There's no real Details Table to route that content into in that
    // case, so this builds the smallest one that can host it: a single full-width
    // column, a Header holding the field-bound Page Header content (RepeatOnNewPage,
    // matching Crystal's own "prints every page" semantics), and one blank Details row —
    // RDL requires at least one ("For TableRows at least one TableRow is required" — a
    // hard rule even though Crystal's own Details has nothing in it either).
    private void WriteHeaderOnlyTable(XmlWriter w, ReportDefinition report,
        List<Section> tableHeaderSections, List<Section> fieldBoundPageFooters, int topOffsetTwips,
        string tableName = "Table1", bool includeReportFooter = true,
        List<Section>? groupBands = null, List<Section>? detailsSections = null)
    {
        groupBands ??= [];
        int width = report.Page.WidthTwips - report.Page.LeftMarginTwips - report.Page.RightMarginTwips;
        if (width <= 0) width = 1440;

        w.WriteStartElement("Table", RdlNs);
        w.WriteAttributeString("Name", tableName);
        if (topOffsetTwips > 0)
            w.WriteElementString("Top", RdlNs, TwipsToRdl(topOffsetTwips));
        w.WriteElementString("DataSetName", RdlNs, "DataSet1");
        w.WriteElementString("Width", RdlNs, TwipsToRdl(width));

        w.WriteStartElement("TableColumns", RdlNs);
        w.WriteStartElement("TableColumn", RdlNs);
        w.WriteElementString("Width", RdlNs, TwipsToRdl(width));
        w.WriteEndElement();
        w.WriteEndElement();

        // Header is optional, and "at least one TableRow" binds here just as it does for
        // Details below — this table is also reached when only *footer* content needs a
        // host, leaving nothing to put in the header band.
        if (tableHeaderSections.Count > 0)
        {
            w.WriteStartElement("Header", RdlNs);
            w.WriteElementString("RepeatOnNewPage", RdlNs, "true");
            w.WriteStartElement("TableRows", RdlNs);
            foreach (var section in tableHeaderSections)
                WriteTableFreeFormRow(w, section, report, tableCols: 1);
            w.WriteEndElement(); // TableRows
            w.WriteEndElement(); // Header
        }

        if (groupBands.Count > 0)
            WriteBandGroups(w, report, groupBands);

        // A grouped report's empty Details still takes its height for every record, as
        // Crystal lays it out: parking__orders' month headers stand apart by that blank space,
        // one Details height per order. Without groups the table exists only to give its
        // header band a data scope, and the row stays hidden (see below).
        int detailsSpaceTwips = groupBands.Count > 0
            ? (detailsSections ?? []).Where(s => !s.Suppress && s.SuppressFormula is null).Sum(s => s.HeightTwips)
            : 0;

        w.WriteStartElement("Details", RdlNs);
        w.WriteStartElement("TableRows", RdlNs);
        StartTableRow(w, detailsSpaceTwips > 0 ? TwipsToRdl(detailsSpaceTwips) : "1pt", null);
        // Hidden, because a Details row is emitted once per row of the DataSet. This table
        // exists only to give its Header band a data scope, so on a report with a couple of
        // thousand rows the placeholder would otherwise lay down a couple of thousand blank
        // rows and push everything below it down the page.
        if (detailsSpaceTwips == 0)
        {
            w.WriteStartElement("Visibility", RdlNs);
            w.WriteElementString("Hidden", RdlNs, "true");
            w.WriteEndElement(); // Visibility
        }
        w.WriteStartElement("TableCells", RdlNs);
        WriteTableCell(w, string.Empty);
        w.WriteEndElement(); // TableCells
        w.WriteEndElement(); // TableRow
        w.WriteEndElement(); // TableRows
        w.WriteEndElement(); // Details

        if (includeReportFooter)
            WriteTableReportFooter(w, report, tableCols: 1, fieldBoundPageFooters);

        w.WriteEndElement(); // Table
    }

    /// <summary>
    /// With no record to print, Crystal still makes one pass over the sections between the
    /// page header and the report footer: each group's headers, the Details and the group
    /// footers print once, their fields blank and their summaries empty. A collection letter
    /// laid out in group sections prints its text and an empty address, where a table with
    /// no rows prints none of its group or detail rows. So each table gets those rows again
    /// at the top of its footer, in Crystal's order, shown only when its dataset has no rows.
    /// An ungrouped report makes the same pass over its Details, and so does a subreport
    /// given no rows: the Crystal runtime prints its group sections and Details once between
    /// its report header and footer.
    /// </summary>
    private string AddNoRowsPass(string rdl)
    {
        System.Xml.Linq.XNamespace ns = RdlNs;
        var doc = System.Xml.Linq.XDocument.Parse(rdl, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        var items = doc.Root?.Element(ns + "Body")?.Element(ns + "ReportItems");
        if (items is null) return rdl;

        IEnumerable<System.Xml.Linq.XElement> RowsOf(System.Xml.Linq.XElement? band) =>
            band?.Element(ns + "TableRows")?.Elements(ns + "TableRow") ?? [];
        static bool AlwaysHidden(System.Xml.Linq.XElement row, System.Xml.Linq.XNamespace ns) =>
            row.Element(ns + "Visibility")?.Element(ns + "Hidden")?.Value.Trim() is "true";

        bool changed = false;
        foreach (var table in items.Elements(ns + "Table"))
        {
            var groups = table.Element(ns + "TableGroups")?.Elements(ns + "TableGroup").ToList() ?? [];
            var rows = groups.SelectMany(g => RowsOf(g.Element(ns + "Header")))
                .Concat(RowsOf(table.Element(ns + "Details")))
                .Concat(Enumerable.Reverse(groups).SelectMany(g => RowsOf(g.Element(ns + "Footer"))))
                .Where(r => !AlwaysHidden(r, ns))
                .Select(r => new System.Xml.Linq.XElement(r))
                .ToList();
            if (rows.Count == 0) continue;

            // An item's name must be unique in the report, so each copied one is renamed, and
            // a reference to it among these rows with it. A subreport's parameter names the
            // subreport's own parameter and keeps its name.
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in rows.SelectMany(r => r.Descendants()))
                if (e.Attribute("Name") is { } name && e.Name.LocalName != "Parameter")
                    name.Value = renamed[name.Value] = name.Value + "_NoRows";
            // A group's scope does not reach the table's footer, and over no rows a summary
            // has nothing to print.
            var scopes = groups.Select(g => g.Element(ns + "Grouping")?.Attribute("Name")?.Value)
                .OfType<string>().Select(n => $"\"{n}\"").ToList();
            foreach (var text in rows.SelectMany(r => r.DescendantNodes()).OfType<System.Xml.Linq.XText>().ToList())
            {
                if (!text.Value.StartsWith('=')) continue;
                // Over no record a formula that reads the data prints nothing, rather than what
                // it makes of empty fields ("UNKNOWN TYPE"), and a colour formula on the record
                // number colours nothing: the Crystal runtime prints a pick list's Details band
                // with no rows blank and white. A formula of parameters and constants prints.
                if (text.Parent?.Parent?.Name.LocalName != "Visibility")
                    text.Value = FieldReference.Replace(text.Value, m =>
                        IsDataFreeFormula(m.Groups[1].Value, []) ? m.Value : "Nothing");
                if (text.Parent?.Ancestors(ns + "Style").Any() == true && ContainsAggregateCall(text.Value))
                {
                    text.Parent.Remove();
                    continue;
                }
                if (text.Parent?.Name.LocalName == "Value"
                    && (ContainsAggregateCall(text.Value) || scopes.Any(s => text.Value.Contains(s, StringComparison.Ordinal))))
                {
                    text.Value = "";
                    continue;
                }
                if (scopes.Any(s => text.Value.Contains(s, StringComparison.Ordinal)))
                {
                    text.Parent?.Remove();
                    continue;
                }
                text.Value = NoRowsReference.Replace(text.Value, m =>
                    renamed.TryGetValue(m.Groups[2].Value, out var to) ? m.Groups[1].Value + to + m.Groups[3].Value : m.Value);
            }

            string dataSet = table.Element(ns + "DataSetName")?.Value ?? "DataSet1";
            foreach (var row in rows)
            {
                var visibility = row.Element(ns + "Visibility");
                string? hidden = EitherHidden(visibility?.Element(ns + "Hidden")?.Value.Trim(),
                    $"=CountRows(\"{dataSet}\") > 0");
                visibility?.Remove();
                row.Element(ns + "Height")!.AddAfterSelf(
                    new System.Xml.Linq.XElement(ns + "Visibility", new System.Xml.Linq.XElement(ns + "Hidden", hidden)));
            }

            // The engine counts a footer's hidden rows at their height when it decides whether
            // the footer fits on the page, unless a row of it can shrink; then each row counts
            // as tall as it prints. Without this always-hidden row whose textbox can shrink,
            // a report with rows had its last group pushed onto a page of its own to make
            // room for rows that do not print.
            int columns = table.Element(ns + "TableColumns")?.Elements(ns + "TableColumn").Count() ?? 1;
            var cell = new System.Xml.Linq.XElement(ns + "TableCell");
            if (columns > 1)
                cell.Add(new System.Xml.Linq.XElement(ns + "ColSpan", columns));
            cell.Add(new System.Xml.Linq.XElement(ns + "ReportItems",
                new System.Xml.Linq.XElement(ns + "Textbox", new System.Xml.Linq.XAttribute("Name", $"Textbox_{++_textboxCounter}_NoRows"),
                    new System.Xml.Linq.XElement(ns + "Value", ""),
                    new System.Xml.Linq.XElement(ns + "CanShrink", "true"))));
            rows.Add(new System.Xml.Linq.XElement(ns + "TableRow",
                new System.Xml.Linq.XElement(ns + "Height", "1pt"),
                new System.Xml.Linq.XElement(ns + "Visibility", new System.Xml.Linq.XElement(ns + "Hidden", "true")),
                new System.Xml.Linq.XElement(ns + "TableCells", cell)));

            var footer = table.Element(ns + "Footer");
            if (footer is null)
                table.Add(footer = new System.Xml.Linq.XElement(ns + "Footer"));
            var footerRows = footer.Element(ns + "TableRows");
            if (footerRows is null)
                footer.AddFirst(footerRows = new System.Xml.Linq.XElement(ns + "TableRows"));
            footerRows.AddFirst(rows);
            changed = true;
        }
        return changed
            ? doc.Declaration + doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)
            : rdl;
    }

    // A name an expression refers to: a scope in quotes, or a report item.
    private static readonly System.Text.RegularExpressions.Regex NoRowsReference =
        new(@"(""|ReportItems!)([A-Za-z_][A-Za-z0-9_]*)(""|\b)");

    // Report headers that need a data scope, each run of adjacent ones as a full-width table
    // of its own where that run starts, below the report headers above it, and carrying no
    // report footer - the details or header-only table owns that. ConsolidatedBalanceSheet's
    // title block (a print date beside it) is the first of four report headers; written
    // after all four, it printed below the liabilities instead of at the top of the page.
    private void WriteRoutedReportHeaders(XmlWriter w, ReportDefinition report, List<Section> routed)
    {
        var headers = report.Sections.Where(s => s.Type == SectionType.ReportHeader).ToList();
        int run = 0;
        for (int i = 0; i < headers.Count; i++)
        {
            if (!routed.Contains(headers[i])) continue;
            int start = i;
            while (i + 1 < headers.Count && routed.Contains(headers[i + 1])) i++;
            run++;
            WriteHeaderOnlyTable(w, report, headers.GetRange(start, i - start + 1), [],
                topOffsetTwips: ReportHeaderSpaceTwips(headers.Take(start)),
                tableName: run == 1 ? "TableReportHeader" : $"TableReportHeader{run}",
                includeReportFooter: false);
        }
    }

    // A group level's New Page Before and After, on the Grouping being written: the details
    // table's groups and the band groups alike.
    private static void WriteGroupPageBreaks(XmlWriter w, ReportDefinition report, int gi)
    {
        // A page-break formula supersedes the static checkbox (same precedence as
        // section suppression) and gates the break via PageBreakCondition —
        // PageBreakAtStart/AtEnd choose *where*, PageBreakCondition gates *whether*.
        // RDL's Grouping has only one PageBreakCondition shared by both directions;
        // when Crystal attaches formulas to both NewPageBefore and NewPageAfter,
        // the before-formula wins (rare — most reports use at most one direction).
        // Any section of the level can carry the break, not only its first: a statement's
        // footer is several sections, and New Page After sits on the last of them, or on
        // the area that holds them all (see RptParser.ParseArea).
        var ghBreaks = report.Sections.FirstOrDefault(s => s.Type == SectionType.GroupHeader && s.GroupLevel == gi
            && (s.NewPageBefore || TranspileNewPageBeforeFormula(s.NewPageBeforeFormula, report) is not null));
        var gfBreaks = report.Sections.FirstOrDefault(s => s.Type == SectionType.GroupFooter && s.GroupLevel == gi
            && (s.NewPageAfter || TranspileNewPageAfterFormula(s.NewPageAfterFormula, report) is not null));
        string? npbExpr = ghBreaks is not null ? TranspileNewPageBeforeFormula(ghBreaks.NewPageBeforeFormula, report) : null;
        string? npaExpr = gfBreaks is not null ? TranspileNewPageAfterFormula(gfBreaks.NewPageAfterFormula, report) : null;
        bool breakBefore = npbExpr is not null || ghBreaks?.NewPageBefore == true;
        bool breakAfter = npaExpr is not null || gfBreaks?.NewPageAfter == true;
        // New Page After on a group header that prints nothing is a new page between
        // groups: Crystal starts no page from an empty one, so the first group stays
        // on page 1 and each later one starts a page. A cheque register sets it, as
        // "Not OnLastRecord", on a suppressed header, and printed every cheque on a
        // page of its own where ours ran them on together.
        if (!breakBefore && !breakAfter && BreaksAfterAnEmptyHeader(report, gi))
            breakAfter = true;
        // Crystal starts no new page before the first group: New Page Before means a
        // new page between groups. The engine breaks before the first instance too,
        // which left page 1 of USA-Orders-Pct-colored holding only its headers. Between
        // groups is also "after every group but the last", which is what PageBreakAtEnd
        // means, so an unconditional break before is written as one after. A condition
        // the engine can use keeps the break where Crystal put it.
        if (breakBefore && (npbExpr is null || ContainsAggregateCall(npbExpr)))
            (breakBefore, breakAfter) = (false, true);
        if (breakBefore)
            w.WriteElementString("PageBreakAtStart", RdlNs, "true");
        if (breakAfter)
            w.WriteElementString("PageBreakAtEnd", RdlNs, "true");
        // PageBreakCondition is parsed in Grouping context, where the engine bans
        // aggregate functions outright — and Crystal's page-break formulas reach
        // for them constantly: "Not OnFirstRecord" ("break before each group except
        // the very first") transpiles to RowNumber(), and "break when the next row
        // starts a new customer" to Next(). Emit the static break without the
        // condition in that case: RDL's plain PageBreakAtStart is the same behavior
        // minus the conditional nuance, an acceptable approximation vs. a fatal
        // parse error that costs the whole report.
        if ((npbExpr ?? npaExpr) is string pageBreakExpr && !ContainsAggregateCall(pageBreakExpr))
            w.WriteElementString("PageBreakCondition", RdlNs, pageBreakExpr);
    }

    // Whether a group level's headers print nothing and one of them starts a new page after
    // it, by its checkbox or a formula.
    private static bool BreaksAfterAnEmptyHeader(ReportDefinition report, int level)
    {
        var headers = report.Sections.Where(s => s.Type == SectionType.GroupHeader && s.GroupLevel == level).ToList();
        return headers.Count > 0
            && headers.Any(s => s.NewPageAfter || !string.IsNullOrWhiteSpace(s.NewPageAfterFormula))
            && headers.All(s => AlwaysSuppressed(s) || s.HeightTwips == 0 || s.Objects.Count == 0);
    }

    // The report's groups as table groups of a one-column table, each group's header and
    // footer sections as its header and footer rows, written free-form across the band. For
    // a grouped report with no detail columns (see groupsWithoutTable in WriteBody).
    private void WriteBandGroups(XmlWriter w, ReportDefinition report, List<Section> groupBands)
    {
        w.WriteStartElement("TableGroups", RdlNs);
        for (int gi = 0; gi < report.Groups.Count; gi++)
        {
            var grp = report.Groups[gi];
            var headers = groupBands.Where(s => s.Type == SectionType.GroupHeader && s.GroupLevel == gi).ToList();
            var footers = groupBands.Where(s => s.Type == SectionType.GroupFooter && s.GroupLevel == gi).ToList();
            string groupExpr = DatePartGroupExpression(
                $"Fields!{SanitizeName(NormalizeFieldName(grp.FieldName))}.Value", grp.Condition);

            w.WriteStartElement("TableGroup", RdlNs);
            w.WriteStartElement("Grouping", RdlNs);
            w.WriteAttributeString("Name", $"Group{gi + 1}");
            w.WriteStartElement("GroupExpressions", RdlNs);
            w.WriteElementString("GroupExpression", RdlNs, groupExpr);
            w.WriteEndElement(); // GroupExpressions
            WriteGroupPageBreaks(w, report, gi);
            w.WriteEndElement(); // Grouping

            w.WriteStartElement("Sorting", RdlNs);
            if (grp.TopN is not null)
            {
                w.WriteStartElement("SortBy", RdlNs);
                w.WriteElementString("SortExpression", RdlNs, $"=Fields!{TopNRankField(grp.Level)}.Value");
                w.WriteEndElement(); // SortBy
            }
            w.WriteStartElement("SortBy", RdlNs);
            w.WriteElementString("SortExpression", RdlNs, groupExpr);
            if (grp.SortOrder == GroupSortOrder.Descending)
                w.WriteElementString("Direction", RdlNs, "Descending");
            w.WriteEndElement(); // SortBy
            w.WriteEndElement(); // Sorting

            if (headers.Count > 0)
            {
                w.WriteStartElement("Header", RdlNs);
                w.WriteStartElement("TableRows", RdlNs);
                foreach (var section in headers)
                    WriteBandRow(w, section, report);
                w.WriteEndElement(); // TableRows
                w.WriteElementString("RepeatOnNewPage", RdlNs, headers.Any(s => s.RepeatGroupHeader) ? "true" : "false");
                w.WriteEndElement(); // Header
            }
            if (footers.Count > 0)
            {
                w.WriteStartElement("Footer", RdlNs);
                w.WriteStartElement("TableRows", RdlNs);
                foreach (var section in footers)
                    WriteBandRow(w, section, report);
                w.WriteEndElement(); // TableRows
                w.WriteEndElement(); // Footer
            }
            w.WriteEndElement(); // TableGroup
        }
        w.WriteEndElement(); // TableGroups
    }

    // A band as one table row: its objects free-form, or, with nothing to draw, a blank row of
    // its height, since Crystal spaces by an empty band too and a Rectangle with nothing in it
    // is fatal to the engine. A band of no height, or suppressed outright, is hidden.
    private void WriteBandRow(XmlWriter w, Section section, ReportDefinition report)
    {
        if (HasRenderableContent(section))
        {
            WriteTableFreeFormRow(w, section, report, tableCols: 1);
            return;
        }
        string? hiddenExpr = section.Suppress || section.HeightTwips <= 0 ? "true"
            : TranspileSuppressFormula(section.SuppressFormula, report);
        StartTableRow(w, TwipsToRdl(Math.Max(section.HeightTwips, 20)), section);
        if (hiddenExpr is not null)
        {
            w.WriteStartElement("Visibility", RdlNs);
            w.WriteElementString("Hidden", RdlNs, hiddenExpr);
            w.WriteEndElement();
        }
        w.WriteStartElement("TableCells", RdlNs);
        WriteTableCell(w, string.Empty);
        w.WriteEndElement(); // TableCells
        w.WriteEndElement(); // TableRow
    }

    // Crystal's Report Footer prints once, after the very last detail row, however many
    // pages that turns out to be — the same "once, wherever the data ends" semantics as an
    // RDL Table's own top-level Footer (sibling to Header, not the per-TableGroup Footer
    // used for group summaries above). Content spans the full row via ColSpan since these
    // are normally free-form title/tagline-style objects, not one-value-per-column data.
    //
    // fieldBoundPageFooters piggybacks on this same band for the mirror-image reason
    // tableHeaderSections piggybacks on the Table's own Header (see WriteBody): a
    // PageFooter section with FieldObjects needs Fields! access RDL's own <PageFooter>
    // can never provide. Written before the once-only ReportFooter text, matching the
    // Header side's "repeating content first" ordering.
    private void WriteTableReportFooter(XmlWriter w, ReportDefinition report, int tableCols,
        List<Section> fieldBoundPageFooters, int leftOffsetTwips = 0)
    {
        // A ReportFooter with FieldObjects is already in fieldBoundPageFooters (handled by
        // the free-form-row loop below, which — unlike this plain TextObject-only join —
        // actually preserves field bindings) — skip it here so it isn't rendered twice.
        var section = report.Sections.FirstOrDefault(s =>
            s.Type == SectionType.ReportFooter && !fieldBoundPageFooters.Contains(s));
        string text = section is not null
            ? string.Join(" ", section.Objects.OfType<TextObject>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)))
            : "";
        var format = section?.Objects.OfType<TextObject>().FirstOrDefault()?.Format;
        if (text.Length == 0 && fieldBoundPageFooters.Count == 0) return;

        w.WriteStartElement("Footer", RdlNs);
        w.WriteStartElement("TableRows", RdlNs);

        foreach (var pfSection in fieldBoundPageFooters)
            WriteTableFreeFormRow(w, pfSection, report, tableCols, leftOffsetTwips);

        if (text.Length == 0)
        {
            w.WriteEndElement(); // TableRows
            w.WriteEndElement(); // Footer
            return;
        }

        string? hiddenExpr = TranspileSuppressFormula(section!.SuppressFormula, report)
                             ?? (section.Suppress ? "true" : null);

        StartTableRow(w, TwipsToRdl(section.HeightTwips > 0 ? section.HeightTwips : 240), section);
        if (hiddenExpr is not null)
        {
            w.WriteStartElement("Visibility", RdlNs);
            w.WriteElementString("Hidden", RdlNs, hiddenExpr);
            w.WriteEndElement();
        }
        w.WriteStartElement("TableCells", RdlNs);
        w.WriteStartElement("TableCell", RdlNs);
        if (tableCols > 1)
            w.WriteElementString("ColSpan", RdlNs, tableCols.ToString());
        w.WriteStartElement("ReportItems", RdlNs);
        w.WriteStartElement("Textbox", RdlNs);
        w.WriteAttributeString("Name", $"Textbox_{++_textboxCounter}");
        w.WriteElementString("Value", RdlNs, text);
        w.WriteElementString("CanGrow", RdlNs, "true");
        WriteObjectStyle(w, format);
        w.WriteEndElement(); // Textbox
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // TableCell
        w.WriteEndElement(); // TableCells
        w.WriteEndElement(); // TableRow
        w.WriteEndElement(); // TableRows
        w.WriteEndElement(); // Footer
    }

    // Renders a whole section's free-form content (labels + field values together, at
    // their original relative positions) as one full-width row inside the Table's own
    // Header — used for a Crystal PageHeader section whose FieldObjects need Fields!
    // access that RDL's own <PageHeader> can never provide (see the fieldBoundPageHeader
    // comment in WriteBody). Unlike WriteTableReportFooter, this can't collapse to a
    // single joined-text Textbox — the field bindings themselves are the point — so it
    // reuses WriteFreeFormObjects' existing Left/Top layout. A TableCell's own
    // <ReportItems> only permits exactly one child element ("Only one element in
    // ReportItems element is allowed within a TableCell" — a hard engine/schema rule,
    // unlike Body/PageHeader/PageFooter's ReportItems, which allow any number), so the
    // section's (typically many) items are wrapped in a single containing Rectangle.
    // A band of a table is not at the page's left edge: it starts where the table does,
    // which is where the table's first column starts. Its contents carry absolute page
    // positions, so without translating them by that offset every object in a header,
    // footer or group band comes out that much too far right - and anything that ran to
    // the page's edge now runs past the end of the table and is clipped.
    private void WriteTableFreeFormRow(XmlWriter w, Section section, ReportDefinition report, int tableCols,
        int leftOffsetTwips = 0)
    {
        if (_underlaid.Contains(section))
            return;
        string? hiddenExpr = TranspileSuppressFormula(section.SuppressFormula, report)
                             ?? (section.Suppress ? "true" : null);
        _underlayInto.TryGetValue(section, out var underlays);

        StartTableRow(w, TwipsToRdl(section.HeightTwips > 0 ? section.HeightTwips : 240), section);
        if (hiddenExpr is not null)
        {
            w.WriteStartElement("Visibility", RdlNs);
            w.WriteElementString("Hidden", RdlNs, hiddenExpr);
            w.WriteEndElement();
        }
        w.WriteStartElement("TableCells", RdlNs);
        w.WriteStartElement("TableCell", RdlNs);
        if (tableCols > 1)
            w.WriteElementString("ColSpan", RdlNs, tableCols.ToString());
        w.WriteStartElement("ReportItems", RdlNs);
        if (!HasRenderableContent(section) && underlays is null)
        {
            // A band of colour and nothing else: an empty Rectangle is fatal to the engine.
            w.WriteStartElement("Textbox", RdlNs);
            w.WriteAttributeString("Name", $"Textbox_{++_textboxCounter}");
            w.WriteElementString("Value", RdlNs, "");
            WriteBackgroundStyle(w, _bandBackColor);
            w.WriteEndElement(); // Textbox
            w.WriteEndElement(); // ReportItems
            w.WriteEndElement(); // TableCell
            w.WriteEndElement(); // TableCells
            w.WriteEndElement(); // TableRow
            return;
        }
        w.WriteStartElement("Rectangle", RdlNs);
        w.WriteAttributeString("Name", $"Rectangle_{++_textboxCounter}");
        WriteBackgroundStyle(w, _bandBackColor);
        // A report footer that starts a new page, and a report header that ends one (a cover
        // page), break the table at the band's row; a group's breaks are on its Grouping. A
        // report header's New Page Before and a report footer's New Page After are set by
        // default and change nothing.
        if (section.Type == SectionType.ReportFooter && section.NewPageBefore && section.NewPageBeforeFormula is null)
            w.WriteElementString("PageBreakAtStart", RdlNs, "true");
        if (section.Type == SectionType.ReportHeader && section.NewPageAfter && section.NewPageAfterFormula is null)
            w.WriteElementString("PageBreakAtEnd", RdlNs, "true");
        w.WriteStartElement("ReportItems", RdlNs);
        foreach (var (piece, shift) in underlays ?? [])
            WriteFreeFormObjects(w, piece, report, leftOffsetTwips, -shift);
        WriteFreeFormObjects(w, section, report, leftOffsetTwips);
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // Rectangle
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // TableCell
        w.WriteEndElement(); // TableCells
        w.WriteEndElement(); // TableRow
    }

    // Detail-row sort order, distinct from group-level sorting (GroupDefinition.SortOrder,
    // emitted per-TableGroup elsewhere) — this is Crystal's plain "sort the detail records
    // by this field" setting, which RdlConverter never read until runtime overrides needed it.
    private static void WriteDetailSortExpressions(XmlWriter w, List<SortField> sortFields)
    {
        if (sortFields.Count == 0) return;

        w.WriteStartElement("Sorting", RdlNs);
        foreach (var sf in sortFields)
        {
            w.WriteStartElement("SortBy", RdlNs);
            w.WriteElementString("SortExpression", RdlNs, $"=Fields!{SanitizeName(NormalizeFieldName(sf.FieldName))}.Value");
            if (sf.Direction == SortDirection.Descending)
                w.WriteElementString("Direction", RdlNs, "Descending");
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    // padRightTwips is the difference between the column's width and the width of the field
    // the report actually drew there. A Textbox Width inside a TableCell is ignored - the
    // textbox fills the cell - and this converter makes each column as wide as the gap to
    // the next column's start, which is wider than the field whenever Crystal left a space
    // between them. Right-aligned text therefore ran to the column's right edge, which is
    // the next column's left edge, and a number ended flush against the following word:
    // "158Bicicletas Buenos Aires", where Crystal has an eighth of an inch between them.
    // Padding the cell on the right by the difference puts the text back inside the field.
    //
    // frame is the other way of putting content somewhere inside a cell, and it is the
    // one a band needs. Padding moves the text and nothing else: a border and a background
    // belong to the cell, so they fill the whole column whatever the padding says. Crystal
    // draws both on the object, so a column label's underline is that label's own width -
    // 305px under "Order Amount" on SalesByCustomer-Grouped - where a padded cell draws one
    // continuous 1,098px rule across the band, and a group footer's grey subtotal box comes
    // out as wide as the column instead of as wide as the summary. Given a frame, the cell
    // holds a Rectangle with the Textbox positioned inside it at the object's own bounds,
    // which is how the report header and page footer bands of this same table are written;
    // the border and the background then have the object's rectangle to draw on.
    private void WriteTableCell(XmlWriter w, string value, ObjectFormat? format = null, bool isBold = false,
        int colSpan = 1, CellInset inset = default, ObjectBounds? frame = null, int? boxHeightTwips = null)
    {
        boxHeightTwips ??= frame?.Height;
        w.WriteStartElement("TableCell", RdlNs);
        if (colSpan > 1)
            w.WriteElementString("ColSpan", RdlNs, colSpan.ToString());
        w.WriteStartElement("ReportItems", RdlNs);
        if (frame is not null)
        {
            // The Rectangle fills the cell, so it carries the band's colour; the textbox
            // inside it, at the object's own bounds, carries only the object's.
            w.WriteStartElement("Rectangle", RdlNs);
            w.WriteAttributeString("Name", $"Rectangle_{++_textboxCounter}");
            WriteBackgroundStyle(w, _bandBackColor);
            w.WriteStartElement("ReportItems", RdlNs);
        }
        // A plain cell's textbox is the cell, so with no colour of its own it takes the band's.
        if (frame is null && _bandBackColor is not null && format?.BackColor is null)
            format = (format ?? new ObjectFormat()) with { BackColor = _bandBackColor };
        w.WriteStartElement("Textbox", RdlNs);
        w.WriteAttributeString("Name", $"Textbox_{++_textboxCounter}");
        // A positioned object carries its own border, and that border sits outside its
        // bounds - see BorderFrame. A plain cell has no box of its own to move.
        var (borderFrame, borderPadding) = frame is not null
            ? BorderFrame(format, frame)
            : (null!, default(CellInset));
        if (frame is not null)
            WriteObjectPosition(w, borderFrame);
        w.WriteStartElement("Value", RdlNs);
        w.WriteString(value);
        w.WriteEndElement();
        // Crystal's Can Grow is a per-object flag and it is off by default: a detail field
        // occupies the height the report drew it at. Writing "true" here regardless made
        // every row in every table grow to whatever line box this engine gives the font,
        // which discards the row height derived from the object's own bounds - and being a
        // row, the error is not a one-off but a pitch, compounding down the page. On the
        // three list reports in the visual suite our rows came out 53.5px apart where
        // Crystal's are 46px, drifting a third of an inch by the thirteenth row.
        //
        // The flag is now decoded - tag-252 data[9], see RptParser.ExtractObjectProps - so
        // this is no longer the constant it was written as. 96 objects across 49 of the 88
        // public reports set it, and no report in the visual suite has a flagged object
        // whose value is long enough to wrap, which is why none of those numbers move.
        //
        // Note that WriteTableReportFooter writes an unconditional "true" on its own
        // spanning cell and has never consulted this property. That predates the flag being
        // decoded and is left alone here: changing it would alter that band's row height on
        // every report that has one, which is a measurement this change cannot make.
        // A cell whose line limit fits in its height may not grow; see GrowsPastLineLimit.
        bool cellGrows = (format?.CanGrow ?? false)
            && !(LineLimitTwips(format) is int limit && boxHeightTwips is int cellHeight && limit <= cellHeight);
        w.WriteElementString("CanGrow", RdlNs, cellGrows ? "true" : "false");
        bool bold = format?.Bold ?? isBold;
        var effectiveFormat = format is not null
            ? format with { Bold = bold }
            : (bold ? new ObjectFormat { Bold = true } : null);
        WriteObjectStyle(w, effectiveFormat, value, frame is null ? inset : borderPadding, boxHeightTwips);
        w.WriteEndElement(); // Textbox
        if (frame is not null)
        {
            w.WriteEndElement(); // ReportItems
            w.WriteEndElement(); // Rectangle
        }
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // TableCell
    }

    // Objects placed in a group header/footer section that the row's own cells do not
    // write (alsoPlaced lists those that they do) and the tabular layout would otherwise
    // drop — filled into empty cells of the group row.
    private static Queue<ReportObject> QueueGroupRowExtras(Section section, TextObject? usedTextObject,
        IReadOnlyCollection<ReportObject>? alsoPlaced = null) =>
        new(section.Objects.Where(o => !(alsoPlaced?.Any(p => ReferenceEquals(p, o)) ?? false)).Where(o => o switch
        {
            SubreportObject sub => sub.Report is not null,
            ImageObject img => img.Source == ImageSourceKind.Database || img.ImageData is not null,
            TextObject t => !ReferenceEquals(t, usedTextObject) && (!string.IsNullOrWhiteSpace(t.Text) || DrawsWithoutText(t.Format)),
            ChartObject => true,
            // Any field the band's own cells do not write, which the caller passes in
            // alsoPlaced. Only a field matching a detail column has a cell of its own, so a
            // formula, or a database field the details do not show, was dropped here: a
            // group footer showing a formula printed nothing. (A Percentage-of-total field
            // was the first case found, sharing its column name with the summary it is a
            // percentage of, where the column takes only the first.)
            FieldObject => true,
            _ => false
        }));

    // An object with no text still draws its border, fill and shadow: a payroll report's rule
    // under every detail line is an empty text object 89 twips tall with a bottom border.
    private static bool DrawsWithoutText(ObjectFormat f) =>
        f.BackColor is not null || f.DropShadow
        || f.BorderLeft != 0 || f.BorderRight != 0 || f.BorderTop != 0 || f.BorderBottom != 0;

    /// <summary>
    /// Emits group-row objects that the band's own cells had no room for as extra rows in
    /// the same band, one cell per column until the queue drains. These have to stay
    /// inside the table: a database-bound image or a field-bound subreport carries Fields!
    /// references, which resolve only within a DataRegion, so the free-form Body path
    /// cannot host them at all — it produces "Field 'X' not found" for a column that is
    /// right there in the DataSet.
    /// </summary>
    // leadCols is padding, not placement: an object queued out of a band belongs over a
    // data column, never in the spacer that only exists so the band can reach left of them.
    private void WriteQueuedExtrasRows(XmlWriter w, Queue<ReportObject> extras, ReportDefinition report,
        List<ReportObject> consumedExtras, int totalCols, Section section, int leadCols = 0)
    {
        while (extras.Count > 0)
        {
            int before = extras.Count;
            StartTableRow(w, TwipsToRdl(section.HeightTwips > 0 ? section.HeightTwips : 240), section);
            WriteRowVisibility(w, section, report);
            w.WriteStartElement("TableCells", RdlNs);
            for (int si = 0; si < leadCols; si++)
                WriteTableCell(w, string.Empty);
            for (int ci = 0; ci < totalCols; ci++)
                if (!TryWriteQueuedObjectCell(w, extras, report, consumedExtras))
                    WriteTableCell(w, string.Empty);
            w.WriteEndElement(); // TableCells
            w.WriteEndElement(); // TableRow
            // TryWriteQueuedObjectCell can decline everything left in the queue (an object
            // kind it has no cell writer for); stop rather than emit blank rows forever.
            if (extras.Count == before) return;
        }
    }

    /// <summary>
    /// Writes a whole group band as one free-form row: every object at its own position,
    /// the row as tall as the section. Used when the band's objects do not fit its cells,
    /// because the overflow rows WriteQueuedExtrasRows writes are each as tall as the
    /// section - a section of 128 objects over a one-column table came out as 44 copies
    /// of its own height, 776 inches of rows for one group footer Crystal prints once.
    /// </summary>
    /// <summary>
    /// Whether a band's objects sit on more than one line: one starts at or below where the
    /// ones above it end. A row of cells holds one line, and a band laid out in several - a
    /// certificate's group header with the property, the requester, the owner and the
    /// address stacked down the page - came out as a single row, every object squeezed into a
    /// column of its own and its embedded fields left as "{table.column}". Lines and boxes
    /// are left out: a rule under a row of labels does not make it two lines.
    /// </summary>
    private static bool HoldsSeveralLines(Section section)
    {
        // Some object starting at or below where another ends. Taken as a run down the band,
        // one tall object - a client's address of six lines beside a column of four labels -
        // made the whole band one line, and it was laid out as a row of cells.
        var bounds = section.Objects.Where(o => o is not ShapeObject && o.Bounds.Height > 0)
            .Select(o => o.Bounds).ToList();
        if (bounds.Count < 2) return false;
        return bounds.Min(b => b.Top + b.Height) <= bounds.Max(b => b.Top);
    }

    /// <summary>
    /// Crystal's "Underlay Following Sections" on a group header or footer: the sections of
    /// the band after it print from its top, over it, so it takes no space of its own. A
    /// table's rows cannot overlap, so each of its objects is written into the row of the
    /// following section it starts in, moved up by where that section starts. A bill's
    /// footer drawn under the bill holds only its crests, and as a row of its own it filled
    /// a page with them and pushed the bill on to the next.
    ///
    /// Left as it is when a suppress formula decides whether it prints, when an object would
    /// go into a section that has one, since it would then print under that formula, and
    /// when an object does not fit in the section it starts in.
    /// </summary>
    private void PlaceGroupUnderlays(IEnumerable<Section> groupSections)
    {
        _underlayInto.Clear();
        _underlaid.Clear();
        foreach (var band in groupSections.GroupBy(s => (s.Type, s.GroupLevel)).Select(g => g.ToList()))
            for (int u = 0; u < band.Count; u++)
            {
                var under = band[u];
                if (!under.Underlay || under.Suppress || !string.IsNullOrWhiteSpace(under.SuppressFormula))
                    continue;
                var over = band.Skip(u + 1).TakeWhile(s => !s.Underlay).Where(s => !AlwaysSuppressed(s)).ToList();
                if (over.Count == 0)
                    continue;

                var pieces = new Dictionary<int, List<ReportObject>>();
                bool placeable = true;
                foreach (var obj in under.Objects)
                {
                    int k = 0, start = 0;
                    while (k < over.Count - 1 && obj.Bounds.Top >= start + over[k].HeightTwips)
                        start += over[k++].HeightTwips;
                    // A row grows to hold what is drawn in it, so an object running past the
                    // bottom of the row it starts in would push everything after it down.
                    placeable &= string.IsNullOrWhiteSpace(over[k].SuppressFormula)
                                 && obj.Bounds.Top + obj.Bounds.Height - start <= over[k].HeightTwips;
                    if (!pieces.TryGetValue(k, out var list))
                        pieces[k] = list = [];
                    list.Add(obj);
                }
                if (!placeable)
                    continue;
                foreach (var (k, objects) in pieces)
                {
                    int shift = over.Take(k).Sum(s => s.HeightTwips);
                    if (!_underlayInto.TryGetValue(over[k], out var into))
                        _underlayInto[over[k]] = into = [];
                    into.Add((new Section { Type = under.Type, GroupLevel = under.GroupLevel, Objects = objects }, shift));
                }
                _underlaid.Add(under);
            }
    }

    // Whether underlaid objects are to be drawn in this section's row, which only a
    // free-form row can hold, or it is an underlay whose objects were moved to those rows.
    private bool TakesUnderlay(Section section) =>
        _underlayInto.ContainsKey(section) || _underlaid.Contains(section);

    private void WriteGroupBandFreeForm(XmlWriter w, Section section, ReportDefinition report,
        int tableCols, int tableLeftTwips, List<ReportObject> consumedExtras)
    {
        WriteTableFreeFormRow(w, section, report, tableCols, tableLeftTwips);
        consumedExtras.AddRange(section.Objects);
    }

    /// <summary>
    /// A further section at a group level that already has its band row: its objects get
    /// rows of their own, one per column, or - when they need more than one such row -
    /// the whole section free-form, for the reason WriteGroupBandFreeForm gives. So is one
    /// whose objects stand on several lines, as the first section's are: a work order's
    /// second group header, labels over their values in two columns, came out as one row
    /// of seventeen cells.
    /// </summary>
    private void WriteSurplusGroupBand(XmlWriter w, Section surplus, ReportDefinition report,
        List<ReportObject> consumedExtras, int totalCols, int leadCols, int tableCols, int tableLeftTwips,
        List<int>? columnStarts = null)
    {
        // Free-form too when an object spans columns, as for the level's first section: a
        // certificate's paragraph of seven inches, put in the first cell, wrapped to two.
        var extras = QueueGroupRowExtras(surplus, usedTextObject: null);
        bool spansColumns = columnStarts is not null && extras.Any(o =>
            ColumnIndexForLeft(o.Bounds.Left, columnStarts)
            != ColumnIndexForLeft(o.Bounds.Left + Math.Max(0, o.Bounds.Width - 1), columnStarts));
        if (extras.Count > totalCols || HoldsSeveralLines(surplus) || spansColumns || TakesUnderlay(surplus))
            WriteGroupBandFreeForm(w, surplus, report, tableCols, tableLeftTwips, consumedExtras);
        else
            WriteQueuedExtrasRows(w, extras, report, consumedExtras, totalCols, surplus, leadCols);
    }

    private bool TryWriteQueuedObjectCell(XmlWriter w, Queue<ReportObject> extras, ReportDefinition report,
        List<ReportObject> consumedExtras)
    {
        if (extras.Count == 0) return false;
        var obj = extras.Dequeue();
        consumedExtras.Add(obj);
        switch (obj)
        {
            case SubreportObject sub:
                WriteSubreportTableCell(w, sub, report);
                return true;
            case ImageObject img:
                WriteImageTableCell(w, img);
                return true;
            case TextObject text:
                // Same leading-"=" hazard as in ResolveTextWithFieldRefs.
                WriteTableCell(w, text.Text.StartsWith('=') || text.Text.Contains('\n') ? $"={QuoteLiteral(text.Text)}" : text.Text,
                    text.Format);
                return true;
            case ChartObject chart:
                WriteChartTableCell(w, chart, report);
                return true;
            case FieldObject fo:
                var groupNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int gi = 0; gi < report.Groups.Count; gi++)
                    groupNames[$"Group #{gi + 1} Name"] = GroupNameExpression(report.Groups[gi]);
                WriteTableCell(w, PlacedFieldValue(fo, report, BuildKnownFieldsMap(report), groupNames, BuildParameterMap(report)), fo.Format);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// What a placed field object shows, resolved the way the free-form layout always has:
    /// a DataSet field by its declared name (summarized when the object summarizes), a
    /// group's name, a parameter, a special field, and otherwise the reference as literal
    /// text, so that a field the DataSet does not have prints its name rather than failing
    /// the report. A table cell holding a field that is not its column's resolves the same
    /// way.
    /// </summary>
    private string PlacedFieldValue(FieldObject field, ReportDefinition? report,
        Dictionary<string, string> knownFields, Dictionary<string, string> groupNameMap,
        Dictionary<string, string> parameterMap)
    {
        // Strip @/# prefix for Crystal formula/running-total field references
        string lookupName = NormalizeFieldName(field.FieldName);
        if (knownFields.TryGetValue(lookupName, out string? realFieldName))
            // Summary fields (grand totals in report header/footer) aggregate
            // over the whole DataSet scope; plain fields render the raw value.
            // Use the DataSet's own declared casing (realFieldName), not the
            // FieldObject's — this engine's Fields lookup is case-sensitive, and
            // Crystal's placed-object FieldName doesn't always match a DB column's
            // stored case exactly (e.g. FieldName "Personal" vs column "personal").
            return BuildSummaryExpression(field.SummaryFunction, SanitizeName(realFieldName),
                _freeFormGroupLevel is int level ? PercentageScope(field, level) : null);
        else if (groupNameMap.TryGetValue(field.FieldName, out string? groupFieldExpr))
            return "=" + groupFieldExpr;
        // Checked ahead of the special fields below: when a report declares a
        // parameter by this name, that declaration is direct evidence of what
        // the author meant, whereas the special-field list is a fallback for
        // names nothing else resolves. Reports naming a parameter "Page Number"
        // do mean their own parameter, not Globals!PageNumber.
        //
        // A parameter dropped directly as its own report object (rather than
        // referenced inside a formula) carries Crystal's "?@Name" field-code -
        // the leading "?" marks it as a parameter reference. lookupName (via
        // NormalizeFieldName) never strips it, so the plain knownFields/
        // groupNameMap lookups above correctly miss. For the parameter lookup
        // specifically, only the "?" needs stripping - confirmed via
        // --dumpfields that BuildParameterMap's own keys are the *declared*
        // ParameterField.Name verbatim, which for these parameters already
        // includes the "@" (e.g. Name="@Driver", not "Driver") - so normalizing
        // it away here would make the lookup miss the very key it needs to hit.
        else if (parameterMap.TryGetValue(field.FieldName.TrimStart('?'), out string? paramExpr))
            return "=" + paramExpr;
        // A special date in a format of its own is left a date, for the style's Format to print.
        else if (field.Format.FormatString is not null
                 && field.FieldName.ToLowerInvariant() is "print date" or "data date" or "modification date")
            return "=Globals!ExecutionTime";
        else if (SpecialFieldExpression(field.FieldName, report?.ReportComments ?? string.Empty, report?.ReportTitle ?? string.Empty) is string specialExpr)
            return specialExpr;
        else
            return $"[{field.FieldName}]";
    }

    // What an on-demand subreport prints in its place: its caption formula's value, or with
    // none its report's name, as Crystal prints it.
    private string OnDemandCaption(SubreportObject sub, ReportDefinition? report,
        Dictionary<string, string> knownFields, Dictionary<string, string> parameterMap,
        Dictionary<string, string>? groupNameMap = null)
    {
        if (sub.OnDemandCaptionFormula is { } formula)
            return ResolveTextWithFieldRefs("{" + formula + "}", knownFields,
                groupNameMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                report?.ReportComments ?? string.Empty, report?.ReportTitle ?? string.Empty, parameterMap);
        string name = sub.ReportName.Length > 0 ? sub.ReportName : sub.SubreportName;
        return name.StartsWith('=') ? $"={QuoteLiteral(name)}" : name;
    }

    private void WriteSubreportTableCell(XmlWriter w, SubreportObject sub, ReportDefinition report)
    {
        if (sub.OnDemand)
        {
            string caption = OnDemandCaption(sub, report, BuildKnownFieldsMap(report), BuildParameterMap(report));
            w.WriteStartElement("TableCell", RdlNs);
            w.WriteStartElement("ReportItems", RdlNs);
            w.WriteStartElement("Textbox", RdlNs);
            w.WriteAttributeString("Name", $"Textbox_{++_textboxCounter}");
            w.WriteElementString("Value", RdlNs, caption);
            w.WriteElementString("CanGrow", RdlNs, "false");
            WriteObjectStyle(w, sub.Format, caption);
            w.WriteEndElement(); // Textbox
            w.WriteEndElement(); // ReportItems
            w.WriteEndElement(); // TableCell
            return;
        }
        w.WriteStartElement("TableCell", RdlNs);
        w.WriteStartElement("ReportItems", RdlNs);
        w.WriteStartElement("Subreport", RdlNs);
        w.WriteAttributeString("Name", SanitizeName(sub.Name.Length > 0 ? sub.Name : $"subreport_{++_textboxCounter}"));
        w.WriteElementString("ReportName", RdlNs, SubreportRdlName(_subreportNamePrefix, sub.SubreportName));
        WriteSubreportParameters(w, sub, report);
        w.WriteEndElement(); // Subreport
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // TableCell
    }

    private void WriteImageTableCell(XmlWriter w, ImageObject image)
    {
        w.WriteStartElement("TableCell", RdlNs);
        w.WriteStartElement("ReportItems", RdlNs);
        w.WriteStartElement("Image", RdlNs);
        w.WriteAttributeString("Name", SanitizeName(image.Name.Length > 0 ? image.Name : $"image_{++_textboxCounter}"));
        WriteImageSourceElements(w, image);
        w.WriteEndElement(); // Image
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // TableCell
    }

    private void WriteImageSourceElements(XmlWriter w, ImageObject image)
    {
        if (image.Source == ImageSourceKind.Embedded)
        {
            w.WriteElementString("Source", RdlNs, "Embedded");
            w.WriteElementString("Value", RdlNs, EmbeddedImageName(image.EmbeddingIndex));
        }
        else
        {
            w.WriteElementString("Source", RdlNs, "Database");
            w.WriteElementString("Value", RdlNs, $"=Fields!{SanitizeName(NormalizeFieldName(image.FieldName))}.Value");
            // Blob content type is unknowable at convert time; bmp matches
            // Crystal's typical DIB storage and is easy to adjust manually.
            w.WriteElementString("MIMEType", RdlNs, "image/bmp");
        }
        w.WriteElementString("Sizing", RdlNs, "FitProportional");
    }

    // padRightTwips keeps a table cell's text inside the field the report drew, rather than
    // letting it run to the edge of a column that is wider - see WriteTableCell.

    private readonly record struct CellInset(int Left, int Right, int Top, int Bottom);

    /// <summary>
    /// How far outside an object's bounds Crystal puts the outer edge of its border: 2pt,
    /// on every side that has one. Measured at 300dpi on SalesByCustomer-Grouped, whose
    /// report title is framed on four sides and whose group total on four, and whose column
    /// labels are underlined: the title's frame runs x 50-2428, y 61-382 against bounds of
    /// 58.3-2420.8 and 69.4-375.0, the group total's x 942-1499 against 950-1491.7, and the
    /// underlines sit 6px below ours. A 1pt line, drawn inward from that edge.
    ///
    /// It is per edge, not per box. An underline moves down by the same 2pt but keeps its
    /// length - "Order Amount"'s spans x 1023-1327 against bounds of 1025-1325.8 - so only
    /// the sides that carry a border move out, and a full frame closes its corners at the
    /// enlarged rectangle.
    /// </summary>
    private const int BorderOutsetTwips = 40;

    /// <summary>
    /// The box a bordered object's textbox has to occupy for its border to land where
    /// Crystal draws it, and the padding that keeps its contents where they were.
    ///
    /// This engine draws a border line centred on the item's edge, so each bordered side is
    /// pushed out by the outset less half the line - 30 twips for a 1pt line - and padded by
    /// the same amount. A side is never pushed past the container's left or top edge, where
    /// RDL has no negative position to put it.
    /// </summary>
    private static (ObjectBounds Frame, CellInset Padding) BorderFrame(ObjectFormat? fmt, ObjectBounds b)
    {
        if (fmt is null) return (b, default);
        int line = fmt.BorderWidthTwips > 0 ? fmt.BorderWidthTwips : 20;
        int d = Math.Max(0, BorderOutsetTwips - line / 2);
        int left = fmt.BorderLeft != 0 ? Math.Min(d, Math.Max(0, b.Left)) : 0;
        int top = fmt.BorderTop != 0 ? Math.Min(d, Math.Max(0, b.Top)) : 0;
        int right = fmt.BorderRight != 0 ? d : 0;
        int bottom = fmt.BorderBottom != 0 ? d : 0;
        if (left + top + right + bottom == 0) return (b, default);
        return (new ObjectBounds(b.Left - left, b.Top - top, b.Width + left + right, b.Height + top + bottom),
            new CellInset(left, right, top, bottom));
    }

    /// <summary>
    /// The object's own box, expressed inside the cell that is going to hold it.
    ///
    /// A group band's objects do not define this table's columns - the detail band does -
    /// so an object here starts wherever the report drew it, which is generally somewhere
    /// inside the column that contains it rather than at that column's left edge. Crystal
    /// draws SalesByCustomer-Grouped's "Order Amount" label 1,122 twips into a column 3,522
    /// twips wide, and writing it as a plain cell put it at the column's start: three
    /// quarters of an inch left of where the real engine renders it, a clear label-width
    /// away from its own position. Vertically the same: a group band is one row as tall as
    /// the whole section, and its objects sit at their own Top inside it, so plain cells
    /// flushed every one of them to the top of the row.
    ///
    /// This was padding until it had to carry a border as well. Padding insets the text and
    /// leaves the frame alone, so an underline drawn on a label came out as wide as the
    /// column and a subtotal's background filled it - the object's box is what the border
    /// and the background need, and only a positioned item inside a Rectangle has one. The
    /// width is the object's, never the cell's: an object wider than its cell keeps its own
    /// width, which is what Crystal draws, and the Rectangle decides what that overflow
    /// looks like.
    ///
    /// Left clamps at zero. An object left of the cell it is placed in cannot be expressed
    /// inside it - RDL has no negative Left - and the cell's own edge is the nearest place
    /// there is, which is where such an object landed before any of this.
    /// </summary>
    private static ObjectBounds CellFrameTwips(ReportObject obj, int cellStartTwips) =>
        new(Math.Max(0, obj.Bounds.Left - cellStartTwips), obj.Bounds.Top,
            obj.Bounds.Width, obj.Bounds.Height);

    /// <summary>
    /// The frame for a group band's object, or none if it does not belong to this column.
    ///
    /// Cells in these bands are matched to columns by field name, not by position, and the
    /// two can disagree: a group footer's Sum of a field is placed in that field's column
    /// wherever the report happens to have drawn the summary. A frame measured from a
    /// column the object does not sit in is not a measurement of anything - it would push
    /// the object by an arbitrary distance, or clamp to zero and look like a coincidence -
    /// so the object has to be inside the column before its offset within it means
    /// something, and where it is not the cell stays the plain full-width one it was.
    /// </summary>
    private static ObjectBounds? BandCellFrame(ReportObject? obj, int columnIndex,
        List<int> columnStarts) =>
        obj is not null
        && columnIndex < columnStarts.Count
        && ColumnIndexForLeft(obj.Bounds.Left, columnStarts) == columnIndex
            ? CellFrameTwips(obj, columnStarts[columnIndex])
            : null;

    /// <summary>
    /// The inside of an RDL expression, or null when there is nothing to compare. A value
    /// is only usable here when it is an expression - "=Sum(Fields!X.Value)" becomes
    /// "Sum(Fields!X.Value)". Literal text and the "[Field]" placeholder emitted for a
    /// field nothing resolved are refused: comparing those would either fail at render time
    /// or silently never match.
    /// </summary>
    private static string? ComparableValue(string? value) =>
        value is not null && value.Length > 1 && value[0] == '='
            ? value[1..]
            : null;

    /// <summary>
    /// Crystal's highlighting rules as one nested IIf, with the object's own colour as the
    /// innermost else. The first rule is the outermost test because Crystal applies the
    /// first one that matches, and rules that do not set this particular colour are skipped
    /// rather than resolved to the default - a rule setting only a background must leave the
    /// font alone.
    /// </summary>
    private static string? ConditionalColor(IReadOnlyList<ConditionalFormat> rules,
        Func<ConditionalFormat, string?> pick, string? ownColor, string fallback, string test)
    {
        if (!rules.Any(r => pick(r) is not null)) return null;
        string expr = $"\"{ownColor ?? fallback}\"";
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (pick(rules[i]) is not string c) continue;
            string op = rules[i].Operator == ConditionalOperator.LessThan ? "<" : ">";
            string threshold = rules[i].Threshold.ToString("0.################",
                System.Globalization.CultureInfo.InvariantCulture);
            expr = $"IIf({test} {op} {threshold}, \"{c}\", {expr})";
        }
        return "=" + expr;
    }

    /// <summary>
    /// Whether a format string is a numeric picture, and so needs the culture that spells
    /// this report's separators.
    ///
    /// "," and "." in a .NET numeric format string are placeholders the culture fills in, so
    /// "#,##0.00" only renders "1.234,56" under a culture that says so. A date format from
    /// this converter never needs that: its separators are quoted literals ("MM'/'dd'/'yyyy")
    /// and a date that defers to the machine carries no format string at all. A numeric
    /// picture is the only thing here with an unquoted separator in it, and '0' or '#' is
    /// what distinguishes one - no date pattern this converter writes contains either.
    /// </summary>
    private static bool NeedsSeparatorCulture(string? format) =>
        format is not null && (format.Contains('0') || format.Contains('#'));

    // boxHeightTwips is the object's own height inside any padding, where it is known.
    private void WriteObjectStyle(XmlWriter w, ObjectFormat? fmt,
        string? valueExpr = null, CellInset inset = default, int? boxHeightTwips = null)
    {
        if (fmt is null && inset == default) return;
        // Highlighting rules compare the object's own value, so they can only be emitted
        // where that value is an expression this can test. A cell holding literal text has
        // nothing to compare and its rules are dropped.
        var rules = fmt?.Conditions ?? [];
        string? test = rules.Count > 0 ? ComparableValue(valueExpr) : null;
        string? condFore = test is null ? null
            : ConditionalColor(rules, r => r.FontColor, fmt?.ForeColor, "#000000", test);
        string? condBack = test is null ? null
            : ConditionalColor(rules, r => r.BackColor, fmt?.BackColor, "Transparent", test);
        bool hasBorders = fmt is not null &&
            (fmt.BorderLeft != 0 || fmt.BorderRight != 0 || fmt.BorderTop != 0 || fmt.BorderBottom != 0);
        bool needsCulture = _separatorCulture is not null
                         && NeedsSeparatorCulture(fmt?.FormatString);
        bool hasStyle = inset != default || needsCulture
                     || condFore is not null || condBack is not null
                     || (fmt is not null &&
                         (fmt.Bold || fmt.Italic || fmt.Underline
                          || fmt.FontName is not null || fmt.FontSize.HasValue
                          || fmt.ForeColor is not null
                          || fmt.BackColor is not null
                          || hasBorders
                          || fmt.FormatString is not null
                          || fmt.HAlign != HorizontalAlignment.Default));
        if (!hasStyle) return;

        w.WriteStartElement("Style", RdlNs);
        if (inset.Left > 0)
            w.WriteElementString("PaddingLeft", RdlNs, TwipsToRdl(inset.Left));
        if (inset.Right > 0)
            w.WriteElementString("PaddingRight", RdlNs, TwipsToRdl(inset.Right));
        // Crystal's baseline sits lower in its box than ours for some fonts - see
        // BaselineDropTwips. The drop rides on top of whatever inset the object already has.
        int topPadding = inset.Top + BaselineDropTwips(fmt);
        if (topPadding > 0)
            w.WriteElementString("PaddingTop", RdlNs, TwipsToRdl(topPadding));
        if (inset.Bottom > 0)
            w.WriteElementString("PaddingBottom", RdlNs, TwipsToRdl(inset.Bottom));
        if (fmt is null)
        {
            w.WriteEndElement(); // Style
            return;
        }
        // What a report draws as an "underline" beneath a column label or a box around a
        // title is border formatting on the object, not a line object. The engine draws a
        // border edge only where both its style and its width are set, so both are written.
        // The drop-shadow flag has no RDL expression and is not emitted.
        if (hasBorders)
        {
            w.WriteStartElement("BorderStyle", RdlNs);
            if (fmt.BorderLeft != 0) w.WriteElementString("Left", RdlNs, RdlBorderStyle(fmt.BorderLeft));
            if (fmt.BorderRight != 0) w.WriteElementString("Right", RdlNs, RdlBorderStyle(fmt.BorderRight));
            if (fmt.BorderTop != 0) w.WriteElementString("Top", RdlNs, RdlBorderStyle(fmt.BorderTop));
            if (fmt.BorderBottom != 0) w.WriteElementString("Bottom", RdlNs, RdlBorderStyle(fmt.BorderBottom));
            w.WriteEndElement();
            w.WriteStartElement("BorderWidth", RdlNs);
            w.WriteElementString("Default", RdlNs,
                $"{(fmt.BorderWidthTwips > 0 ? fmt.BorderWidthTwips : 20) / 20.0:0.##}pt");
            w.WriteEndElement();
        }
        // A highlighting rule supersedes the object's own colour, and carries it as the
        // expression's else branch, so only one of the two is ever written.
        if (condBack is not null)
            w.WriteElementString("BackgroundColor", RdlNs, condBack);
        else if (fmt.BackColor is not null)
            w.WriteElementString("BackgroundColor", RdlNs, fmt.BackColor);
        if (condFore is not null)
            w.WriteElementString("Color", RdlNs, condFore);
        else if (fmt.ForeColor is not null)
            w.WriteElementString("Color", RdlNs, fmt.ForeColor);
        if (fmt.FontName is not null)
            w.WriteElementString("FontFamily", RdlNs, fmt.FontName);
        if (fmt.FontSize.HasValue)
            w.WriteElementString("FontSize", RdlNs,
                $"{EmPointsFor(fmt.FontName, fmt.FontSize.Value, fmt.Bold, fmt.Italic):0.###}pt");
        // The pitch Crystal sets the lines at, which the engine otherwise takes to be the
        // font size; see GdiLinePitches.
        //
        // A growing box keeps its own height until its text needs a second line, as Crystal's
        // does, where the engine grows it to one line at the pitch: a ledger's 9pt fields of
        // 10.5pt lines grew every row 1.5pt. So a growing box shorter than a line at the pitch,
        // or of a height not known here, keeps the engine's own line height. The box's room is
        // what is left below the baseline drop, which pads its top: a property list's 11pt
        // address of 9.75pt lines, dropped 1.45pt, grew every row a point.
        if (fmt.FontSize.HasValue
            && GdiLinePitches.PitchPoints(fmt.FontName, fmt.FontSize.Value, fmt.Bold, fmt.Italic) is { } pitch
            && (!fmt.CanGrow || boxHeightTwips is int box
                && pitch * fmt.LineSpacing * 20 <= box - BaselineDropTwips(fmt) + 1))
            w.WriteElementString("LineHeight", RdlNs, $"{pitch * fmt.LineSpacing:0.###}pt");
        if (fmt.Bold)
            w.WriteElementString("FontWeight", RdlNs, "Bold");
        if (fmt.Italic)
            w.WriteElementString("FontStyle", RdlNs, "Italic");
        if (fmt.Underline)
            w.WriteElementString("TextDecoration", RdlNs, "Underline");
        if (fmt.FormatString is not null)
            w.WriteElementString("Format", RdlNs, fmt.FormatString);
        // The separators, on the one object that needs them and nowhere else. Style's
        // Language falls back to the report's, which falls back to the rendering machine,
        // so leaving it off everything else is what lets a date defer the way Crystal
        // defers it.
        if (needsCulture)
            w.WriteElementString("Language", RdlNs, _separatorCulture);
        // A time with AM or PM prints Crystal's designators, which are English (see
        // RptParser.BuildTimeFormat), where the machine's own may be "a.m." and "p.m.".
        else if (fmt.FormatString is { } timeFormat && System.Text.RegularExpressions.Regex.IsMatch(System.Text.RegularExpressions.Regex.Replace(timeFormat, "'[^']*'", ""), "tt"))
            w.WriteElementString("Language", RdlNs, "en-US");
        if (fmt.HAlign != HorizontalAlignment.Default)
            w.WriteElementString("TextAlign", RdlNs, RdlTextAlign(fmt.HAlign));
        w.WriteEndElement(); // Style
    }

    // Crystal's border style codes, mapped to RDL's BorderStyleEnum names. Only "single"
    // is corpus-verified; 2-4 follow Crystal's line-style list order.
    /// <summary>
    /// Crystal's point size converted to the em size RDL means by FontSize.
    ///
    /// The two are different measurements of the same font. Crystal hands the size to GDI as
    /// a *positive* lfHeight, which GDI reads as the character cell height - ascent plus
    /// descent - and it then picks whatever em size makes the cell that tall. RDL's FontSize
    /// is the em size directly. So a Crystal "10pt Arial" is an em of
    /// 10 x 2048/(1854+434) = 8.95pt, and emitting 10 renders every glyph about 11% too wide.
    ///
    /// That 11% had been recorded in this repo as an unfixable difference between the two
    /// renderers, on the grounds that ours matches Arial's published metrics. Ours does; it
    /// was measuring the wrong thing. Crystal's own PDF export gives the answer directly - it
    /// writes "8.95 Tf" for a 10pt Arial object, "14.3" for 16pt and "29.55" for 33pt, all of
    /// them the nominal size times 0.8951 - and the rendered glyph widths agree, ours coming
    /// out 1.10-1.13x the reference's across four independent measurements.
    ///
    /// The ratio is the font's own, upem/(usWinAscent + usWinDescent), read from the font
    /// files themselves - specifically OS/2's usWinAscent and usWinDescent, which are what
    /// GDI's tmHeight is built from. For every family here but one those match the hhea
    /// ascender and descender; Calibri is the exception, where hhea would give 1.0 and the
    /// OS/2 pair gives 0.8192. Verdana's 0.8228 predicts the 6.6 and 8.25 that Crystal writes for
    /// its 8pt and 10pt objects, which is the second family this was checked against. Weight
    /// does not enter into it: Arial and Arial Bold carry identical metrics.
    ///
    /// The ratio is not the whole answer. GDI hands back a font of a whole number of device
    /// units, and Crystal asks at one twip to the unit, so the em is a whole number of twips
    /// - every size its PDF export writes is a multiple of 0.05pt. Below about 15pt GDI
    /// also sizes from the font's own per-size metrics, which can move the em several twips
    /// off the ratio and differ by weight and slant: Arial Bold 11pt is 9.65 in Crystal's
    /// PDF where the ratio says 9.85, Verdana Bold 12pt 9.95 where it says 9.87. GdiEmSizes
    /// holds those corrections. With them, the em here is GDI's own, twip for twip.
    ///
    /// A family that is not listed is emitted unchanged. Its true ratio is its own font's and
    /// guessing one would mis-size text silently; leaving it alone keeps the behaviour this
    /// converter has always had. The list covers every family in the public corpus and all
    /// but a few hundred objects of the private one.
    /// </summary>
    internal static double EmPointsFor(string? family, double crystalPoints, bool bold = false, bool italic = false)
    {
        if (family is null || !WinMetrics.TryGetValue(family, out var m))
            return crystalPoints;
        int cellTwips = (int)Math.Round(crystalPoints * 20, MidpointRounding.AwayFromZero);
        int emTwips = (int)Math.Round(cellTwips * CellHeightRatio(m), MidpointRounding.AwayFromZero)
            + GdiEmSizes.CorrectionTwips(family, bold, italic, cellTwips);
        return emTwips / 20.0;
    }

    // upem / (usWinAscent + usWinDescent): the em as a fraction of the cell. Every family
    // here has a 2048-unit em.
    private static double CellHeightRatio((int WinAscent, int WinDescent) m) =>
        2048.0 / (m.WinAscent + m.WinDescent);

    // OS/2 usWinAscent and usWinDescent, read from the font files themselves.
    private static readonly Dictionary<string, (int WinAscent, int WinDescent)> WinMetrics =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Arial"] = (1854, 434),            // em 0.89510 of the cell
            ["Arial Black"] = (2254, 634),      // 0.70914
            ["Verdana"] = (2059, 430),          // 0.82282
            ["Times New Roman"] = (1825, 443),  // 0.90300
            ["Courier New"] = (1705, 615),      // 0.88276
            ["Tahoma"] = (2049, 423),           // 0.82848
            ["Calibri"] = (1950, 550),          // 0.81920
            ["Impact"] = (2066, 432),           // 0.81986
            ["Cambria"] = (1946, 455),          // 0.85298
        };

    /// <summary>
    /// How much lower Crystal puts a line's baseline inside its object than this engine does,
    /// in twips, for the families where that is established - otherwise zero.
    ///
    /// This engine draws a line's baseline one em below the top of its box. Crystal draws it
    /// usWinAscent/upem of the object's *nominal* point size below - as if the em were the
    /// point size, although the glyphs themselves are drawn at the cell-derived em (see
    /// EmPointsFor). The drop is the difference between the two, taken against the em this
    /// converter emits: Verdana Bold 12pt's 9.95, not the ratio's 9.87, which had left that
    /// group caption 0.09pt low. The two nearly agree for Arial, whose winAscent is about the size of its
    /// em ratio, which is why Arial reports line up; they do not for a font whose cell is much
    /// taller than its em. Verdana 10 sits 1.83pt lower in Crystal than in ours.
    ///
    /// Established for Verdana and Impact, and applied to those two. Measured from both
    /// engines' PDFs as the baseline offset divided by Crystal's em size, Verdana objects sit
    /// -0.218 to -0.224 against -0.222 predicted, and from Crystal's own geometry alone - no
    /// render of ours - Verdana page-footer text has its baseline 1.003 of its point size
    /// below its object's top, against 1.005. On SalesByCustomer-Grouped its four sizes (8,
    /// 10, 12, 18) were 1.46-3.47pt high and are now within 0.2pt. Impact's 18pt title on
    /// USA-Orders-RWB-colored sits 3.33pt higher in ours than in Crystal's against 3.40
    /// predicted, measured baseline to baseline with the same font on both sides. (It had
    /// looked like a contradiction while the engine drew that title in Arial Bold Italic:
    /// measuring glyph tops across two different fonts measured the fonts.)
    ///
    /// The formula is not applied to the other families it covers, because it has not held:
    ///  - Arial: predicted 0.011 em, and the Arial reports measure about zero, so moving
    ///    116,000 private objects by a tenth of a point on its say-so is not justified.
    ///  - Tahoma, Calibri, Cambria: measured only in typography__font_faces, whose boxes are
    ///    2.6 times the line, where the tall-box inset (#23) is mixed in; they fit only after a
    ///    common bias is taken out.
    ///  - Courier New and Georgia, which Crystal draws *higher* than we do, would need the box
    ///    moved rather than padded. Georgia Bold does match the prediction (+0.091 em against
    ///    +0.094), which is the evidence the formula is right in form, but one object.
    /// </summary>
    private static int BaselineDropTwips(ObjectFormat? fmt)
    {
        if (fmt?.FontName is not string family || fmt.FontSize is not double points || points <= 0)
            return 0;
        if (!BaselineDropFamilies.Contains(family) || !WinMetrics.TryGetValue(family, out var m))
            return 0;
        // Against the em actually emitted, which is GDI's rather than the bare ratio's.
        double dropPoints = points * m.WinAscent / 2048.0 - EmPointsFor(family, points, fmt.Bold, fmt.Italic);
        return (int)Math.Round(dropPoints * 20);
    }

    private static readonly HashSet<string> BaselineDropFamilies =
        new(StringComparer.OrdinalIgnoreCase) { "Verdana", "Impact" };

    private static string RdlBorderStyle(byte code) => code switch
    {
        2 => "Double",
        3 => "Dashed",
        4 => "Dotted",
        _ => "Solid",
    };

    // RDL's TextAlign has no justify, so the one Crystal alignment without a schema
    // equivalent is spelled the way the target engine spells it.
    private static string RdlTextAlign(HorizontalAlignment align) =>
        align == HorizontalAlignment.Justify ? "Justified" : align.ToString();

    // Offset of a drop shadow from the frame that casts it - the frame, not the object's
    // bounds, since the frame sits outside them (see BorderOutsetTwips). Measured off the real
    // engine's render of SalesByCustomer-Grouped's report header at 300dpi: the frame's line
    // runs x 50-2428, y 61-382, its centreline being the bounds grown by 30 twips; the shadow
    // fills x 65-2439, y 76-393, which is that centreline rectangle moved 12.8px right and
    // down. 12.8px at 300dpi is 62 twips. It had been measured from the bounds as 72, before
    // the frame was known not to sit on them.
    private const int DropShadowOffsetTwips = 62;
    private const int DropShadowFrameTwips = BorderOutsetTwips - 10;

    /// <summary>
    /// Crystal's drop shadow as the two strips that are visible: one below the object,
    /// shifted right, and one to its right, shifted down. Filled black, emitted before the
    /// object so it cannot paint over it.
    ///
    /// Two strips rather than one offset rectangle on purpose. A shadowed object normally
    /// sets no background of its own (every border record on the report this was measured
    /// from carries bg=FFFFFFFF), so a single rectangle behind a transparent box would show
    /// through the interior and fill it black instead of edging it.
    /// </summary>
    private void WriteDropShadow(XmlWriter w, ObjectBounds bounds, string? hiddenExpr, int leftOffsetTwips)
    {
        const int d = DropShadowOffsetTwips;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Cast from the frame's centreline rectangle. The one shadowed object measured is
        // framed on all four sides, so this does not decide what an unframed shadow does.
        const int f = DropShadowFrameTwips;
        var b = new ObjectBounds(Math.Max(0, bounds.Left - f), Math.Max(0, bounds.Top - f),
            bounds.Width + 2 * f, bounds.Height + 2 * f);

        WriteShadowStrip(w, new ObjectBounds(b.Left + d, b.Top + b.Height, b.Width, d),
            hiddenExpr, leftOffsetTwips);
        WriteShadowStrip(w, new ObjectBounds(b.Left + b.Width, b.Top + d, d, b.Height),
            hiddenExpr, leftOffsetTwips);
    }

    private void WriteShadowStrip(XmlWriter w, ObjectBounds b, string? hiddenExpr, int leftOffsetTwips)
    {
        w.WriteStartElement("Rectangle", RdlNs);
        w.WriteAttributeString("Name", $"shadow_{++_textboxCounter}");
        WriteObjectPosition(w, b, leftOffsetTwips);
        WriteItemVisibility(w, hiddenExpr);
        w.WriteStartElement("Style", RdlNs);
        w.WriteElementString("BackgroundColor", RdlNs, "#000000");
        w.WriteEndElement(); // Style
        w.WriteEndElement(); // Rectangle
    }

    // topOffsetTwips is where the section starts within its container: object bounds are
    // relative to their own section, so a section below another in the same container is
    // written that much lower.
    // The group level whose table group the free-form objects being written sit in, for a
    // percentage's scope (PercentageScope); null outside one. A group section lifted to the
    // Body (liftedToBody) is in no table group, so has none.
    private int? _freeFormGroupLevel;

    private void WriteFreeFormObjects(XmlWriter w, Section section, ReportDefinition? report = null,
        int leftOffsetTwips = 0, int topOffsetTwips = 0, bool liftedToBody = false)
    {
        int? outer = _freeFormGroupLevel;
        _freeFormGroupLevel = !liftedToBody && section.Type is SectionType.GroupHeader or SectionType.GroupFooter
            ? section.GroupLevel
            : null;
        try
        {
            WriteFreeFormObjectsCore(w, section, report, leftOffsetTwips, topOffsetTwips);
        }
        finally
        {
            _freeFormGroupLevel = outer;
        }
    }

    private void WriteFreeFormObjectsCore(XmlWriter w, Section section, ReportDefinition? report,
        int leftOffsetTwips, int topOffsetTwips)
    {
        // Free-form containers (PageHeader/PageFooter, Body items) have no row to
        // hide, so section-level suppression lands on each emitted item instead.
        string? hiddenExpr = TranspileSuppressFormula(section.SuppressFormula, report)
                             ?? (section.Suppress ? "true" : null);

        var knownFields = report is not null
            ? BuildKnownFieldsMap(report)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var parameterMap = report is not null
            ? BuildParameterMap(report)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Build "Group #N Name" → Fields!GroupField.Value lookup from report groups
        var groupNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (report is not null)
        {
            for (int gi = 0; gi < report.Groups.Count; gi++)
                groupNameMap[$"Group #{gi + 1} Name"] = GroupNameExpression(report.Groups[gi]);
        }

        // Objects used to be flowed left-to-right here, because the format appeared to
        // carry no position: the size record's two spare slots read zero for every object
        // in every file. They are zero because position is not in that record - it is in
        // the tag-190 that follows the object wrapper, which the parser now reads. Nothing
        // needs synthesizing, and a section whose objects genuinely share a left edge is
        // a vertical stack rather than a row to be spread out.
        //
        // A section still has to be tall enough for what it holds. Crystal stores the
        // designed band height, which is usually enough, but an object placed against the
        // bottom edge can round past it once heights are converted; growing the band is
        // harmless where it is unnecessary and keeps the last row visible where it is not.
        //
        // A growing object's room still ends at the band's designed bottom (see GrowingRoom):
        // a purchase order's column rules, drawn from its header down into a later section,
        // took the band 54pt past its row, and the addresses given room to there made every
        // row that tall.
        _designedHeights.TryAdd(section, section.HeightTwips);
        foreach (var obj in section.Objects)
        {
            if (obj is ShapeObject inPieces && _shapesInPieces.Contains(inPieces))
                continue;
            int bottom = obj.Bounds.Top + obj.Bounds.Height;
            if (bottom > section.HeightTwips)
                section.HeightTwips = bottom;
        }

        if (_shapePieces.TryGetValue(section, out var pieces))
            foreach (var (shape, bounds, openTop, openBottom) in pieces)
                WriteShape(w, shape, bounds, shape.SuppressOverride is { } suppressed ? (suppressed ? "true" : "false") : hiddenExpr,
                    leftOffsetTwips, topOffsetTwips, openTop, openBottom);

        foreach (var obj in section.Objects)
        {
            if (obj is ShapeObject inPieces && _shapesInPieces.Contains(inPieces))
                continue;

            // A suppressed object keeps its space, as in Crystal. Written hidden alone, the
            // engine closed up the space it took and moved what was below it up: a ledger's
            // suppressed title put the whole page 0.3in too high. An empty box of its size,
            // which prints nothing, holds the space.
            //
            // One suppressed whatever the data is not written at all, which moves nothing
            // either, as every position is the band's own, and leaves the band holding only
            // what prints. It is kept as a box where the band would otherwise hold nothing,
            // as an empty band is fatal to the engine.
            if (obj.SuppressOverride == true && section.Objects.Any(o => o.SuppressOverride != true))
                continue;
            if (obj.SuppressOverride == true
                || (obj.SuppressOverride is null && ObjectSuppressExpression(obj, report) is not null))
            {
                w.WriteStartElement("Textbox", RdlNs);
                w.WriteAttributeString("Name", $"Textbox_{++_textboxCounter}");
                WriteObjectPosition(w, obj.Bounds, leftOffsetTwips, topOffsetTwips);
                w.WriteElementString("Value", RdlNs, "");
                w.WriteEndElement(); // Textbox
            }

            // A per-object suppress override (Crystal's ReportObjects[x].ObjectFormat.EnableSuppress)
            // wins over whatever the section itself would otherwise apply.
            string? itemHidden = obj.SuppressOverride switch
            {
                true => "true",
                false => "false",
                null => EitherHidden(hiddenExpr, ObjectSuppressExpression(obj, report))
            };

            // Crystal's drop shadow, which RDL has no property for. Emitted as the two
            // strips that are actually visible rather than one offset rectangle behind the
            // object: the object's own background is usually unset, so a rectangle behind a
            // transparent box would show through its interior and paint the whole thing
            // black. See WriteDropShadow.
            if (obj.Format?.DropShadow == true)
                WriteDropShadow(w, obj.Bounds, itemHidden, leftOffsetTwips);

            switch (obj)
            {
                case TextObject text:
                {
                    string textName = SanitizeName(text.Name.Length > 0 ? text.Name : $"text_{++_textboxCounter}");
                    var textRoom = GrowingRoom(section, text);
                    bool textGrows = GrowsPastLineLimit(text, ref textRoom);
                    var (textFrame, textPadding) = BorderFrame(text.Format, textRoom);
                    bool textHeld = WriteGrowthHolderStart(w, section, text, textFrame, textGrows, textName, itemHidden, leftOffsetTwips, topOffsetTwips);
                    w.WriteStartElement("Textbox", RdlNs);
                    w.WriteAttributeString("Name", textName);
                    if (textHeld)
                        WriteObjectPosition(w, textFrame with { Left = 0, Top = 0 });
                    else
                    {
                        WriteObjectPosition(w, textFrame, leftOffsetTwips, topOffsetTwips);
                        WriteItemVisibility(w, itemHidden);
                    }
                    w.WriteElementString("Value", RdlNs, ResolveTextWithFieldRefs(text.Text, knownFields, groupNameMap, report?.ReportComments ?? string.Empty, report?.ReportTitle ?? string.Empty, parameterMap,
                        EmbeddedDateFormats(text, report)));
                    w.WriteElementString("CanGrow", RdlNs, textGrows ? "true" : "false");
                    WriteObjectStyle(w, text.Format,
                        ResolveTextWithFieldRefs(text.Text, knownFields, groupNameMap,
                            report?.ReportComments ?? string.Empty,
                            report?.ReportTitle ?? string.Empty, parameterMap),
                        inset: textPadding, boxHeightTwips: textRoom.Height);
                    w.WriteEndElement();
                    WriteGrowthHolderEnd(w, textHeld);
                    break;
                }

                case FieldObject field:
                {
                    string fieldName = SanitizeName(field.Name.Length > 0 ? field.Name : $"field_{++_textboxCounter}");
                    var fieldRoom = GrowingRoom(section, field);
                    bool fieldGrows = GrowsPastLineLimit(field, ref fieldRoom);
                    var (fieldFrame, fieldPadding) = BorderFrame(field.Format, fieldRoom);
                    bool fieldHeld = WriteGrowthHolderStart(w, section, field, fieldFrame, fieldGrows, fieldName, itemHidden, leftOffsetTwips, topOffsetTwips);
                    w.WriteStartElement("Textbox", RdlNs);
                    w.WriteAttributeString("Name", fieldName);
                    if (fieldHeld)
                        WriteObjectPosition(w, fieldFrame with { Left = 0, Top = 0 });
                    else
                    {
                        WriteObjectPosition(w, fieldFrame, leftOffsetTwips, topOffsetTwips);
                        WriteItemVisibility(w, itemHidden);
                    }
                    string fieldValue = PlacedFieldValue(field, report, knownFields, groupNameMap, parameterMap);
                    w.WriteElementString("Value", RdlNs, fieldValue);
                    w.WriteElementString("CanGrow", RdlNs, fieldGrows ? "true" : "false");
                    WriteObjectStyle(w, field.Format, fieldValue, inset: fieldPadding, boxHeightTwips: fieldRoom.Height);
                    w.WriteEndElement();
                    WriteGrowthHolderEnd(w, fieldHeld);
                    break;
                }

                case CrossTabObject crossTab:
                    WriteMatrix(w, crossTab, itemHidden, leftOffsetTwips, topOffsetTwips);
                    break;

                case ChartObject chart:
                    WriteChart(w, chart, itemHidden, report, leftOffsetTwips, topOffsetTwips);
                    break;

                case ShapeObject shape:
                    WriteShape(w, shape, shape.Bounds, itemHidden, leftOffsetTwips, topOffsetTwips);
                    break;

                // An on-demand subreport prints its caption, the value of its caption formula or
                // else its report's name, and none of its own content: Crystal runs it only
                // when a viewer clicks the caption. Run in place, a trial balance's per-account
                // detail made every account's row a dozen lines tall.
                case SubreportObject { OnDemand: true } onDemand:
                {
                    w.WriteStartElement("Rectangle", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(onDemand.Name.Length > 0 ? onDemand.Name : $"subreport_{++_textboxCounter}") + "_Frame");
                    WriteObjectPosition(w, onDemand.Bounds, leftOffsetTwips, topOffsetTwips);
                    WriteItemVisibility(w, itemHidden);
                    WriteFrameStyle(w, onDemand.Format);
                    w.WriteStartElement("ReportItems", RdlNs);
                    w.WriteStartElement("Textbox", RdlNs);
                    w.WriteAttributeString("Name", $"Textbox_{++_textboxCounter}");
                    w.WriteElementString("Top", RdlNs, TwipsToRdl(0));
                    w.WriteElementString("Left", RdlNs, TwipsToRdl(0));
                    w.WriteElementString("Width", RdlNs, TwipsToRdl(onDemand.Bounds.Width));
                    w.WriteElementString("Height", RdlNs, TwipsToRdl(onDemand.Bounds.Height));
                    string caption = OnDemandCaption(onDemand, report, knownFields, parameterMap, groupNameMap);
                    w.WriteElementString("Value", RdlNs, caption);
                    w.WriteElementString("CanGrow", RdlNs, "false");
                    WriteObjectStyle(w, onDemand.Format with
                    {
                        BorderLeft = 0, BorderRight = 0, BorderTop = 0, BorderBottom = 0, BackColor = null, DropShadow = false,
                    }, caption, boxHeightTwips: onDemand.Bounds.Height);
                    w.WriteEndElement(); // Textbox
                    w.WriteEndElement(); // ReportItems
                    w.WriteEndElement(); // Rectangle
                    break;
                }

                case SubreportObject sub when sub.Report is not null:
                {
                    // In a Rectangle of its own, placed where the object is, with the subreport at
                    // the Rectangle's top left corner. The Rectangle draws the subreport object's
                    // border, the frame Crystal draws around it. (Before Reporting 27 the engine
                    // dropped the gap above a subreport and ignored its container's Left, so the
                    // subreport repeated the Rectangle's Left. The engine now honours both, and a
                    // repeated Left would put the subreport there twice over.)
                    string subName = SanitizeName(sub.Name.Length > 0 ? sub.Name : $"subreport_{++_textboxCounter}");
                    w.WriteStartElement("Rectangle", RdlNs);
                    w.WriteAttributeString("Name", subName + "_Frame");
                    WriteObjectPosition(w, sub.Bounds, leftOffsetTwips, topOffsetTwips);
                    WriteItemVisibility(w, itemHidden);
                    WriteFrameStyle(w, sub.Format);
                    w.WriteStartElement("ReportItems", RdlNs);
                    w.WriteStartElement("Subreport", RdlNs);
                    w.WriteAttributeString("Name", subName);
                    w.WriteElementString("Top", RdlNs, TwipsToRdl(0));
                    w.WriteElementString("Left", RdlNs, TwipsToRdl(0));
                    w.WriteElementString("Width", RdlNs, TwipsToRdl(sub.Bounds.Width));
                    w.WriteElementString("Height", RdlNs, TwipsToRdl(sub.Bounds.Height));
                    // Companion .rdl written by the batch caller under this name
                    w.WriteElementString("ReportName", RdlNs, SubreportRdlName(_subreportNamePrefix, sub.SubreportName));
                    if (report is not null)
                        WriteSubreportParameters(w, sub, report);
                    w.WriteEndElement(); // Subreport
                    w.WriteEndElement(); // ReportItems
                    w.WriteEndElement(); // Rectangle
                    break;
                }

                case ImageObject image:
                    // Unresolved embedded images (missing storage / unknown format) are skipped
                    if (image.Source == ImageSourceKind.Embedded && image.ImageData is null)
                        break;
                    w.WriteStartElement("Image", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(image.Name.Length > 0 ? image.Name : $"image_{++_textboxCounter}"));
                    WriteObjectPosition(w, image.Bounds, leftOffsetTwips, topOffsetTwips);
                    WriteItemVisibility(w, itemHidden);
                    WriteImageSourceElements(w, image);
                    w.WriteEndElement();
                    break;
            }
        }
    }

    // A growing object's box, taken down to whatever is next below it. Crystal moves an
    // object down only when one above grows into it; the engine moves everything below a
    // growing item down by all of its growth, so a six-line name and address 0.9in above
    // the next line of a letter pushed the rest of its band 0.8in down, into the rows after
    // it. A box with no frame or fill draws the same at any height, so given the gap to
    // begin with it pushes only by what it grows past that.
    //
    // In fact Crystal moves nothing within a section: a growing object prints over whatever is
    // below it there, and only the section grows to hold it, which is why reports put what
    // must move into a section of its own. A receipt's five-line address prints over the
    // "In Account with" line beneath it. So the gap is all the way to the band's bottom: the
    // box is then above nothing as the engine reckons it, pushes nothing, and makes the band
    // taller only when it outgrows the band, as Crystal's section does. An employee's
    // four-line address pushed the column of fields on its right two lines down.
    private ObjectBounds GrowingRoom(Section section, ReportObject obj)
    {
        var fmt = obj.Format;
        if (fmt is not { CanGrow: true } || fmt.BackColor is not null || fmt.DropShadow
            || fmt.BorderLeft != 0 || fmt.BorderRight != 0 || fmt.BorderTop != 0 || fmt.BorderBottom != 0)
            return obj.Bounds;
        int bottom = obj.Bounds.Top + obj.Bounds.Height;
        int bandBottom = _designedHeights.TryGetValue(section, out int designed) ? designed : section.HeightTwips;
        return bandBottom > bottom ? obj.Bounds with { Height = bandBottom - obj.Bounds.Top } : obj.Bounds;
    }

    // A framed growing object cannot be given the room GrowingRoom gives a plain one, as its
    // frame is drawn at the height of its box. It still moves nothing else in its section when
    // it grows - Crystal draws it over what is below it there - where the engine moved every
    // item below it down by its growth: a notice's framed account details, grown four lines,
    // pushed the paragraph beneath them 65pt down. So it goes in a frameless Rectangle that
    // runs from its top to the band's bottom, which grows only when the box outgrows the band
    // and is above nothing for the engine to move. Returns whether it was so held; the box is
    // then placed at the Rectangle's top left corner, and the Rectangle carries its visibility.
    private bool WriteGrowthHolderStart(XmlWriter w, Section section, ReportObject obj, ObjectBounds frame,
        bool grows, string name, string? hidden, int leftOffsetTwips, int topOffsetTwips)
    {
        var fmt = obj.Format;
        if (!grows || fmt is null || !(fmt.BackColor is not null || fmt.DropShadow
            || fmt.BorderLeft != 0 || fmt.BorderRight != 0 || fmt.BorderTop != 0 || fmt.BorderBottom != 0))
            return false;
        int bandBottom = _designedHeights.TryGetValue(section, out int designed) ? designed : section.HeightTwips;
        if (bandBottom <= frame.Top + frame.Height)
            return false;
        w.WriteStartElement("Rectangle", RdlNs);
        w.WriteAttributeString("Name", name + "_Room");
        WriteObjectPosition(w, frame with { Height = bandBottom - frame.Top }, leftOffsetTwips, topOffsetTwips);
        WriteItemVisibility(w, hidden);
        w.WriteStartElement("ReportItems", RdlNs);
        return true;
    }

    private static void WriteGrowthHolderEnd(XmlWriter w, bool held)
    {
        if (!held) return;
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // Rectangle
    }

    // Each free-form band's height as the report designs it, before WriteFreeFormObjectsCore
    // grows it to hold what is placed past its bottom.
    private readonly Dictionary<Section, int> _designedHeights = new(ReferenceEqualityComparer.Instance);

    // Crystal's maximum number of lines on a growing object (ObjectFormat.MaxLines): it grows to
    // that many lines and stops, and the rest of its value does not print. The engine has no
    // such limit and grew a notice's seven-line name and address, which Crystal cuts at five,
    // pushing the rest of its band down. Where the lines fit in the room the box already has,
    // the box is made that many lines tall and may not grow, and the engine draws the lines
    // that fit in it and no more. A framed box keeps its own height, as its frame is drawn at
    // that height. Where the lines do not fit, it grows as it did. Returns whether it grows.
    private static bool GrowsPastLineLimit(ReportObject obj, ref ObjectBounds room)
    {
        var fmt = obj.Format;
        if (fmt is not { CanGrow: true })
            return false;
        if (LineLimitTwips(fmt) is not int lines)
            return true;
        bool framed = fmt.BackColor is not null || fmt.DropShadow
            || fmt.BorderLeft != 0 || fmt.BorderRight != 0 || fmt.BorderTop != 0 || fmt.BorderBottom != 0;
        if (framed)
            return lines > room.Height;
        if (lines > room.Height)
            return true;
        room = room with { Height = lines };
        return false;
    }

    // The height a growing object's line limit lets it reach, below the baseline drop that pads
    // its top, at the pitch WriteObjectStyle sets its lines at; null for no limit.
    private static int? LineLimitTwips(ObjectFormat? fmt)
    {
        if (fmt is not { MaxLines: > 0, FontSize: double size })
            return null;
        double pitch = (GdiLinePitches.PitchPoints(fmt.FontName, size, fmt.Bold, fmt.Italic)
                        ?? EmPointsFor(fmt.FontName, size, fmt.Bold, fmt.Italic)) * fmt.LineSpacing;
        return BaselineDropTwips(fmt) + (int)Math.Ceiling(fmt.MaxLines * pitch * 20);
    }

    // leftOffsetTwips is where the container's own left edge sits on the page. Object
    // bounds are absolute, and a free-form container is normally at the page's left edge,
    // so it is zero everywhere except inside a table band - see WriteTableFreeFormRow.
    //
    // Clamped at zero because RDL has no negative Left: an object left of the table's
    // first column cannot be expressed inside it, and the table's own edge is the closest
    // place there is, which is where it lands today anyway.
    // A free-form body section's background: a Rectangle as wide as the page body and as
    // tall as the section, written before the section's objects so they draw over it.
    // Free-form sections take their objects' positions from the top of the body, which is
    // where this sits too.
    // A frame: the object's border edges and width, and nothing else. Nothing when it has none.
    // A line's or box's stroke, from its border record (RptParser.ExtractBorders): the style
    // code (a line's on its top edge), colour and width. A shape the file gives no record
    // keeps the solid black 1pt it always drew. A width of 0 is Crystal's hairline, drawn at
    // the thinnest width RDL allows.
    private static void WriteShapeBorder(XmlWriter w, ObjectFormat fmt, byte styleCode,
        bool openTop = false, bool openBottom = false)
    {
        bool described = fmt.BorderLeft != 0 || fmt.BorderRight != 0 || fmt.BorderTop != 0 || fmt.BorderBottom != 0
            || fmt.BackColor is not null || fmt.BorderWidthTwips != 0;
        w.WriteStartElement("BorderStyle", RdlNs);
        w.WriteElementString("Default", RdlNs, !described ? "Solid" : styleCode == 0 ? "None" : RdlBorderStyle(styleCode));
        // A piece of a box drawn across sections has no edge where it meets the next piece.
        if (openTop) w.WriteElementString("Top", RdlNs, "None");
        if (openBottom) w.WriteElementString("Bottom", RdlNs, "None");
        w.WriteEndElement();
        if (fmt.BorderColor is { } color)
        {
            w.WriteStartElement("BorderColor", RdlNs);
            w.WriteElementString("Default", RdlNs, color);
            w.WriteEndElement();
        }
        if (described)
        {
            w.WriteStartElement("BorderWidth", RdlNs);
            w.WriteElementString("Default", RdlNs, $"{Math.Max(0.25, fmt.BorderWidthTwips / 20.0):0.##}pt");
            w.WriteEndElement();
        }
    }

    private static void WriteFrameStyle(XmlWriter w, ObjectFormat fmt)
    {
        if (fmt.BorderLeft == 0 && fmt.BorderRight == 0 && fmt.BorderTop == 0 && fmt.BorderBottom == 0)
            return;
        w.WriteStartElement("Style", RdlNs);
        w.WriteStartElement("BorderStyle", RdlNs);
        if (fmt.BorderLeft != 0) w.WriteElementString("Left", RdlNs, RdlBorderStyle(fmt.BorderLeft));
        if (fmt.BorderRight != 0) w.WriteElementString("Right", RdlNs, RdlBorderStyle(fmt.BorderRight));
        if (fmt.BorderTop != 0) w.WriteElementString("Top", RdlNs, RdlBorderStyle(fmt.BorderTop));
        if (fmt.BorderBottom != 0) w.WriteElementString("Bottom", RdlNs, RdlBorderStyle(fmt.BorderBottom));
        w.WriteEndElement();
        // In its own colour: a work order's subreports are each framed in white, which shows
        // nothing on the page, and drawn in the default black they boxed every one.
        if (fmt.BorderColor is { } color)
        {
            w.WriteStartElement("BorderColor", RdlNs);
            w.WriteElementString("Default", RdlNs, color);
            w.WriteEndElement();
        }
        w.WriteStartElement("BorderWidth", RdlNs);
        w.WriteElementString("Default", RdlNs, $"{(fmt.BorderWidthTwips > 0 ? fmt.BorderWidthTwips : 20) / 20.0:0.##}pt");
        w.WriteEndElement();
        w.WriteEndElement(); // Style
    }

    private void WriteSectionBackdrop(XmlWriter w, ReportDefinition report, Section section, int topTwips = 0)
    {
        int bodyWidth = report.Page.WidthTwips - report.Page.LeftMarginTwips - report.Page.RightMarginTwips;
        w.WriteStartElement("Rectangle", RdlNs);
        w.WriteAttributeString("Name", SanitizeName($"Backdrop_{section.Type}_{++_textboxCounter}"));
        w.WriteElementString("Top", RdlNs, TwipsToRdl(topTwips));
        w.WriteElementString("Left", RdlNs, TwipsToRdl(0));
        w.WriteElementString("Width", RdlNs, TwipsToRdl(Math.Max(1, bodyWidth)));
        w.WriteElementString("Height", RdlNs, TwipsToRdl(section.HeightTwips));
        WriteItemVisibility(w, TranspileSuppressFormula(section.SuppressFormula, report) ?? (section.Suppress ? "true" : null));
        WriteBackgroundStyle(w, section.BackColor);
        w.WriteEndElement(); // Rectangle
    }

    // A section written as one positioned Rectangle in the Body, its objects inside at their
    // own positions, which RDL takes relative to the Rectangle. Its width is the section's
    // rightmost object, since a band in the Body has no table column to take one from.
    private void WriteBandRectangle(XmlWriter w, Section section, ReportDefinition report, int topTwips)
    {
        int width = section.Objects.Max(o => o.Bounds.Left + o.Bounds.Width);
        int height = Math.Max(section.HeightTwips, section.Objects.Max(o => o.Bounds.Top + o.Bounds.Height));
        w.WriteStartElement("Rectangle", RdlNs);
        w.WriteAttributeString("Name", SanitizeName($"Band_{section.Type}_{++_textboxCounter}"));
        w.WriteElementString("Top", RdlNs, TwipsToRdl(topTwips));
        w.WriteElementString("Left", RdlNs, TwipsToRdl(0));
        w.WriteElementString("Width", RdlNs, TwipsToRdl(width));
        w.WriteElementString("Height", RdlNs, TwipsToRdl(height));
        WriteBackgroundStyle(w, section.BackColor);
        w.WriteStartElement("ReportItems", RdlNs);
        WriteFreeFormObjects(w, section, report);
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // Rectangle
    }

    private void WriteObjectPosition(XmlWriter w, ObjectBounds bounds, int leftOffsetTwips = 0, int topOffsetTwips = 0)
    {
        w.WriteElementString("Top", RdlNs, TwipsToRdl(bounds.Top + topOffsetTwips));
        w.WriteElementString("Left", RdlNs, TwipsToRdl(Math.Max(0, bounds.Left - leftOffsetTwips)));
        w.WriteElementString("Width", RdlNs, TwipsToRdl(bounds.Width));
        w.WriteElementString("Height", RdlNs, TwipsToRdl(bounds.Height));
    }

    private void WritePageHeader(XmlWriter w, ReportDefinition report, List<Section> consumedByTable)
    {
        var sections = report.Sections.Where(s => s.Type == SectionType.PageHeader && !consumedByTable.Contains(s)).ToList();
        if (sections.Count > 1)
        {
            WriteStackedPageBand(w, report, "PageHeader", sections);
            return;
        }
        var section = sections.FirstOrDefault();
        // An empty one is not written: on page 1 it is a gap below the report headers, which
        // the Body leaves (see EmptyPageHeaderTwips), and an RDL page header would put it above
        // them instead, as the engine reserves a page header's height at the top of every page.
        if (section is null || !HasRenderableContent(section)) return;

        w.WriteStartElement("PageHeader", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(section.HeightTwips));
        w.WriteElementString("PrintOnFirstPage", RdlNs, "true");
        w.WriteElementString("PrintOnLastPage", RdlNs, "true");
        WriteBackgroundStyle(w, section.BackColor);
        w.WriteStartElement("ReportItems", RdlNs);
        WriteFreeFormObjects(w, section, report);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>
    /// The height of a page header with nothing in it, which on page 1 Crystal prints below
    /// the report headers, as it does every page header: the pct_base probes' report and page
    /// headers are both 600 twips and empty, and their groups printed 30pt above Crystal's,
    /// below the report header only. 0 when the page header holds something (it is an RDL page
    /// header), is suppressed, is one of several, or is printed in the Body already.
    /// </summary>
    private static int EmptyPageHeaderTwips(ReportDefinition report, IEnumerable<Section> consumed)
    {
        var headers = report.Sections.Where(s => s.Type == SectionType.PageHeader).ToList();
        if (headers.Count != 1) return 0;
        var header = headers[0];
        return consumed.Contains(header) || header.Suppress || !string.IsNullOrWhiteSpace(header.SuppressFormula)
            || HasRenderableContent(header) ? 0 : Math.Max(0, header.HeightTwips);
    }

    private void WritePageFooter(XmlWriter w, ReportDefinition report, List<Section> consumedByTable)
    {
        var sections = report.Sections.Where(s => s.Type == SectionType.PageFooter && !consumedByTable.Contains(s)).ToList();
        if (sections.Count > 1)
        {
            WriteStackedPageBand(w, report, "PageFooter", sections);
            return;
        }
        var section = sections.FirstOrDefault();
        if (section is null) return;

        // An empty page footer still takes its height off every page. TenPct-DiscountDays'
        // is 30pt with nothing in it, and Crystal breaks its first page 30pt above the bottom
        // margin - the last row ends at 748.6pt, 792 - 12 - 30 = 750 - where leaving the
        // footer out let ours run on to the margin and take three more rows, moving every
        // page after it. So an empty footer is written with its height and no ReportItems,
        // which RDL allows and the engine requires: an empty ReportItems is fatal (see
        // HasRenderableContent). One that is suppressed, or suppressed by a formula, is left
        // out as before - there is no measurement of what Crystal reserves for those.
        bool hasContent = HasRenderableContent(section);
        if (!hasContent && (section.Suppress || section.SuppressFormula is not null || section.HeightTwips <= 0))
            return;

        w.WriteStartElement("PageFooter", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(section.HeightTwips));
        w.WriteElementString("PrintOnFirstPage", RdlNs, "true");
        w.WriteElementString("PrintOnLastPage", RdlNs, "true");
        WriteBackgroundStyle(w, section.BackColor);
        if (hasContent)
        {
            w.WriteStartElement("ReportItems", RdlNs);
            WriteFreeFormObjects(w, section, report);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    /// <summary>
    /// A page header or footer of several sections, as one RDL band with each section's
    /// objects below the ones before it. Only the first was written: StatementOfAccount's
    /// second page footer, the publisher's block and the page number, went missing once
    /// its first, the company address, stayed in the page footer rather than the table's.
    /// A section suppressed outright takes no space, as in Crystal; an empty one takes its
    /// height, as a single empty page footer does (see WritePageFooter).
    /// </summary>
    private void WriteStackedPageBand(XmlWriter w, ReportDefinition report, string band, List<Section> sections)
    {
        var shown = sections.Where(s => !s.Suppress && s.HeightTwips > 0).ToList();
        if (!shown.Any(HasRenderableContent)) return;

        w.WriteStartElement(band, RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(shown.Sum(s => s.HeightTwips)));
        w.WriteElementString("PrintOnFirstPage", RdlNs, "true");
        w.WriteElementString("PrintOnLastPage", RdlNs, "true");
        w.WriteStartElement("ReportItems", RdlNs);
        int top = 0;
        foreach (var section in shown)
        {
            if (section.BackColor is not null)
                WriteSectionBackdrop(w, report, section, top);
            if (HasRenderableContent(section))
                WriteFreeFormObjects(w, section, report, topOffsetTwips: top);
            top += section.HeightTwips;
        }
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement();
    }

    // A section can have Objects.Count > 0 yet still produce zero XML elements —
    // WriteFreeFormObjects silently skips unresolved embedded images, subreports with
    // no linked report, and cross-tabs missing a row/column/cell axis. An empty
    // <ReportItems> is a fatal (Severity 8) error to the engine — "At least one item
    // must be in the ReportItems" — which doesn't just drop that one section; it
    // cascades into null-reference exceptions on every other expression evaluation for
    // the rest of the render. Mirrors the same skip conditions WriteFreeFormObjects's
    // switch uses, so a PageHeader/PageFooter with nothing real to show is omitted
    // entirely instead of emitting an empty shell (both are optional at the Report level).
    private static bool HasRenderableContent(Section section) =>
        section.Objects.Any(obj => obj switch
        {
            ImageObject { Source: ImageSourceKind.Embedded, ImageData: null } => false,
            SubreportObject { Report: null } => false,
            CrossTabObject ct => CrossTabGrid.Writable(ct),
            _ => true
        });

    // Bind child subreport parameters to parent values by naming convention.
    // Crystal stores the actual link table in encrypted streams, but linked child
    // parameters are conventionally named after the parent thing they bind to:
    // "Pm-Table.Column" (wizard links), "@Formula" (formula links), or the bare
    // parent field/parameter name. Unresolvable parameters stay promptable.
    /// <summary>
    /// How each of the child report's parameters is fed from the parent: either a parent
    /// DataSet field (a Fields! expression, which only resolves inside a DataRegion — see
    /// SubreportBindsParentFields) or one of the parent's own parameters. Parameters the
    /// parent has nothing to supply are left unbound.
    /// </summary>
    private static List<(string ChildParam, string ParentExpr)> SubreportParameterBindings(
        SubreportObject sub, ReportDefinition parent)
    {
        var bindings = new List<(string ChildParam, string ParentExpr)>();
        foreach (var childParam in sub.Report!.Fields.OfType<ParameterField>())
        {
            // A link's name says what it takes: "Pm-?Name" the parent's parameter, "Pm-@Name"
            // its formula, "Pm-Table.Column" its column. A parameter not named "Pm-" takes the
            // parent's parameter of the same name, as Crystal passes it - a stored procedure's
            // @Invoice the parent's @Invoice, not a column or formula that shares the name.
            bool linked = childParam.Name.StartsWith("Pm-", StringComparison.OrdinalIgnoreCase);
            string source = linked ? childParam.Name[3..] : childParam.Name;
            string candidate = source;
            int dot = candidate.IndexOf('.');
            if (dot >= 0) candidate = candidate[(dot + 1)..];      // "Table.Column" → "Column"
            candidate = candidate.Trim('@', '?', '$', '[', ']');

            string? expr = null;
            if ((!linked || source.StartsWith('?'))
                && parent.Fields.OfType<ParameterField>().FirstOrDefault(p =>
                    string.Equals(p.Name, linked ? source[1..] : source, StringComparison.OrdinalIgnoreCase))
                is { } sameParam)
                expr = $"=Parameters!{SanitizeName(FormulaTranspiler.StripSapParamWrapper(sameParam.Name))}.Value";
            else if ((parent.Fields.OfType<FormulaField>().FirstOrDefault(f =>
                    string.Equals(NormalizeFieldName(f.Name), candidate, StringComparison.OrdinalIgnoreCase)) is { } formula
                    ? (Found: true, Type: formula.ResultType)
                    : parent.Fields.OfType<DatabaseField>().FirstOrDefault(f =>
                        string.Equals(f.ColumnName, candidate, StringComparison.OrdinalIgnoreCase)) is { } column
                        ? (Found: true, Type: column.DataType)
                        : (Found: false, Type: null)) is { Found: true } match
                // Matched by name alone, a column or formula feeds the parameter only where it
                // could: a string, or a value of the parameter's own kind. A stored procedure's
                // Boolean @Invoice is not the parent's Invoice column of invoice numbers.
                && (linked || ValueKindOf(childParam.DataType) is "String" or ""
                    || ValueKindOf(match.Type) is "" || ValueKindOf(match.Type) == ValueKindOf(childParam.DataType)))
                expr = $"=Fields!{SanitizeName(candidate)}.Value";
            else if (parent.Fields.OfType<ParameterField>().FirstOrDefault(p =>
                    string.Equals(p.Name.Trim('@', '?', '$', '[', ']'), candidate, StringComparison.OrdinalIgnoreCase))
                is { } parentParam)
                // Must match the parent report's own declared parameter Name (see
                // WriteReportParameters — same "$[Id]" wrapper stripped there too).
                expr = $"=Parameters!{SanitizeName(FormulaTranspiler.StripSapParamWrapper(parentParam.Name))}.Value";

            if (expr is not null)
                // Must match the child report's own declared parameter Name exactly —
                // WriteReportParameters strips the same "$[Id]" SAP wrapper before
                // sanitizing, so this needs to strip it too or the two never agree.
                bindings.Add((SanitizeName(FormulaTranspiler.StripSapParamWrapper(childParam.Name)), expr));
        }
        return bindings;
    }

    // A model type's kind - Number, Boolean, DateTime or String - or "" when it is not known.
    private static string ValueKindOf(string? type) => type switch
    {
        "Float64" or "Float32" or "Currency" or "Int16" or "Int32" or "Number" => "Number",
        "Boolean" => "Boolean",
        "DateTime" or "Date" or "Time" => "DateTime",
        "String" => "String",
        _ => ""
    };

    /// <summary>
    /// Whether a placed subreport feeds any of its parameters from a parent DataSet field.
    /// Those become Fields! expressions, which the engine resolves by walking ancestors for
    /// a DataRegion — so such a Subreport has to sit inside the table, exactly like the
    /// field-bound header sections NeedsTableRouting already routes there. Left at Body
    /// level it fails with "Field 'X' not found" even though the field is in the DataSet.
    /// </summary>
    private static bool SubreportBindsParentFields(SubreportObject sub, ReportDefinition parent) =>
        sub.Report is not null
        && SubreportParameterBindings(sub, parent)
            .Any(b => b.ParentExpr.Contains("Fields!", StringComparison.Ordinal));

    private void WriteSubreportParameters(XmlWriter w, SubreportObject sub, ReportDefinition parent)
    {
        // Derived parent-field links first; a runtime value for the same parameter replaces
        // its link, and one for an unlinked parameter is added.
        var values = new List<(string ChildParam, string Expr)>(SubreportParameterBindings(sub, parent));
        foreach (var (declaredName, value) in sub.ParameterValueOverrides)
        {
            string childParam = SanitizeName(FormulaTranspiler.StripSapParamWrapper(declaredName));
            values.RemoveAll(b => string.Equals(b.ChildParam, childParam, StringComparison.OrdinalIgnoreCase));
            values.Add((childParam, LiteralExpression(value)));
        }
        if (values.Count == 0) return;

        w.WriteStartElement("Parameters", RdlNs);
        foreach (var (childParam, expr) in values)
        {
            w.WriteStartElement("Parameter", RdlNs);
            w.WriteAttributeString("Name", childParam);
            w.WriteElementString("Value", RdlNs, expr);
            w.WriteEndElement();
        }
        w.WriteEndElement(); // Parameters
    }

    /// <summary>A runtime value as the RDL expression that evaluates to it.</summary>
    private static string LiteralExpression(object? value) => value switch
    {
        null => "=Nothing",
        bool b => b ? "=True" : "=False",
        string s => $"={QuoteLiteral(s)}",
        DateTime d => $"=CDate({QuoteLiteral(d.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))})",
        DateOnly d => $"=CDate({QuoteLiteral(d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))})",
        IFormattable n => "=" + n.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => $"={QuoteLiteral(value.ToString() ?? "")}"
    };

    // Crystal cross-tab → SSRS 2005 Matrix. RDL Matrix grouping levels are nested
    // outermost-first in document order: each <ColumnGrouping>/<RowGrouping> element is one
    // axis level, so N row fields or M column fields become N/M dynamic grouping levels. When
    // more than one summary cell is defined, they become an extra innermost *static* level:
    // rows (one <StaticRow> and one MatrixRow per cell) when Crystal stacks them, as it does
    // unless set otherwise, or columns (one <StaticColumn> and one MatrixCell/MatrixColumn per
    // cell) - the engine requires the counts to match the static level's (count ==
    // max(1, StaticCount)).
    //
    // The grid is drawn the way the file lays it out - see CrossTabGrid for the sizes, the
    // totals and the lines, and RptParser.CrossTab for where each comes from. Crystal's
    // grand totals are a Subtotal on the outermost grouping of each axis. Totals inside an
    // outer group (a nested level's subtotals) are not written: Crystal prints them by
    // default, but whether a file turns them off is not decoded, and no report in either
    // corpus with more than one level on an axis has rows to show what it does.
    private void WriteMatrix(XmlWriter w, CrossTabObject crossTab, string? hiddenExpr,
        int leftOffsetTwips = 0, int topOffsetTwips = 0)
    {
        if (!CrossTabGrid.Writable(crossTab))
            return;

        var grid = CrossTabGrid.Of(crossTab);
        // One field can be two levels of an axis - B1Budget_M's columns are its date twice,
        // by year and by month - or an axis of two cross-tabs, as ConsolidatedBalanceSheet's
        // quarters are, and a grouping name used twice is fatal to the report.
        var rowGroupNames = UniqueNames(crossTab.RowGroupFields
            .Select(f => $"MatrixRow_{SanitizeName(NormalizeFieldName(f))}"));
        var columnGroupNames = UniqueNames(crossTab.ColumnGroupFields
            .Select(f => $"MatrixColumn_{SanitizeName(NormalizeFieldName(f))}"));

        w.WriteStartElement("Matrix", RdlNs);
        w.WriteAttributeString("Name", SanitizeName(crossTab.Name.Length > 0 ? crossTab.Name : $"matrix_{++_textboxCounter}"));
        WriteObjectPosition(w, crossTab.Bounds, leftOffsetTwips, topOffsetTwips);
        WriteItemVisibility(w, hiddenExpr);
        w.WriteElementString("DataSetName", RdlNs, "DataSet1");

        w.WriteStartElement("ColumnGroupings", RdlNs);
        for (int k = 0; k < crossTab.ColumnGroupFields.Count; k++)
        {
            string colField = SanitizeName(NormalizeFieldName(crossTab.ColumnGroupFields[k]));
            w.WriteStartElement("ColumnGrouping", RdlNs);
            w.WriteElementString("Height", RdlNs, TwipsToRdl(grid.ColumnHeaderHeights[k]));
            w.WriteStartElement("DynamicColumns", RdlNs);
            w.WriteStartElement("Grouping", RdlNs);
            w.WriteAttributeString("Name", columnGroupNames[k]);
            w.WriteStartElement("GroupExpressions", RdlNs);
            w.WriteElementString("GroupExpression", RdlNs, $"=Fields!{colField}.Value");
            w.WriteEndElement(); // GroupExpressions
            w.WriteEndElement(); // Grouping
            if (k == 0 && !crossTab.SuppressRowGrandTotals && !grid.ColumnTotalTooNarrow)
                WriteMatrixGrandTotal(w, AxisLevel(crossTab.ColumnLevels, 0), grid.ColumnTotalFirst, grid);
            w.WriteStartElement("ReportItems", RdlNs);
            WriteMatrixTextbox(w, $"=Fields!{colField}.Value",
                AxisLevel(crossTab.ColumnLevels, k + 1)?.HeaderFormat, grid);
            w.WriteEndElement(); // ReportItems
            w.WriteEndElement(); // DynamicColumns
            w.WriteEndElement(); // ColumnGrouping
        }
        if (crossTab.Cells.Count > 1 && !grid.Stacked)
        {
            w.WriteStartElement("ColumnGrouping", RdlNs);
            w.WriteElementString("Height", RdlNs, "14pt");
            w.WriteStartElement("StaticColumns", RdlNs);
            foreach (var cell in crossTab.Cells)
            {
                w.WriteStartElement("StaticColumn", RdlNs);
                w.WriteStartElement("ReportItems", RdlNs);
                WriteMatrixTextbox(w, CellLabel(cell), new ObjectFormat { Bold = true }, grid);
                w.WriteEndElement(); // ReportItems
                w.WriteEndElement(); // StaticColumn
            }
            w.WriteEndElement(); // StaticColumns
            w.WriteEndElement(); // ColumnGrouping
        }
        w.WriteEndElement(); // ColumnGroupings

        w.WriteStartElement("RowGroupings", RdlNs);
        for (int k = 0; k < crossTab.RowGroupFields.Count; k++)
        {
            string rowField = SanitizeName(NormalizeFieldName(crossTab.RowGroupFields[k]));
            w.WriteStartElement("RowGrouping", RdlNs);
            w.WriteElementString("Width", RdlNs, TwipsToRdl(grid.RowHeaderWidths[k]));
            w.WriteStartElement("DynamicRows", RdlNs);
            w.WriteStartElement("Grouping", RdlNs);
            w.WriteAttributeString("Name", rowGroupNames[k]);
            w.WriteStartElement("GroupExpressions", RdlNs);
            w.WriteElementString("GroupExpression", RdlNs, $"=Fields!{rowField}.Value");
            w.WriteEndElement(); // GroupExpressions
            w.WriteEndElement(); // Grouping
            if (k == 0 && !crossTab.SuppressColumnGrandTotals)
                WriteMatrixGrandTotal(w, AxisLevel(crossTab.RowLevels, 0), grid.RowTotalFirst, grid);
            w.WriteStartElement("ReportItems", RdlNs);
            WriteMatrixTextbox(w, $"=Fields!{rowField}.Value",
                AxisLevel(crossTab.RowLevels, k + 1)?.HeaderFormat, grid);
            w.WriteEndElement(); // ReportItems
            w.WriteEndElement(); // DynamicRows
            w.WriteEndElement(); // RowGrouping
        }
        // Summaries stacked in the cell are an innermost static row level, one row each.
        // Crystal prints no label beside them unless the cross-tab asks for labels, which
        // is not decoded, so the label column is the row header's width left empty.
        if (grid.Stacked)
        {
            w.WriteStartElement("RowGrouping", RdlNs);
            w.WriteElementString("Width", RdlNs, TwipsToRdl(grid.SummaryLabelWidth));
            w.WriteStartElement("StaticRows", RdlNs);
            foreach (var _ in crossTab.Cells)
            {
                w.WriteStartElement("StaticRow", RdlNs);
                w.WriteStartElement("ReportItems", RdlNs);
                WriteMatrixTextbox(w, string.Empty, null, grid);
                w.WriteEndElement(); // ReportItems
                w.WriteEndElement(); // StaticRow
            }
            w.WriteEndElement(); // StaticRows
            w.WriteEndElement(); // RowGrouping
        }
        w.WriteEndElement(); // RowGroupings

        // The totals' own colours, on the cells as well as on their labels: a Subtotal's
        // Style is not something the engine applies, so the cell tells for itself whether
        // it is printing a total, by whether it is outside the outermost group.
        string? rowTotalColour = crossTab.SuppressColumnGrandTotals || rowGroupNames.Count == 0
            ? null : AxisLevel(crossTab.RowLevels, 0)?.TotalBackColor;
        string? columnTotalColour = crossTab.SuppressRowGrandTotals || grid.ColumnTotalTooNarrow
            ? null : AxisLevel(crossTab.ColumnLevels, 0)?.TotalBackColor;

        w.WriteStartElement("MatrixRows", RdlNs);
        if (grid.Stacked)
        {
            for (int i = 0; i < crossTab.Cells.Count; i++)
            {
                w.WriteStartElement("MatrixRow", RdlNs);
                w.WriteElementString("Height", RdlNs, TwipsToRdl(grid.RowHeight));
                w.WriteStartElement("MatrixCells", RdlNs);
                WriteMatrixCell(i);
                w.WriteEndElement(); // MatrixCells
                w.WriteEndElement(); // MatrixRow
            }
        }
        else
        {
            w.WriteStartElement("MatrixRow", RdlNs);
            w.WriteElementString("Height", RdlNs, TwipsToRdl(grid.RowHeight));
            w.WriteStartElement("MatrixCells", RdlNs);
            for (int i = 0; i < crossTab.Cells.Count; i++)
                WriteMatrixCell(i);
            w.WriteEndElement(); // MatrixCells
            w.WriteEndElement(); // MatrixRow
        }
        w.WriteEndElement(); // MatrixRows

        w.WriteStartElement("MatrixColumns", RdlNs);
        for (int i = 0; i < (grid.Stacked ? 1 : crossTab.Cells.Count); i++)
        {
            w.WriteStartElement("MatrixColumn", RdlNs);
            w.WriteElementString("Width", RdlNs, TwipsToRdl(grid.ColumnWidth));
            w.WriteEndElement(); // MatrixColumn
        }
        w.WriteEndElement(); // MatrixColumns

        w.WriteEndElement(); // Matrix

        void WriteMatrixCell(int i)
        {
            var cell = crossTab.Cells[i];
            string cellExpr = CrossTabCellExpression(cell);
            var format = grid.SummaryFormat(i);
            if (rowTotalColour is not null || columnTotalColour is not null)
                format = (format ?? new ObjectFormat()) with
                {
                    // The row group is read only with a row total colour, which needs one.
                    BackColor = TotalColourExpression(rowTotalColour, rowGroupNames.FirstOrDefault() ?? "",
                        columnTotalColour, columnGroupNames[0], format?.BackColor),
                };
            w.WriteStartElement("MatrixCell", RdlNs);
            w.WriteStartElement("ReportItems", RdlNs);
            WriteMatrixTextbox(w, cellExpr, format, grid);
            w.WriteEndElement(); // ReportItems
            w.WriteEndElement(); // MatrixCell
        }
    }

    // Each name as it is, or numbered when a grouping written earlier in the report has it.
    private List<string> UniqueNames(IEnumerable<string> names) =>
        names.Select(n =>
        {
            string name = n;
            for (int k = 2; !_matrixGroupingNames.Add(name); k++) name = $"{n}_{k}";
            return name;
        }).ToList();

    private static CrossTabAxisLevel? AxisLevel(List<CrossTabAxisLevel> levels, int level) =>
        levels.FirstOrDefault(l => l.Level == level);

    /// <summary>
    /// A summarized value. A Sum or a count of nothing prints 0 in Crystal, formatted like
    /// any other value: Canada-CrossTab's export has "$0.00" in every province that bought
    /// none of a product type, 33 of its 40 data cells, where the engine leaves an empty
    /// intersection blank. Other summaries of nothing are left blank - no export shows what
    /// Crystal prints for an empty average or maximum.
    ///
    /// The summary is the IIf's first branch because the engine types an IIf by that branch:
    /// with the literal 0 first, every amount was rounded to a whole number of dollars.
    /// </summary>
    private string CrossTabCellExpression(CrossTabCell cell)
    {
        string expr = BuildSummaryExpression(cell.Function, SanitizeName(NormalizeFieldName(cell.FieldName)));
        return cell.Function is AggregateFunction.Sum or AggregateFunction.Count or AggregateFunction.DistinctCount
            ? $"=IIf(Count(Fields!{SanitizeName(NormalizeFieldName(cell.FieldName))}.Value) > 0, {expr[1..]}, 0)"
            : expr;
    }

    /// <summary>
    /// The background of a summary cell: the Total row's colour outside every row group,
    /// the Total column's outside every column group, the cell's own everywhere else. Null
    /// when there is nothing to choose between.
    /// </summary>
    private static string? TotalColourExpression(string? rowTotal, string rowGroup,
        string? columnTotal, string columnGroup, string? own)
    {
        string expr = $"\"{own ?? "Transparent"}\"";
        if (columnTotal is not null)
            expr = $"IIf(InScope(\"{columnGroup}\"), {expr}, \"{columnTotal}\")";
        if (rowTotal is not null)
            expr = $"IIf(InScope(\"{rowGroup}\"), {expr}, \"{rowTotal}\")";
        return "=" + expr;
    }

    /// <summary>
    /// A grand total: the Subtotal of an axis's outermost grouping, labelled with the label
    /// Crystal keeps on that axis's level 0, in its font and colour.
    /// </summary>
    private void WriteMatrixGrandTotal(XmlWriter w, CrossTabAxisLevel? level0, bool first, CrossTabGrid grid)
    {
        string label = level0?.TotalLabel ?? "Total";
        var format = level0?.TotalLabelFormat;
        if (level0?.TotalBackColor is { } colour)
            format = (format ?? new ObjectFormat()) with { BackColor = colour };
        w.WriteStartElement("Subtotal", RdlNs);
        w.WriteStartElement("ReportItems", RdlNs);
        WriteMatrixTextbox(w, label.StartsWith('=') ? $"={QuoteLiteral(label)}" : label, format, grid);
        w.WriteEndElement(); // ReportItems
        if (first)
            w.WriteElementString("Position", RdlNs, "Before");
        w.WriteEndElement(); // Subtotal
    }

    /// <summary>
    /// The cross-tab's grid as the file lays it out, in twips.
    ///
    /// Each cell's box comes from the file (RptParser.ReadGridCellBox) and is the cell whole,
    /// margins included, so the innermost data cell's box is a Matrix column's width and a
    /// row's height: Canada-CrossTab's 1328 x 430 is the 66.4pt x 21.5pt pitch of the grid in
    /// Crystal's export. A Matrix has one width for every column and one height for every
    /// row, so a total column or row that Crystal sizes differently from the data takes the
    /// data's size here.
    ///
    /// The header bands are the file's two header sizes, split between the levels of an
    /// axis where the levels' own header cells say where one ends and the next begins.
    ///
    /// The totals print first when their cell sits before the data cell: left of it for the
    /// Total column, above it for the Total row (see ReadGridCellBox).
    ///
    /// Without a layout - a cross-tab the file holds no grid cells for - every size is the
    /// 1in by 14pt this converter has always written.
    /// </summary>
    private sealed class CrossTabGrid
    {
        // Crystal's export strokes the grid at width 0 - the thinnest line the device draws,
        // one pixel in the 300dpi reference renders - and 0.25pt is the thinnest RDL allows.
        private const int GridLineTwips = 5;

        public List<int> ColumnHeaderHeights { get; } = [];
        public List<int> RowHeaderWidths { get; } = [];
        public int ColumnWidth { get; private init; } = 1440;
        public int RowHeight { get; private init; } = 280;
        public int MarginLeft { get; private init; }
        public int MarginTop { get; private init; }
        public bool ShowGrid { get; private init; }
        public bool RowTotalFirst { get; private init; }
        public bool ColumnTotalFirst { get; private init; }
        public bool Stacked { get; private init; }
        public int SummaryLabelWidth { get; private init; }
        public bool ColumnTotalTooNarrow { get; private init; }
        private CrossTabCellLayout? DataCell { get; init; }

        // A line of the smallest text Crystal sets in a cell, margins included.
        private const int SummaryLineTwips = 180;

        // Narrower than the margins and one digit, a total column prints nothing in Crystal:
        // ConsolidatedBalanceSheet's is 270 twips, and its export shows the quarters alone.
        private const int NarrowestTotalTwips = 360;

        /// <summary>
        /// Whether the cross-tab can be written as a Matrix: summaries, a column axis, and a
        /// row axis - or, with no row groups, summaries stacked into rows of their own.
        /// </summary>
        public static bool Writable(CrossTabObject crossTab) =>
            crossTab.Cells.Count > 0 && crossTab.ColumnGroupFields.Count > 0
            && (crossTab.RowGroupFields.Count > 0 || Of(crossTab).Stacked);

        private static (CrossTabCellLayout? Data, CrossTabCellLayout? RowTotal, CrossTabCellLayout? ColumnTotal)
            Cells(List<CrossTabCellLayout> layouts)
        {
            if (layouts.Count == 0) return (null, null, null);
            int innerRow = layouts.Max(c => c.RowLevel), innerColumn = layouts.Max(c => c.ColumnLevel);
            return (layouts.FirstOrDefault(c => c.RowLevel == innerRow && c.ColumnLevel == innerColumn),
                layouts.FirstOrDefault(c => c.RowLevel == 0 && c.ColumnLevel == innerColumn),
                layouts.FirstOrDefault(c => c.RowLevel == innerRow && c.ColumnLevel == 0));
        }

        public static CrossTabGrid Of(CrossTabObject crossTab)
        {
            var (data, rowTotal, columnTotal) = Cells(crossTab.CellLayouts);
            bool sized = data is { Box.Width: > 0, Box.Height: > 0 };
            int summaries = crossTab.Cells.Count;
            // Crystal stacks a cell's summaries one above the next unless told to set them
            // side by side, and the cell is sized to match: a column wide and a line per
            // summary tall - B1Budget's three in 875 twips, ConsolidatedBalanceSheet's
            // thirteen in 4926. The setting itself is not decoded; the cell's shape tells.
            bool stacked = sized && summaries > 1 && data!.Box.Height / summaries >= SummaryLineTwips;
            var grid = new CrossTabGrid
            {
                DataCell = data,
                Stacked = stacked,
                SummaryLabelWidth = crossTab.RowGroupFields.Count == 0 ? Math.Max(1, crossTab.RowHeaderWidthTwips) : 1,
                ColumnTotalTooNarrow = columnTotal is { Box.Width: > 0 and < NarrowestTotalTwips },
                ColumnWidth = sized ? Math.Max(1, stacked ? data!.Box.Width : data!.Box.Width / summaries) : 1440,
                RowHeight = sized ? (stacked ? data!.Box.Height / summaries : data!.Box.Height) : 280,
                MarginLeft = data?.MarginLeftTwips ?? 0,
                MarginTop = data?.MarginTopTwips ?? 0,
                ShowGrid = crossTab.ShowGrid,
                RowTotalFirst = data is not null && rowTotal is not null && rowTotal.Box.Top < data.Box.Top,
                ColumnTotalFirst = data is not null && columnTotal is not null && columnTotal.Box.Left < data.Box.Left,
            };
            grid.ColumnHeaderHeights.AddRange(SplitBand(crossTab.ColumnHeaderHeightTwips, crossTab.Bounds.Top,
                crossTab.ColumnGroupFields.Count, crossTab.ColumnLevels, b => b.Top, grid.MarginTop, 280));
            grid.RowHeaderWidths.AddRange(SplitBand(crossTab.RowHeaderWidthTwips, crossTab.Bounds.Left,
                crossTab.RowGroupFields.Count, crossTab.RowLevels, b => b.Left, grid.MarginLeft, 1440));
            return grid;
        }

        /// <summary>
        /// A header band divided among an axis's levels. Each level's header cell starts one
        /// margin before its value does; the last runs to the end of the band. Where the
        /// cells do not give a consistent split, the band is shared equally.
        /// </summary>
        private static IEnumerable<int> SplitBand(int band, int bandStart, int count,
            List<CrossTabAxisLevel> levels, Func<ObjectBounds, int> edge, int margin, int fallback)
        {
            if (count == 0) return [];
            if (band <= 0) return Enumerable.Repeat(fallback, count);
            var starts = Enumerable.Range(1, count)
                .Select(k => levels.FirstOrDefault(l => l.Level == k)?.HeaderBounds is { } b ? edge(b) - margin : (int?)null)
                .ToList();
            if (count > 1 && starts.All(s => s is not null) && starts[0] == bandStart)
            {
                var sizes = starts.Select((s, k) => (k + 1 < count ? starts[k + 1]!.Value : bandStart + band) - s!.Value).ToList();
                if (sizes.All(s => s > 0)) return sizes;
            }
            return Enumerable.Repeat(Math.Max(1, band / count), count);
        }

        public ObjectFormat? SummaryFormat(int index) =>
            DataCell is { } d && index < d.SummaryFormats.Count ? d.SummaryFormats[index] : null;

        /// <summary>
        /// A cell's format with the grid's lines added - every edge, where the cell has no
        /// border of its own there - and the margin it insets its value by.
        /// </summary>
        public (ObjectFormat? Format, CellInset Inset) Style(ObjectFormat? format)
        {
            var inset = new CellInset(MarginLeft, MarginLeft, MarginTop, 0);
            if (!ShowGrid) return (format, inset);
            var f = format ?? new ObjectFormat();
            return (f with
            {
                BorderLeft = f.BorderLeft != 0 ? f.BorderLeft : (byte)1,
                BorderRight = f.BorderRight != 0 ? f.BorderRight : (byte)1,
                BorderTop = f.BorderTop != 0 ? f.BorderTop : (byte)1,
                BorderBottom = f.BorderBottom != 0 ? f.BorderBottom : (byte)1,
                BorderWidthTwips = f.BorderWidthTwips > 0 ? f.BorderWidthTwips : GridLineTwips,
            }, inset);
        }
    }

    private static string CellLabel(CrossTabCell cell) =>
        $"{RdlAggregateFunction(cell.Function)} of {NormalizeFieldName(cell.FieldName)}";

    // Chart with N dynamic category levels (outermost first, mirroring the multi-level
    // Matrix RowGroupings/ColumnGroupings convention — Chart builds an internal pseudo-
    // Matrix to compute its data, per the engine's own Chart.cs) and one series (the RDL
    // engine requires SeriesGroupings or CategoryGroupings, not both, for an unnamed single
    // series): one ChartData/ChartSeries/DataPoints/DataPoint/DataValues/DataValue/Value.
    // Schema confirmed against the Majorsilence.Reporting engine's own Chart/ChartData/
    // DynamicCategories definition source, not guessed.
    private void WriteChart(XmlWriter w, ChartObject chart, string? hiddenExpr, ReportDefinition? report,
        int leftOffsetTwips = 0, int topOffsetTwips = 0)
    {
        w.WriteStartElement("Chart", RdlNs);
        string name = SanitizeName(chart.Name.Length > 0 ? chart.Name : $"chart_{++_textboxCounter}");
        w.WriteAttributeString("Name", name);
        WriteObjectPosition(w, chart.Bounds, leftOffsetTwips, topOffsetTwips);
        WriteItemVisibility(w, hiddenExpr);
        WriteChartContent(w, chart, report, name);
        w.WriteEndElement(); // Chart
    }

    // Chart placed in a group header/footer row of a tabular report — same content as
    // WriteChart but without absolute position (the table grid positions the cell).
    private void WriteChartTableCell(XmlWriter w, ChartObject chart, ReportDefinition? report)
    {
        w.WriteStartElement("TableCell", RdlNs);
        w.WriteStartElement("ReportItems", RdlNs);
        w.WriteStartElement("Chart", RdlNs);
        string name = SanitizeName(chart.Name.Length > 0 ? chart.Name : $"chart_{++_textboxCounter}");
        w.WriteAttributeString("Name", name);
        WriteChartContent(w, chart, report, name);
        w.WriteEndElement(); // Chart
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // TableCell
    }

    // Crystal stores a chart's group-by/summary field under its *display* name, which for
    // a table-qualified field is "TableName ColumnName" separated by a space
    // ("Employee Last Name"), not the bare column the DataSet declares ("Last Name").
    // Sanitizing that whole string yields Fields!Employee_Last_Name, which no <Field>
    // ever matches — while every other reference to the same column in the same report
    // correctly reads Fields!Last_Name. Resolve back to the real column when the raw name
    // is a known table+column pair; a name that already matches a declared column (the
    // common case — "Order Amount") or that matches nothing is returned untouched.
    private static string ResolveDisplayFieldName(string rawName, ReportDefinition? report)
    {
        string name = NormalizeFieldName(rawName);
        if (report is null) return name;

        var dbFields = report.Fields.OfType<DatabaseField>().ToList();
        if (dbFields.Any(f => string.Equals(f.ColumnName, name, StringComparison.OrdinalIgnoreCase)))
            return name;

        return dbFields.FirstOrDefault(f =>
            f.TableName.Length > 0 &&
            string.Equals($"{f.TableName} {f.ColumnName}", name, StringComparison.OrdinalIgnoreCase))
            ?.ColumnName ?? name;
    }

    private void WriteChartContent(XmlWriter w, ChartObject chart, ReportDefinition? report, string chartName)
    {
        // Named, not left to default: a data region may omit its dataset only while the
        // report has exactly one. A subreport converted with the row sentinel has two, and
        // a chart there without a name is a compile error ("Chart1 must specify a
        // DataSetName") that left the subreport unbuilt and its RunPage throwing.
        w.WriteElementString("DataSetName", RdlNs, "DataSet1");
        string seriesField = SanitizeName(ResolveDisplayFieldName(chart.SeriesField, report));
        string valueExpr = BuildSummaryExpression(chart.SeriesFunction, seriesField);
        var levels = chart.CategoryFields
            .Select(f => SanitizeName(ResolveDisplayFieldName(f, report)))
            .ToList();
        var (categoryLevels, seriesLevel, subtype) = ChartGroupingLayout(chart, levels);

        // A chart over a top or bottom N group charts the groups the report prints: the
        // first N, in rank order. Top5USA-piechart's pie and Top5USAsubCanada's bars each
        // show exactly the report's five customers, largest first, in Crystal's renders;
        // without this they charted every customer in the data, in name order.
        var topN = report?.Groups.FirstOrDefault(g => g.TopN is not null && levels.Count > 0
            && string.Equals(SanitizeName(ResolveDisplayFieldName(g.FieldName, report)), levels[0],
                StringComparison.OrdinalIgnoreCase));
        string? rankExpr = topN is not null ? $"=Fields!{TopNRankField(topN.Level)}.Value" : null;
        if (topN?.TopN is { DiscardOthers: true } n)
        {
            w.WriteStartElement("Filters", RdlNs);
            w.WriteStartElement("Filter", RdlNs);
            string rank = $"Fields!{TopNRankField(topN.Level)}.Value";
            w.WriteElementString("FilterExpression", RdlNs, $"=IIf(IsNothing({rank}), 0, {rank})");
            w.WriteElementString("Operator", RdlNs, "LessThanOrEqual");
            w.WriteStartElement("FilterValues", RdlNs);
            w.WriteElementString("FilterValue", RdlNs, $"={n.Count}");
            w.WriteEndElement(); // FilterValues
            w.WriteEndElement(); // Filter
            w.WriteEndElement(); // Filters
        }
        // A level that is one of the report's groups on a date breaks where that group does,
        // on its interval ("for each month"), labelled with the group's name and sorted in
        // time: parking__orders charts six months, not thirty orders. It groups on the name
        // (one per interval) rather than the interval's start, because the engine labels a
        // category axis with the group's value, not its Label.
        // A chart with its own "on change of" interval (parking__chart_stock, by week) uses
        // that one.
        GroupDefinition? DateGroupFor(string field)
        {
            int level = levels.IndexOf(field);
            if (level >= 0 && level < chart.CategoryConditions.Count
                && chart.CategoryConditions[level] != GroupCondition.EachValue)
                return new GroupDefinition { FieldName = field, Condition = chart.CategoryConditions[level] };
            return report?.Groups.FirstOrDefault(g =>
                g.Condition != GroupCondition.EachValue
                && string.Equals(SanitizeName(ResolveDisplayFieldName(g.FieldName, report)), field,
                    StringComparison.OrdinalIgnoreCase));
        }
        string GroupExprFor(string field) => DateGroupFor(field) is { } g
            ? "=" + GroupNameExpression(g)
            : $"=Fields!{field}.Value";
        string LabelFor(string field) => GroupExprFor(field);
        string? SortFor(string field) => rankExpr is not null && field == levels[0] ? rankExpr
            : DateGroupFor(field) is { } g ? "=" + IntervalStart($"Fields!{field}.Value", g.Condition) : null;

        w.WriteElementString("Type", RdlNs, chart.Kind switch
        {
            ChartKind.Pie => "Pie",
            ChartKind.Doughnut => "Doughnut",
            ChartKind.Bar => "Bar",
            ChartKind.Line => "Line",
            ChartKind.Area => "Area",
            _ => "Column"
        });
        if (subtype is not null)
            w.WriteElementString("Subtype", RdlNs, subtype);
        if (chart.Title.Length > 0)
        {
            w.WriteStartElement("Title", RdlNs);
            w.WriteElementString("Caption", RdlNs, chart.Title);
            WriteChartFontStyle(w, chart.TitleFont);
            w.WriteEndElement(); // Title
        }

        // The engine draws a legend only for a chart with series groupings, and lists the
        // series; that is the list Crystal draws too, whether its entries are the series
        // or, for a chart coloured by category, the categories.
        if (chart.ShowLegend && seriesLevel is not null)
        {
            w.WriteStartElement("Legend", RdlNs);
            w.WriteElementString("Visible", RdlNs, "true");
            w.WriteElementString("Position", RdlNs, chart.LegendPosition switch
            {
                ChartLegendPosition.Left => "LeftCenter",
                ChartLegendPosition.Bottom => "BottomCenter",
                _ => "RightCenter"
            });
            w.WriteElementString("Layout", RdlNs, "Column");
            // Crystal frames its legend with a thin line, in both the references.
            WriteChartFontStyle(w, chart.LegendFont, border: true);
            w.WriteEndElement(); // Legend
        }

        if (categoryLevels.Count > 0)
        {
            w.WriteStartElement("CategoryGroupings", RdlNs);
            foreach (string categoryField in categoryLevels)
            {
                w.WriteStartElement("CategoryGrouping", RdlNs);
                w.WriteStartElement("DynamicCategories", RdlNs);
                WriteChartGrouping(w, $"{chartName}_Category_{categoryField}", GroupExprFor(categoryField), SortFor(categoryField));
                w.WriteElementString("Label", RdlNs, LabelFor(categoryField));
                w.WriteEndElement(); // DynamicCategories
                w.WriteEndElement(); // CategoryGrouping
            }
            w.WriteEndElement(); // CategoryGroupings
        }

        if (seriesLevel is not null)
        {
            w.WriteStartElement("SeriesGroupings", RdlNs);
            w.WriteStartElement("SeriesGrouping", RdlNs);
            w.WriteStartElement("DynamicSeries", RdlNs);
            WriteChartGrouping(w, $"{chartName}_Series_{seriesLevel}", GroupExprFor(seriesLevel), SortFor(seriesLevel));
            w.WriteElementString("Label", RdlNs, LabelFor(seriesLevel));
            w.WriteEndElement(); // DynamicSeries
            w.WriteEndElement(); // SeriesGrouping
            w.WriteEndElement(); // SeriesGroupings
        }

        if (chart.Kind is not (ChartKind.Pie or ChartKind.Doughnut))
        {
            WriteChartAxis(w, "CategoryAxis", chart.CategoryAxisTitle, chart.CategoryAxisTitleFont,
                chart.CategoryLabelFont, chart.CategoryAxisGridLines, verticalTitle: false);
            WriteChartAxis(w, "ValueAxis", chart.ValueAxisTitle, chart.ValueAxisTitleFont,
                chart.ValueLabelFont, chart.ValueAxisGridLines, verticalTitle: true);

            // Crystal frames the plot area of every axis chart in the references.
            w.WriteStartElement("PlotArea", RdlNs);
            w.WriteStartElement("Style", RdlNs);
            WriteThinBorder(w);
            w.WriteEndElement(); // Style
            w.WriteEndElement(); // PlotArea
        }
        else if (chart.Kind == ChartKind.Doughnut)
        {
            // The engine draws a doughnut's hole in the plot area's style, and only when it
            // has one; without it the slices met in the middle as a pie's do.
            w.WriteStartElement("PlotArea", RdlNs);
            w.WriteStartElement("Style", RdlNs);
            w.WriteElementString("BackgroundColor", RdlNs, "White");
            w.WriteEndElement(); // Style
            w.WriteEndElement(); // PlotArea
        }

        w.WriteStartElement("ChartData", RdlNs);
        w.WriteStartElement("ChartSeries", RdlNs);
        w.WriteStartElement("DataPoints", RdlNs);
        w.WriteStartElement("DataPoint", RdlNs);
        w.WriteStartElement("DataValues", RdlNs);
        w.WriteStartElement("DataValue", RdlNs);
        w.WriteElementString("Value", RdlNs, valueExpr);
        w.WriteEndElement(); // DataValue
        w.WriteEndElement(); // DataValues
        w.WriteEndElement(); // DataPoint
        w.WriteEndElement(); // DataPoints
        w.WriteEndElement(); // ChartSeries
        w.WriteEndElement(); // ChartData
    }

    /// <summary>
    /// Which of a chart's group-by fields become RDL categories and which its series, and
    /// the RDL subtype that draws them as Crystal does.
    /// <para>The engine draws a pie per category, with the series as its slices (ChartPie
    /// loops categories, then series), where Crystal draws one pie whose slices are the
    /// groups. So a pie's innermost group-by becomes the series, and any outer ones remain
    /// categories: Crystal's "multiple pies", one per outer group.</para>
    /// <para>A one-series bar chart in Crystal's current template gives each category a
    /// colour of its own and lists the categories in its legend (Canada-CrossTab's reference;
    /// the template's fills are the colours its export draws, one per bar). The engine
    /// colours series, not categories, and draws a legend only of series, so the category
    /// field is used as the series as well, stacked: each category then holds exactly one
    /// non-empty series, drawn at full width in that series' colour, under its own label.</para>
    /// </summary>
    private static (List<string> Categories, string? Series, string? Subtype) ChartGroupingLayout(
        ChartObject chart, List<string> levels)
    {
        if (levels.Count == 0)
            return ([], null, null);

        if (chart.Kind is ChartKind.Pie or ChartKind.Doughnut)
            return (levels.Take(levels.Count - 1).ToList(), levels[^1], null);

        string stacking = chart.Stacking switch
        {
            ChartStacking.Stacked => "Stacked",
            ChartStacking.PercentStacked => "PercentStacked",
            _ => "Plain"
        };
        if (chart.Kind is not (ChartKind.Column or ChartKind.Bar or ChartKind.Area))
            return (levels, null, null);

        if (chart.ColorByCategory && levels.Count == 1 && chart.Kind is ChartKind.Column or ChartKind.Bar)
            return (levels, levels[0], stacking == "PercentStacked" ? stacking : "Stacked");

        return (levels, null, stacking);
    }

    private static void WriteChartGrouping(XmlWriter w, string name, string groupExpr, string? sortExpr = null)
    {
        // Named per chart: a report with two charts over the same field (Top5USA-piechart's
        // bar and pie) otherwise declares the same grouping name twice, which the engine
        // rejects outright ("Duplicate Grouping name").
        w.WriteStartElement("Grouping", RdlNs);
        w.WriteAttributeString("Name", name);
        w.WriteStartElement("GroupExpressions", RdlNs);
        w.WriteElementString("GroupExpression", RdlNs, groupExpr);
        w.WriteEndElement(); // GroupExpressions
        w.WriteEndElement(); // Grouping
        if (sortExpr is not null)
        {
            w.WriteStartElement("Sorting", RdlNs);
            w.WriteStartElement("SortBy", RdlNs);
            w.WriteElementString("SortExpression", RdlNs, sortExpr);
            w.WriteEndElement(); // SortBy
            w.WriteEndElement(); // Sorting
        }
    }

    // An axis with Crystal's title, label size and gridlines. Without an axis element the
    // engine draws no axis at all: no labels, no scale, no title.
    private static void WriteChartAxis(XmlWriter w, string element, string title, ChartFont titleFont,
        ChartFont labelFont, bool gridLines, bool verticalTitle)
    {
        w.WriteStartElement(element, RdlNs);
        w.WriteStartElement("Axis", RdlNs);
        w.WriteElementString("Visible", RdlNs, "true");
        WriteChartFontStyle(w, labelFont);
        if (title.Length > 0)
        {
            w.WriteStartElement("Title", RdlNs);
            w.WriteElementString("Caption", RdlNs, title);
            // Crystal turns the value-axis title to run up the axis. The engine can turn
            // text only to run down (tb-rl); that is the nearest it has.
            WriteChartFontStyle(w, titleFont, writingMode: verticalTitle ? "tb-rl" : null);
            w.WriteEndElement(); // Title
        }
        if (gridLines)
        {
            w.WriteStartElement("MajorGridLines", RdlNs);
            w.WriteElementString("ShowGridLines", RdlNs, "true");
            w.WriteStartElement("Style", RdlNs);
            WriteThinBorder(w);
            w.WriteEndElement(); // Style
            w.WriteEndElement(); // MajorGridLines
        }
        w.WriteEndElement(); // Axis
        w.WriteEndElement(); // element
    }

    private static void WriteChartFontStyle(XmlWriter w, ChartFont font, bool border = false,
        string? writingMode = null)
    {
        w.WriteStartElement("Style", RdlNs);
        if (border)
            WriteThinBorder(w);
        if (!string.IsNullOrEmpty(font.Name))
            w.WriteElementString("FontFamily", RdlNs, font.Name);
        if (font.SizePt is double size && size > 0)
            w.WriteElementString("FontSize", RdlNs,
                size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "pt");
        if (font.Bold)
            w.WriteElementString("FontWeight", RdlNs, "Bold");
        if (font.Italic)
            w.WriteElementString("FontStyle", RdlNs, "Italic");
        if (writingMode is not null)
            w.WriteElementString("WritingMode", RdlNs, writingMode);
        w.WriteEndElement(); // Style
    }

    private static void WriteThinBorder(XmlWriter w)
    {
        w.WriteStartElement("BorderStyle", RdlNs);
        w.WriteElementString("Default", RdlNs, "Solid");
        w.WriteEndElement();
        w.WriteStartElement("BorderColor", RdlNs);
        w.WriteElementString("Default", RdlNs, "Black");
        w.WriteEndElement();
        w.WriteStartElement("BorderWidth", RdlNs);
        w.WriteElementString("Default", RdlNs, "0.5pt");
        w.WriteEndElement();
    }

    // A Matrix cell's textbox, styled as the cross-tab draws that cell: its own format, the
    // grid's lines and the cell margin (see CrossTabGrid.Style).
    private void WriteMatrixTextbox(XmlWriter w, string value, ObjectFormat? format, CrossTabGrid grid)
    {
        w.WriteStartElement("Textbox", RdlNs);
        w.WriteAttributeString("Name", $"Textbox_{++_textboxCounter}");
        w.WriteElementString("Value", RdlNs, value);
        var (style, inset) = grid.Style(format);
        WriteObjectStyle(w, style, value, inset);
        w.WriteEndElement();
    }

    private static void WriteItemVisibility(XmlWriter w, string? hiddenExpr)
    {
        if (hiddenExpr is null) return;
        w.WriteStartElement("Visibility", RdlNs);
        w.WriteElementString("Hidden", RdlNs, hiddenExpr);
        w.WriteEndElement();
    }

    // Emit a TableRow <Visibility> from the section's suppression. The suppress
    // formula supersedes the static checkbox when both are present (Crystal keeps
    // the stale static bit set alongside an attached formula).
    private static void WriteRowVisibility(XmlWriter w, Section section, ReportDefinition? report)
    {
        string? expr = TranspileSuppressFormula(section.SuppressFormula, report)
                       ?? (section.Suppress ? "true" : null);
        if (expr is null) return;
        w.WriteStartElement("Visibility", RdlNs);
        w.WriteElementString("Hidden", RdlNs, expr);
        w.WriteEndElement();
    }

    // Transpile a Crystal suppress formula into an RDL Hidden expression.
    // Returns null when there is no formula or it cannot be transpiled
    // (variable-based formulas fall back to "" — never hide on those).
    /// <summary>
    /// An object's own Suppress formula as a Hidden expression, null when it has none or it
    /// does not translate. CurrentFieldValue is the object's own value: JournalEntry's debit
    /// and credit columns are suppressed where "currentfieldvalue = 0". Only a placed field
    /// has one this can name; for a summary or a text object the formula is left out, and
    /// the object prints, as all of them did.
    /// </summary>
    private static string? ObjectSuppressExpression(ReportObject obj, ReportDefinition? report)
    {
        if (string.IsNullOrWhiteSpace(obj.SuppressFormula)) return null;
        string text = obj.SuppressFormula;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bCurrentFieldValue\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (obj is not FieldObject { SummaryFunction: null } field) return null;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\bCurrentFieldValue\b",
                "{" + field.FieldName.Trim('{', '}') + "}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return TranspileSectionFormula(text, "ObjectSuppress", report);
    }

    // Hidden where either is.
    private static string? EitherHidden(string? a, string? b) => (a, b) switch
    {
        (null or "false", _) => b,
        (_, null or "false") => a,
        ("true", _) or (_, "true") => "true",
        _ => $"=({a![1..]}) OrElse ({b![1..]})",
    };

    private static string? TranspileSuppressFormula(string? crystalFormula, ReportDefinition? report = null) =>
        TranspileSectionFormula(crystalFormula, "SectionSuppress", report);

    private static string? TranspileNewPageBeforeFormula(string? crystalFormula, ReportDefinition? report = null) =>
        TranspileSectionFormula(crystalFormula, "SectionNewPageBefore", report);

    private static string? TranspileNewPageAfterFormula(string? crystalFormula, ReportDefinition? report = null) =>
        TranspileSectionFormula(crystalFormula, "SectionNewPageAfter", report);

    private static string? TranspileBackColorFormula(string? crystalFormula, ReportDefinition? report = null) =>
        TranspileSectionFormula(crystalFormula, "SectionBackColor", report);

    /// <summary>
    /// Rewrites a reference to Crystal's built-in "Group #N Name" field into the group's
    /// own field. Those are not columns and no DataSet declares them, so left as written
    /// the engine rejects the whole expression with "Field 'Group__N_Name' not found" -
    /// fatal when the expression is a table's visibility. The placed-object paths already
    /// resolve these through their own lookup; section formulas are transpiled straight
    /// from Crystal text and so need the same substitution applied afterwards.
    /// </summary>
    private static string ResolveGroupNameFields(string expr, ReportDefinition? report)
    {
        if (report is null || report.Groups.Count == 0
            || !expr.Contains("Group__", StringComparison.OrdinalIgnoreCase))
            return expr;

        return GroupNameFieldReference.Replace(expr, m =>
            int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= report.Groups.Count
                ? GroupNameExpression(report.Groups[n - 1])
                : m.Value);
    }

    /// <summary>The sanitised form of Crystal's "Group #N Name" special field.</summary>
    private static readonly System.Text.RegularExpressions.Regex GroupNameFieldReference =
        new(@"Fields!Group__(\d+)_Name\.Value",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string? TranspileSectionFormula(string? crystalFormula, string debugName,
        ReportDefinition? report = null)
    {
        if (string.IsNullOrWhiteSpace(crystalFormula)) return null;
        string expr = FormulaTranspiler.ToRdlExpression(new FormulaField
        {
            Name = debugName,
            FormulaText = crystalFormula,
            Syntax = FormulaSyntax.Crystal
        });
        expr = ResolveGroupNameFields(expr, report);
        return string.IsNullOrWhiteSpace(expr) || expr is "=\"\"" or "=" ? null : expr;
    }

    // Crystal summary function → SSRS/RDL aggregate function name
    private static string RdlAggregateFunction(AggregateFunction? fn) => fn switch
    {
        AggregateFunction.Count => "Count",
        AggregateFunction.DistinctCount => "CountDistinct",
        AggregateFunction.Average => "Avg",
        AggregateFunction.Maximum => "Max",
        AggregateFunction.Minimum => "Min",
        AggregateFunction.StandardDeviation => "StDev",
        AggregateFunction.Variance => "Var",
        AggregateFunction.Percentage => "Percentage",   // label only — see BuildSummaryExpression for the value
        _ => "Sum"   // null (plain numeric column heuristic) and Sum
    };

    // Full "=..." value expression for a (possibly summarized) field. Percentage is not
    // a single RDL function — so it needs its own two-part expression rather than a simple
    // "=Func(...)" wrap. Sum() with no explicit scope auto-scopes to the enclosing table
    // group when nested in one, and is divided by the sum over the scope it is a share of:
    // the grouping Crystal's "Percentage of" names (percentageScope, see PercentageScope),
    // or the whole DataSet for the grand total.
    private string BuildSummaryExpression(AggregateFunction? fn, string sanitizedFieldRef, string? percentageScope = null) => fn switch
    {
        null when _footerFormulaExpressions.TryGetValue(sanitizedFieldRef, out var footer) => footer,
        null => $"=Fields!{sanitizedFieldRef}.Value",
        AggregateFunction.Percentage =>
            $"=Sum(Fields!{sanitizedFieldRef}.Value) / Sum(Fields!{sanitizedFieldRef}.Value, \"{percentageScope ?? "DataSet1"}\") * 100",
        _ => $"={RdlAggregateFunction(fn)}(Fields!{sanitizedFieldRef}.Value)"
    };

    // The grouping a percentage written in group level ownLevel divides by: the group Crystal
    // says it is a share of (FieldObject.PercentageOfGroupLevel), when that group encloses
    // this one and so is in scope here; null for the grand total. pct_base_innergroup's city
    // percentages are shares of their country, and printed 6.48% for Crystal's 27.33%.
    private static string? PercentageScope(FieldObject field, int ownLevel) =>
        field.PercentageOfGroupLevel is int level && level < ownLevel ? $"Group{level + 1}" : null;

    private static bool IsNumericType(string? dataType) => dataType switch
    {
        "Float64" or "Float32" or "Decimal" or "Currency" or
        "Integer" or "Int16" or "Int32" or "Int64" => true,
        _ => false
    };

    /// <summary>
    /// A twip measurement, in points, exactly.
    ///
    /// This used to emit inches to three decimal places, and a thousandth of an inch is
    /// 0.3px at 300dpi - so every dimension lost up to half a pixel and, on a table row,
    /// that loss is a pitch rather than a one-off. Country-Region-Sort's rows start aligned
    /// with the real engine's and end 13px above them by the bottom of the page, from a
    /// per-row error of about a quarter of a pixel.
    ///
    /// Inches cannot fix this by adding digits: a twip is 1/1440in, whose decimal expansion
    /// repeats, so no finite number of decimal places is exact. Points can - a twip is
    /// exactly 0.05pt, so twips/20 always terminates within two decimal places and the
    /// conversion is lossless for every integer input.
    /// </summary>
    private static string TwipsToRdl(int twips) => $"{twips / 20.0:0.##}pt";

    private static string MapDataSourceKind(DataSourceKind kind) => kind switch
    {
        DataSourceKind.Odbc => "ODBC",
        DataSourceKind.OleDb => "OLEDB",
        DataSourceKind.Native => "SQL",
        DataSourceKind.Dataset => "SQL",
        _ => "SQL"
    };

    /// <summary>
    /// The column holding a top or bottom N group's rank: 1 for the group the summary ranks
    /// first (highest for a top N, lowest for a bottom N), within the groups above it.
    /// </summary>
    public static string TopNRankField(int groupLevel) => $"CrystalTopNRank{groupLevel}";

    // The details table keeps a top or bottom N group's first N instances: a filter on the
    // data region, which this engine applies (a group's own Filters it parses and ignores).
    // The rows past N stay in the dataset, so a summary scoped to it, as a percentage of the
    // grand total is, still counts them, as Crystal's does. An empty rank passes, so a
    // renderer that adds no rank column prints every group.
    private static void WriteTopNFilters(XmlWriter w, ReportDefinition report)
    {
        var topN = report.Groups.Where(g => g.TopN is { DiscardOthers: true }).ToList();
        if (topN.Count == 0) return;
        w.WriteStartElement("Filters", RdlNs);
        foreach (var g in topN)
        {
            string rank = $"Fields!{TopNRankField(g.Level)}.Value";
            w.WriteStartElement("Filter", RdlNs);
            w.WriteElementString("FilterExpression", RdlNs, $"=IIf(IsNothing({rank}), 0, {rank})");
            w.WriteElementString("Operator", RdlNs, "LessThanOrEqual");
            w.WriteStartElement("FilterValues", RdlNs);
            w.WriteElementString("FilterValue", RdlNs, $"={g.TopN!.Count}");
            w.WriteEndElement(); // FilterValues
            w.WriteEndElement(); // Filter
        }
        w.WriteEndElement(); // Filters
    }

    /// <summary>The column a database field is read from in the pushed table: "Table.Column" when its table is known.</summary>
    internal static string QualifiedDataField(DatabaseField f) =>
        string.IsNullOrEmpty(f.TableName) ? f.ColumnName : $"{f.TableName}.{f.ColumnName}";

    // The query a report's own tables and links describe: every column of every table, the
    // tables joined as the links say, in link order. A link whose tables are both already in
    // the FROM clause adds its condition to the join that brought the second one in; a table
    // no link reaches is cross-joined, as Crystal queries it.
    private static string BuildSelectFromTables(DataSource ds, List<TableLink> links)
    {
        if (ds.Tables.Count == 0) return "SELECT * FROM <TableName>";
        var cols = ds.Tables
            .SelectMany(t => t.Columns.Select(c => $"{Bracket(t.Alias)}.{Bracket(c.Name)}"))
            .ToList();
        string select = cols.Count > 0 ? string.Join(", ", cols) : "*";

        string TableRef(TableDefinition t) =>
            t.Alias.Length > 0 && t.Alias != t.Name ? $"{Bracket(t.Name)} {Bracket(t.Alias)}" : Bracket(t.Name);
        string Alias(TableDefinition t) => t.Alias.Length > 0 ? t.Alias : t.Name;
        string Condition(TableLink l) =>
            $"{Bracket(l.SourceTable)}.{Bracket(l.SourceColumn)} {OperatorSql(l.Operator)} {Bracket(l.TargetTable)}.{Bracket(l.TargetColumn)}";

        var placed = new List<string>();                 // aliases in the FROM clause, in order
        var clauses = new List<string>();                // one entry per table placed: "name alias" or "JOIN name alias ON ..."
        var joinOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // alias -> clause index that joined it
        var where = new List<string>();
        var pending = links.ToList();

        void Place(TableDefinition t) { placed.Add(Alias(t)); joinOf[Alias(t)] = clauses.Count; clauses.Add(TableRef(t)); }
        TableDefinition? ByAlias(string alias) => ds.Tables.FirstOrDefault(t => string.Equals(Alias(t), alias, StringComparison.OrdinalIgnoreCase));

        Place(ds.Tables[0]);
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var link in pending.ToList())
            {
                bool sourceIn = placed.Contains(link.SourceTable, StringComparer.OrdinalIgnoreCase);
                bool targetIn = placed.Contains(link.TargetTable, StringComparer.OrdinalIgnoreCase);
                if (!sourceIn && !targetIn) continue;
                pending.Remove(link);
                progress = true;
                if (sourceIn && targetIn)
                {
                    // Both already in: the condition belongs to the join that brought the
                    // later one in, or to the WHERE clause when that was a cross join.
                    int idx = Math.Max(joinOf[link.SourceTable], joinOf[link.TargetTable]);
                    if (clauses[idx].Contains(" ON ", StringComparison.Ordinal))
                        clauses[idx] = $"{clauses[idx]} AND {Condition(link)}";
                    else
                        where.Add(Condition(link));
                    continue;
                }
                var joining = ByAlias(sourceIn ? link.TargetTable : link.SourceTable);
                if (joining is null) continue;
                // A left outer join written from the source's side; adding the source to a
                // FROM that already holds the target makes it a right outer join, and so on.
                string joinWord = (link.JoinType, sourceIn) switch
                {
                    (TableJoinType.LeftOuter, true) or (TableJoinType.RightOuter, false) => "LEFT OUTER JOIN",
                    (TableJoinType.RightOuter, true) or (TableJoinType.LeftOuter, false) => "RIGHT OUTER JOIN",
                    (TableJoinType.FullOuter, _) => "FULL OUTER JOIN",
                    _ => "INNER JOIN"
                };
                placed.Add(Alias(joining));
                joinOf[Alias(joining)] = clauses.Count;
                clauses.Add($"{joinWord} {TableRef(joining)} ON {Condition(link)}");
            }
            if (!progress)
            {
                // No link reaches the tables left; cross-join the next and carry on.
                var next = ds.Tables.FirstOrDefault(t => !placed.Contains(Alias(t), StringComparer.OrdinalIgnoreCase));
                if (next is null) break;
                Place(next);
                progress = true;
            }
        }

        var from = new System.Text.StringBuilder();
        for (int i = 0; i < clauses.Count; i++)
        {
            if (i == 0) from.Append(clauses[i]);
            else if (clauses[i].Contains(" JOIN ", StringComparison.Ordinal)) from.Append(' ').Append(clauses[i]);
            else from.Append(", ").Append(clauses[i]);
        }
        string tail = where.Count > 0 ? $" WHERE {string.Join(" AND ", where)}" : string.Empty;
        return $"SELECT {select} FROM {from}{tail}";
    }

    private static string Bracket(string name) => $"[{name}]";

    private static string OperatorSql(LinkOperator op) => op switch
    {
        LinkOperator.GreaterThan => ">",
        LinkOperator.LessThan => "<",
        LinkOperator.GreaterOrEqual => ">=",
        LinkOperator.LessOrEqual => "<=",
        LinkOperator.NotEqual => "<>",
        _ => "="
    };

    // Build SELECT from DatabaseField metadata when QESession tables aren't available.
    private static string BuildSelectFromFields(List<DatabaseField> dbFields)
    {
        if (dbFields.Count == 0) return "SELECT * FROM <TableName>";
        var tables = dbFields
            .Where(f => !string.IsNullOrEmpty(f.TableName))
            .Select(f => f.TableName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tables.Count == 0) return "SELECT * FROM <TableName>";
        string select = string.Join(", ", dbFields
            .Where(f => !string.IsNullOrEmpty(f.ColumnName))
            .Select(f => string.IsNullOrEmpty(f.TableName)
                ? $"[{f.ColumnName}]"
                : $"[{f.TableName}].[{f.ColumnName}]"));
        string from = string.Join(", ", tables.Select(t => $"[{t}]"));
        return $"SELECT {select} FROM {from}";
    }

    // Maps RptParser's DatabaseField.DataType strings (RptParser.MapCrValueType — Crystal
    // field-type codes) to the TypeName strings the engine's DataType.GetStyle actually
    // recognizes. Most already match verbatim; only the three renamed here don't. Returns
    // null for "String" (Field.Type already defaults to String, so it's not worth a line).
    private static string? RdlFieldTypeName(string crystalDataType) => crystalDataType switch
    {
        "Float32" => "Single",
        "Float64" => "Double",
        "Currency" => "Decimal",
        "String" => null,
        _ => crystalDataType,   // Boolean, Int16, Int32, DateTime already match as-is
    };

    /// <summary>
    /// Which detail column an object at this horizontal position belongs to: the last
    /// column that starts at or before it. Anything left of the first column belongs to
    /// the first, so an object indented differently from the detail row it labels still
    /// lands in the right cell. With no column starts to compare against, everything
    /// reports column 0, which reproduces the declaration-order behaviour this replaced.
    /// </summary>
    private static int ColumnIndexForLeft(int left, List<int> columnStarts)
    {
        int index = 0;
        for (int i = 0; i < columnStarts.Count; i++)
            if (left >= columnStarts[i]) index = i;
        return index;
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Item1";
        string s = System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
        return char.IsDigit(s[0]) ? "_" + s : s;
    }

    // A group's grouping and sort expression: its field, or for a group on a date the start
    // of the interval the record falls in, a date itself so that the groups sort in time.
    private static string DatePartGroupExpression(string fieldRef, GroupCondition cond) =>
        "=" + IntervalStart(fieldRef, cond);

    /// <summary>
    /// The start of the interval a date falls in, as Crystal groups by it: the day; the
    /// week from its Sunday; two weeks, in fourteen-day periods counted from Sunday
    /// 0001-01-14 (so 2023-12-24 and 2024-01-07 start one);
    /// the 1st or the 16th; the month, quarter, half year or year from its first month;
    /// the hour, minute or second; or midnight or noon. Read from Crystal's own exports of
    /// the same report grouped each way.
    /// </summary>
    private static string IntervalStart(string f, GroupCondition cond)
    {
        string day = $"DateSerial(Year({f}), Month({f}), Day({f}))";
        return cond switch
        {
            GroupCondition.Daily => day,
            GroupCondition.Weekly => $"DateAdd(\"d\", 1 - Weekday({f}), {day})",
            GroupCondition.Biweekly =>
                $"DateAdd(\"d\", -((DateDiff(\"d\", DateSerial(1, 1, 1), {day}) + 1) Mod 14), {day})",
            GroupCondition.Semimonthly => $"DateSerial(Year({f}), Month({f}), IIf(Day({f}) < 16, 1, 16))",
            GroupCondition.Monthly => $"DateSerial(Year({f}), Month({f}), 1)",
            GroupCondition.Quarterly => $"DateSerial(Year({f}), Month({f}) - ((Month({f}) - 1) Mod 3), 1)",
            GroupCondition.Semiannually => $"DateSerial(Year({f}), IIf(Month({f}) < 7, 1, 7), 1)",
            GroupCondition.Annually => $"DateSerial(Year({f}), 1, 1)",
            GroupCondition.ByHour => $"DateAdd(\"h\", DatePart(\"h\", {f}), {day})",
            GroupCondition.ByMinute =>
                $"DateAdd(\"n\", DatePart(\"h\", {f}) * 60 + DatePart(\"n\", {f}), {day})",
            GroupCondition.BySecond =>
                $"DateAdd(\"s\", (DatePart(\"h\", {f}) * 60 + DatePart(\"n\", {f})) * 60 + DatePart(\"s\", {f}), {day})",
            GroupCondition.ByAmPm => $"DateAdd(\"h\", IIf(DatePart(\"h\", {f}) < 12, 0, 12), {day})",
            _ => f,
        };
    }

    /// <summary>
    /// What Crystal's "Group #N Name" prints for a group: its field, or for a group on a date
    /// the interval's start, in the form Crystal prints it. A day-based interval prints the
    /// machine's short date ("2024-01-03"); a month-based one the year and month ("2024-01");
    /// a year the year; a time-based one the date and the time on a 12-hour clock with no
    /// space before AM or PM ("2024-01-03 9:00AM"), and AM/PM groups 12:00AM or 12:00PM.
    /// An expression without its leading "=".
    /// </summary>
    private static string GroupNameExpression(GroupDefinition group)
    {
        string f = $"Fields!{SanitizeName(NormalizeFieldName(group.FieldName))}.Value";
        if (group.Condition == GroupCondition.EachValue)
            return f;
        string v = IntervalStart(f, group.Condition);
        string date = $"Format({v}, \"d\")";
        string ampm = $"IIf(DatePart(\"h\", {f}) < 12, \"AM\", \"PM\")";
        return group.Condition switch
        {
            GroupCondition.Monthly or GroupCondition.Quarterly or GroupCondition.Semiannually
                => $"Format({v}, \"yyyy-MM\")",
            GroupCondition.Annually => $"Format({v}, \"yyyy\")",
            // "%h": a lone "h" would be read as a standard format, which there is none of.
            GroupCondition.ByHour => $"{date} & \" \" & Format({v}, \"%h\") & \":00\" & {ampm}",
            GroupCondition.ByMinute => $"{date} & \" \" & Format({v}, \"h:mm\") & {ampm}",
            GroupCondition.BySecond => $"{date} & \" \" & Format({v}, \"h:mm:ss\") & {ampm}",
            GroupCondition.ByAmPm => $"{date} & \" 12:00\" & {ampm}",
            _ => date,
        };
    }

    // Strip Crystal's field-type prefix (@=formula, #=running-total) so the sanitized
    // result matches the DataSet Field name (which is built from the bare name).
    private static string NormalizeFieldName(string name) =>
        name.Length > 1 && (name[0] == '@' || name[0] == '#') ? name[1..] : name;

    // Map Crystal special field display names to SSRS RDL global expressions.
    // reportComments: value from OLE SummaryInformation, embedded as literal when "report comments" is referenced.
    // Returns null for unknown special fields (will render as [Name] placeholder).
    private static string? SpecialFieldExpression(string fieldName, string? reportComments = null, string? reportTitle = null) =>
        fieldName.ToLowerInvariant() switch
        {
            "page number"          => "=Globals!PageNumber",
            "total page count"     => "=Globals!TotalPages",
            "page n of m"          => "=\"Page \" & Globals!PageNumber & \" of \" & Globals!TotalPages",
            "print date"           => "=Format(Globals!ExecutionTime, \"d\")",
            "print time"           => "=Format(Globals!ExecutionTime, \"T\")",
            "modification date"    => "=Format(Globals!ExecutionTime, \"d\")",
            "modification time"    => "=Format(Globals!ExecutionTime, \"T\")",
            // When the data was read: for a report rendered from the data it is given, now.
            // Unmapped they printed as "[Data Date]" and "[Data Time]".
            "data date"            => "=Format(Globals!ExecutionTime, \"d\")",
            "data time"            => "=Format(Globals!ExecutionTime, \"T\")",
            // Crystal's title lives in OLE SummaryInformation (ReportDefinition.ReportTitle).
            // A report that has none prints nothing there, as Crystal does. It printed
            // Globals!ReportName, the RDL's own internal name: "Item1" over a property list
            // whose title Crystal leaves blank.
            "report title"         => string.IsNullOrEmpty(reportTitle)
                                        ? "=\"\""
                                        : $"={QuoteLiteral(reportTitle)}",
            "record number"        => "=RowNumber(Nothing)",
            "report comments"      => string.IsNullOrEmpty(reportComments)
                                        // Empty when there are none. As an expression: without the
                                        // "=" the engine printed the two quote marks themselves.
                                        ? "=\"\""
                                        : $"={QuoteLiteral(reportComments)}",
            _                      => null,
        };

    // "<Function> of <field>" as the aggregate over the field, where the field is one the
    // DataSet has; null otherwise. The scope is the one the text is in. A summary embedded in
    // a text object, as placed ones are read: "{Sum of @total}" in a tax agreement's
    // sentence printed itself.
    private static string? EmbeddedSummary(string reference, Dictionary<string, string> knownFields)
    {
        int of = reference.IndexOf(" of ", StringComparison.Ordinal);
        if (of <= 0) return null;
        AggregateFunction? fn = reference[..of] switch
        {
            "Sum" => AggregateFunction.Sum,
            "Count" => AggregateFunction.Count,
            "DistinctCount" or "Distinct Count" => AggregateFunction.DistinctCount,
            "Average" or "Avg" => AggregateFunction.Average,
            "Maximum" or "Max" => AggregateFunction.Maximum,
            "Minimum" or "Min" => AggregateFunction.Minimum,
            _ => null,
        };
        if (fn is null) return null;
        string field = NormalizeFieldName(reference[(of + 4)..].Trim());
        int dot = field.IndexOf('.');
        string bare = dot >= 0 ? field[(dot + 1)..] : field;
        string? real = knownFields.TryGetValue(field, out var f) ? f : knownFields.TryGetValue(bare, out var b) ? b : null;
        return real is null ? null : $"{RdlAggregateFunction(fn)}(Fields!{SanitizeName(real)}.Value)";
    }

    // Convert Crystal text with embedded {FieldName} field references to an RDL expression.
    // Pure literal text (no braces) is returned as-is.
    // Mixed text like "Total for {Customer Name}:" becomes:
    //   ="Total for " & Fields!Customer_Name.Value & ":"
    // A value in an embedded field's own format, where it sets one.
    private static string Formatted(string expr, string? format) =>
        format is null ? expr : $"Format({expr}, {QuoteLiteral(format)})";

    // A field or parameter embedded in a text object that has no value prints nothing, and
    // the text around it still prints, as in Crystal. Joined as it is, a null makes the
    // engine's "&" give null for the whole text: "Account: {@Code}" printed nothing at all on
    // a letter with no record. A reference standing alone keeps its value's type, for the
    // textbox's number format.
    private static string BlankWhenNull(string expr, string? format, bool joined) =>
        joined ? $"IIf(IsNothing({expr}), \"\", {Formatted(expr, format)})" : Formatted(expr, format);

    /// <summary>
    /// The format of a field embedded in a text object, for a date field or parameter that
    /// sets one; null for anything else, since a date format given anything but a date
    /// prints the format itself.
    /// </summary>
    private static Func<string, string?> EmbeddedDateFormats(TextObject text, ReportDefinition? report) => reference =>
    {
        if (report is null || !text.FieldFormats.TryGetValue(reference, out var format)) return null;
        string bare = reference.Trim().TrimStart('?', '@');
        int dot = bare.IndexOf('.');
        string column = dot >= 0 ? bare[(dot + 1)..] : bare;
        bool isDate = report.Fields.Any(f => f switch
        {
            DatabaseField db => string.Equals(db.ColumnName, column, StringComparison.OrdinalIgnoreCase)
                && db.DataType is "DateTime" or "Date",
            ParameterField p => string.Equals(p.Name.TrimStart('?'), bare, StringComparison.OrdinalIgnoreCase)
                && p.DataType is "DateTime" or "Date",
            FormulaField fo => string.Equals(fo.Name, bare, StringComparison.OrdinalIgnoreCase)
                && fo.ResultType is "DateTime" or "Date",
            _ => false,
        });
        return isDate ? format : null;
    };

    private static string ResolveTextWithFieldRefs(string text, Dictionary<string, string> knownFields,
        Dictionary<string, string>? groupNameMap = null, string? reportComments = null, string? reportTitle = null,
        Dictionary<string, string>? parameterMap = null, Func<string, string?>? formatFor = null)
    {
        // RDL reads a leading "=" as "an expression follows", so literal text that starts
        // with one (a bare "=" used as a separator label, "=== Total ===", ...) becomes a
        // broken expression — "=" alone fails with "Constant or Identifier expected but not
        // found". Emit it as an expression yielding that literal instead.
        if (!text.Contains('{'))
            return text.StartsWith('=') || text.Contains('\n') ? $"={QuoteLiteral(text)}" : text;

        var parts = new List<string>();
        // A reference with text beside it, or another reference, is joined into one string.
        bool joined = text.IndexOf('{') > 0 || text.IndexOf('}') < text.Length - 1 || text.Count(c => c == '{') > 1;
        int pos = 0;
        while (pos < text.Length)
        {
            int open = text.IndexOf('{', pos);
            if (open < 0)
            {
                parts.Add(QuoteLiteral(text[pos..]));
                break;
            }
            if (open > pos)
                parts.Add(QuoteLiteral(text[pos..open]));

            int close = text.IndexOf('}', open + 1);
            if (close < 0)
            {
                parts.Add(QuoteLiteral(text[open..]));
                break;
            }
            string refName = text[(open + 1)..close];
            string refNorm = NormalizeFieldName(refName);
            // Also try stripping "Table." prefix if present (e.g. {Customer.Customer Name})
            int dotIdx = refNorm.IndexOf('.');
            string refBare = dotIdx >= 0 ? refNorm[(dotIdx + 1)..] : refNorm;
            // Resolve to the DataSet's own declared casing, not the reference's own — this
            // engine's Fields lookup is case-sensitive (see the FieldObject case above).
            // A summary first: "Count of T.Owing" would otherwise be taken for the column.
            string? summary = EmbeddedSummary(refName, knownFields);
            string? resolvedRealName = summary is not null ? null
                : knownFields.TryGetValue(refNorm, out var byNorm) ? byNorm
                : knownFields.TryGetValue(refBare, out var byBare) ? byBare
                : null;
            if (summary is not null)
                parts.Add(Formatted(summary, formatFor?.Invoke(refName)));
            else if (resolvedRealName is not null)
                parts.Add(BlankWhenNull($"Fields!{SanitizeName(resolvedRealName)}.Value", formatFor?.Invoke(refName), joined));
            else if (groupNameMap is not null && groupNameMap.TryGetValue(refName, out string? groupExpr))
                parts.Add(groupExpr);
            // Crystal writes parameter references as {?Name}; the leading "?" is part of
            // the reference syntax, not of the declared parameter name. Only the "?" is
            // stripped here (not "@"/"#" too) - confirmed via --dumpfields that
            // BuildParameterMap's own keys are the declared ParameterField.Name verbatim,
            // which sometimes already includes a leading "@" (see the matching comment on
            // the FieldObject case above) - stripping it here would make this miss the very
            // key it needs to hit.
            else if (parameterMap is not null
                     && parameterMap.TryGetValue(refBare.TrimStart('?'), out string? paramExpr))
                parts.Add(BlankWhenNull(paramExpr, formatFor?.Invoke(refName), joined));
            else if (SpecialFieldExpression(refName, reportComments, reportTitle) is string sfe)
                parts.Add(sfe.TrimStart('='));
            else
                parts.Add(QuoteLiteral($"{{{refName}}}"));
            pos = close + 1;
        }

        if (parts.Count == 0) return text;

        // A resolved part can come back empty (a group-name entry with nothing behind it, a
        // special field with no value), and joining only those yields a bare "=" — an
        // expression with no operand, which the engine rejects outright ("Constant or
        // Identifier expected but not found"). Drop the empties; if that leaves nothing,
        // the text genuinely resolves to nothing, so say so as an empty string literal.
        parts.RemoveAll(string.IsNullOrWhiteSpace);
        if (parts.Count == 0) return "=\"\"";

        // Always build an expression — even when all parts are string literals the resolved
        // content may differ from the original text (e.g. {report comments} → "Annual Summary").
        return "=" + string.Join(" & ", parts);
    }

    // A string literal for an expression. A line break - a text object's paragraphs - goes
    // in as Chr(10) between quoted parts rather than as a raw newline inside the quotes.
    private static string QuoteLiteral(string s)
    {
        if (s.Length == 0) return "\"\"";
        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // A tab inside the quotes as Chr(9), as a line break is Chr(10).
        return string.Join(" & Chr(10) & ", lines.Select(l =>
            string.Join(" & Chr(9) & ", l.Split('\t').Select(part => $"\"{part.Replace("\"", "\"\"")}\""))));
    }

    // Case-insensitive-keyed map of every known field name to its *actual* declared
    // casing (the exact string WriteDataSets emits as <Field Name="...">). A free-form
    // object's own FieldName reference doesn't always match a DB column's stored case
    // exactly (e.g. a placed FieldObject named "Personal" against a column literally
    // named "personal") — and this engine's Fields lookup is case-sensitive, so emitting
    // Fields!Personal.Value against a declared Field Name="personal" fails outright.
    // Resolving through this map instead of the reference's own text keeps the two sides
    // in sync. Multiple fields differing only by case is vanishingly unlikely in
    // practice; first one wins if it happens.
    private static Dictionary<string, string> BuildKnownFieldsMap(ReportDefinition report) =>
        report.Fields
            // Parameters are declared as ReportParameters, never as DataSet fields, so a
            // reference resolved through this map would emit Fields!X.Value for a column
            // WriteDataSets never writes. They resolve via BuildParameterMap instead.
            .Where(f => f is not ParameterField)
            .Select(f => f is DatabaseField db ? db.ColumnName : f.Name)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The space the report headers among these sections take on the page: each one's
    /// height, except a header suppressed outright, which Crystal neither draws nor spaces.
    /// Top5USA-piechart's suppressed chart header is 5,583 twips, and counting it left a
    /// blank four inches between its title block and its groups. A header suppressed by a
    /// formula keeps its height, there being no way to know here whether it prints.
    /// </summary>
    // The space the report header sections take down the page, so where the ones after them
    // start. One set to underlay the sections after it takes none: they print from its top,
    // over it. Top5USAunderlay's first report header holds its France subreport, and the USA
    // list prints beside it in Crystal; stacked, the USA list came out below it.
    // A line or box at the bounds given, which for a piece of one drawn across sections are
    // the piece's, with no edge where it meets the piece before or after it.
    private void WriteShape(XmlWriter w, ShapeObject shape, ObjectBounds bounds, string? hiddenExpr,
        int leftOffsetTwips = 0, int topOffsetTwips = 0, bool openTop = false, bool openBottom = false)
    {
        if (shape is LineObject)
        {
            w.WriteStartElement("Line", RdlNs);
            w.WriteAttributeString("Name", SanitizeName(shape.Name.Length > 0 ? shape.Name : $"line_{++_textboxCounter}"));
            // Crystal draws a line only across or down the page; its bounds' short side
            // is the room the line was given, not a slope. Drawn as the RDL Line those
            // bounds describe, ChinaOrders-RunningTotals' 2.95pt-tall rules came out as
            // diagonals.
            var flat = bounds.Width >= bounds.Height ? bounds with { Height = 0 } : bounds with { Width = 0 };
            WriteObjectPosition(w, flat, leftOffsetTwips, topOffsetTwips);
            WriteItemVisibility(w, hiddenExpr);
            w.WriteStartElement("Style", RdlNs);
            WriteShapeBorder(w, shape.Format, shape.Format.BorderTop);
            w.WriteEndElement();
            w.WriteEndElement();
            return;
        }

        w.WriteStartElement("Rectangle", RdlNs);
        w.WriteAttributeString("Name", SanitizeName(shape.Name.Length > 0 ? shape.Name : $"box_{++_textboxCounter}"));
        WriteObjectPosition(w, bounds, leftOffsetTwips, topOffsetTwips);
        WriteItemVisibility(w, hiddenExpr);
        w.WriteStartElement("Style", RdlNs);
        if (shape.Format.BackColor is { } fill)
            w.WriteElementString("BackgroundColor", RdlNs, fill);
        WriteShapeBorder(w, shape.Format, shape.Format.BorderTop, openTop, openBottom);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>
    /// Boxes and upright lines Crystal draws from one report header down into a later one,
    /// cut into a piece for each header they cross, keyed by that header. Written whole in the
    /// header it starts in, one was drawn at its record's height, which is not where it ends,
    /// and made that band as tall as itself: Top5-Canadian-Customers' frame around its dates
    /// and description pushed everything below it 37pt down. Drawn whole once, it would cover
    /// what the headers hold, since the engine draws items in order of their top. Each piece
    /// fills its own header from where the shape starts or the header's top to where it ends
    /// or the header's bottom, and is drawn under that header's contents as Crystal draws it.
    /// A header that is suppressed or underlays takes no space and gets no piece.
    ///
    /// The same holds for the several sections of one group header or footer, and of the
    /// page header or footer: a work order's box drawn from its group's second header into
    /// its third was cut off at the bottom of the second, open, with the third's fields
    /// below it where Crystal draws them inside.
    /// </summary>
    private static Dictionary<Section, List<(ShapeObject Shape, ObjectBounds Bounds, bool OpenTop, bool OpenBottom)>>
        ShapePiecesAcrossReportHeaders(ReportDefinition report)
    {
        var pieces = new Dictionary<Section, List<(ShapeObject, ObjectBounds, bool, bool)>>();
        foreach (var headers in report.Sections
                     .Where(s => s.Type is not SectionType.Details)
                     .GroupBy(s => (s.Type, s.Type is SectionType.GroupHeader or SectionType.GroupFooter ? s.GroupLevel : 0))
                     .Select(g => g.ToList()))
            AddShapePieces(headers, pieces);
        return pieces;
    }

    // Suppressed whatever the data: a suppress formula decides over the checkbox, as in
    // Crystal, so a section with both is drawn where its formula lets it.
    private static bool AlwaysSuppressed(Section section) =>
        section.Suppress && string.IsNullOrWhiteSpace(section.SuppressFormula);

    private static void AddShapePieces(List<Section> headers,
        Dictionary<Section, List<(ShapeObject, ObjectBounds, bool, bool)>> pieces)
    {
        foreach (var start in headers.Where(s => !AlwaysSuppressed(s) && !s.Underlay))
            foreach (var shape in start.Objects.OfType<ShapeObject>())
            {
                if (shape.EndSection is not { } end || !headers.Contains(end)
                    || shape is LineObject && shape.Bounds.Width > 0)
                    continue;
                int from = headers.IndexOf(start), to = headers.IndexOf(end);
                if (to <= from)
                    continue;
                var drawn = new List<(Section Section, ObjectBounds Bounds)>();
                for (int i = from; i <= to; i++)
                {
                    var section = headers[i];
                    if (AlwaysSuppressed(section) || section.Underlay)
                        continue;
                    int top = i == from ? shape.Bounds.Top : 0;
                    int bottom = i == to ? shape.EndBottomTwips : section.HeightTwips;
                    if (bottom > top)
                        drawn.Add((section, shape.Bounds with { Top = top, Height = bottom - top }));
                }
                // Edges go on the first and last pieces drawn: ending in a suppressed header,
                // Top5USA-piechart's frame is closed at the bottom of the header before it.
                for (int p = 0; p < drawn.Count; p++)
                {
                    if (!pieces.TryGetValue(drawn[p].Section, out var list))
                        pieces[drawn[p].Section] = list = [];
                    list.Add((shape, drawn[p].Bounds, p > 0, p < drawn.Count - 1));
                }
            }
    }

    // See ShadowedColumns; empty outside a conversion.
    private Dictionary<string, string> _shadowedColumns = new(StringComparer.OrdinalIgnoreCase);

    // The DataSet field name of a database column.
    private string ColumnFieldName(DatabaseField f)
    {
        string name = SanitizeName(f.ColumnName);
        return _shadowedColumns.TryGetValue(name, out var renamed) ? renamed : name;
    }

    /// <summary>
    /// Columns a formula shares its name with, each mapped to the name the DataSet declares
    /// the column under instead, so that the formula keeps its own. A DataSet holds one field
    /// per name, and the column won: boyum__BOM's formula ParentPrice, "ToText
    /// ({Header.ParentPrice}) + ' ' + {Header.ParentCurrency}", printed the bare column,
    /// "240" where Crystal prints "240.00 $". Every placed "@ParentPrice" and every
    /// "{@ParentPrice}" already read Fields!ParentPrice, so giving that name to the formula
    /// puts them right, and the column's own references, which are all in formula text,
    /// are written to its new name (FormulaTranspiler.ColumnFieldName).
    ///
    /// Only where the column is read nowhere else: placed or summarised, in a text object,
    /// grouped or sorted on, totalled by a running total, linked to a subreport, or wherever
    /// a chart or cross-tab might read it. Those read the column by the same name, so the
    /// formula keeps losing there, as before. A formula that is just the column itself
    /// ("{Header.Status}" named Status) is left to the column, which is the same value.
    /// </summary>
    private static Dictionary<string, string> ShadowedColumns(ReportDefinition report)
    {
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var columns = report.Fields.OfType<DatabaseField>().Select(f => SanitizeName(f.ColumnName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taken = report.Fields.Select(f => SanitizeName(NormalizeFieldName(f.Name))).Concat(columns)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runningTotals = report.Fields.OfType<RunningTotalField>().Select(f => SanitizeName(f.Name.Length > 0 ? f.Name : "RunTotal"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var objects = report.Sections.SelectMany(s => s.Objects).ToList();
        if (objects.Any(o => o is ChartObject or CrossTabObject))
            return renamed;

        static string LastPart(string reference)
        {
            string s = reference.Trim().Trim('{', '}');
            int dot = s.LastIndexOf('.');
            return SanitizeName(dot >= 0 ? s[(dot + 1)..] : s);
        }
        static bool IsOtherKind(string reference) => reference.Trim().TrimStart('{') is { Length: > 0 } s && s[0] is '@' or '?' or '#' or '%';

        var readDirectly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in objects)
        {
            if (o is FieldObject fo && !IsOtherKind(fo.FieldName))
            {
                readDirectly.Add(LastPart(fo.FieldName));
            }
            else if (o is TextObject t)
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(t.Text ?? "", @"\{[^{}]+\}"))
                    if (!IsOtherKind(m.Value)) readDirectly.Add(LastPart(m.Value));
            }
            else if (o is SubreportObject { Report: not null } sub)
            {
                foreach (var p in sub.Report.Fields.OfType<ParameterField>())
                    readDirectly.Add(LastPart(p.Name.StartsWith("Pm-", StringComparison.OrdinalIgnoreCase) ? p.Name[3..] : p.Name));
            }
        }
        foreach (var g in report.Groups) readDirectly.Add(LastPart(g.FieldName));
        foreach (var s in report.SortFields) readDirectly.Add(LastPart(s.FieldName));
        foreach (var r in report.Fields.OfType<RunningTotalField>())
            if (!string.IsNullOrEmpty(r.SummarizedFieldName)) readDirectly.Add(LastPart(r.SummarizedFieldName));

        foreach (var f in report.Fields.OfType<FormulaField>())
        {
            string name = SanitizeName(NormalizeFieldName(f.Name));
            if (!columns.Contains(name) || runningTotals.Contains(name) || readDirectly.Contains(name) || renamed.ContainsKey(name))
                continue;
            string body = System.Text.RegularExpressions.Regex.Replace(f.FormulaText, @"//[^\r\n]*", "").Trim().TrimEnd(';').Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(body, @"^\{[^{}@?#%]+\}$") && LastPart(body).Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            string to = name + "_column";
            for (int i = 2; taken.Contains(to); i++) to = $"{name}_column{i}";
            taken.Add(to);
            renamed[name] = to;
        }
        return renamed;
    }

    private static int ReportHeaderSpaceTwips(IEnumerable<Section> sections) =>
        sections.Where(s => s.Type == SectionType.ReportHeader && !s.Suppress && !s.Underlay).Sum(s => s.HeightTwips);

    /// <summary>
    /// Whether a Details table would have any column to build from: placed field objects,
    /// database columns, or images. WriteDetailsTable writes nothing at all when none of
    /// the three exist — a Details section holding only static text — so this is also what
    /// decides whether the Body commits to having a table. Deciding that on the broader
    /// "Details has any object" count opens a &lt;ReportItems&gt; the table never fills,
    /// which the engine rejects ("At least one item must be in the ReportItems").
    /// </summary>
    private static bool DetailsTableHasColumns(List<Section> detailsSections, ReportDefinition report) =>
        detailsSections.SelectMany(s => s.Objects.OfType<FieldObject>()).Any()
        || report.Fields.OfType<DatabaseField>().Any()
        || detailsSections.SelectMany(s => s.Objects.OfType<ImageObject>())
            .Any(i => i.Source == ImageSourceKind.Database || i.ImageData is not null);

    /// <summary>
    /// Wraps text-typed column references in <c>Val()</c> when the expression is doing
    /// arithmetic on them. Crystal types plenty of numeric-looking columns as text, and the
    /// engine then rejects the whole expression ("'-' operator works only on numbers") —
    /// but *retyping the column* was tried and measurably regressed both corpora, because a
    /// column used as a number in one formula is routinely a genuine string in another. So
    /// the coercion goes at the reference site, leaving the column's declared type alone.
    ///
    /// Only applied to an expression containing <c>-</c>, <c>*</c> or <c>/</c> and holding
    /// no string literal and no <c>&amp;</c> — Crystal's <c>+</c> is concatenation whenever
    /// a string is in reach, so those two exclusions are what separate "summing text
    /// columns" from "joining them". Same rule the parser's numeric inference already uses.
    /// <c>Val</c> rather than <c>CDbl</c> because it is total: a blank or non-numeric value
    /// yields 0 instead of throwing mid-render.
    /// </summary>
    private static string CoerceStringFieldsForArithmetic(string expr, HashSet<string> stringFieldNames)
    {
        if (stringFieldNames.Count == 0) return expr;
        if (expr.Contains('"') || expr.Contains('&')) return expr;
        if (expr.IndexOfAny(['-', '*', '/']) < 0) return expr;

        return FieldReference.Replace(expr, m =>
            stringFieldNames.Contains(m.Groups[1].Value) ? $"Val({m.Value})" : m.Value);
    }

    /// <summary>
    /// Same use-site coercion as <see cref="CoerceStringFieldsForArithmetic"/>, for the
    /// other place an untyped column is rejected outright: <c>Not {flag}</c>, which the
    /// engine turns down with "NOT requires boolean expression" before any data is read.
    /// The column really is a flag - Crystal would not negate it otherwise - so the fix
    /// is to say so at the point of use rather than to retype the column, which would
    /// also change how every other reference to it reads its value.
    /// </summary>
    private static string CoerceStringFieldsForBoolean(string expr, HashSet<string> stringFieldNames)
    {
        if (stringFieldNames.Count == 0 || !expr.Contains("Not", StringComparison.OrdinalIgnoreCase))
            return expr;

        return NegatedFieldReference.Replace(expr, m =>
            stringFieldNames.Contains(m.Groups[1].Value)
                ? $"Not (CBool(Fields!{m.Groups[1].Value}.Value))"
                : m.Value);
    }

    /// <summary>
    /// "Not" applied to a parenthesised field reference - the only shape the emitter
    /// produces. Matching a bare or optionally-parenthesised operand instead would let
    /// the trailing ")" bind to a paren belonging to an enclosing expression, and
    /// rewriting the match would then unbalance it.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex NegatedFieldReference =
        new(@"\bNot\s*\(\s*Fields!([A-Za-z0-9_]+)\.Value\s*\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex FieldReference =
        new(@"Fields!([A-Za-z0-9_]+)\.Value", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// A running total's evaluate formula as an RDL condition, without its leading "=", or
    /// null when it cannot be one: a formula that does not read as true or false (a
    /// comparison, And, Or, Not, In, Like, or an if whose branches are True or False) or that
    /// reads a field the DataSet will not have. Crystal requires the formula to be Boolean;
    /// one that is not would make the engine reject the whole running total.
    /// </summary>
    private static string? EvaluateConditionExpression(string? text,
        HashSet<string> dataSetFieldNames, HashSet<string> stringFieldNames)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string topLevel = System.Text.RegularExpressions.Regex.Replace(text, @"""[^""]*""|'[^']*'|\{[^}]*\}", "x");
        int depth = 0;
        var outside = new System.Text.StringBuilder();
        foreach (char c in topLevel)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0) outside.Append(c);
        }
        string bare = outside.ToString();
        bool boolean = System.Text.RegularExpressions.Regex.IsMatch(bare, @"[<>=]|\b(?:and|or|not|in|like|startswith|isnull)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(bare, @"^\s*if\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(bare, @"^\s*if\b.*\bthen\s+(?:true|false)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!boolean) return null;
        string expr = FormulaTranspiler.ToRdlExpression(new FormulaField { Name = "_", FormulaText = text });
        if (expr is "=\"\"" or "=0" || ReferencesUnknownField(expr, dataSetFieldNames)) return null;
        expr = CoerceStringFieldsForBoolean(CoerceStringFieldsForArithmetic(expr, stringFieldNames), stringFieldNames);
        return expr.StartsWith('=') ? expr[1..] : expr;
    }

    /// <summary>
    /// Whether an expression references a DataSet field that will not exist. Such a
    /// reference can never resolve, and the engine treats it as fatal rather than empty.
    /// </summary>
    private static bool ReferencesUnknownField(string expr, HashSet<string> dataSetFieldNames)
    {
        foreach (System.Text.RegularExpressions.Match m in FieldReference.Matches(expr))
            if (!dataSetFieldNames.Contains(m.Groups[1].Value))
                return true;
        return false;
    }

    /// Every function the engine's expression parser treats as an aggregate, and so
    /// refuses inside a Grouping expression (Parser.cs dispatches these to its
    /// FunctionAggr* family before ordinary function resolution).
    private static readonly string[] AggregateFunctionNames =
    [
        "Sum", "Avg", "Min", "Max", "Count", "CountRows", "CountDistinct",
        "First", "Last", "Next", "Previous", "RowNumber", "RunningValue",
        "StDev", "StDevP", "Var", "VarP", "Aggregate", "Level",
    ];

    private static bool ContainsAggregateCall(string expr) =>
        AggregateFunctionNames.Any(name => System.Text.RegularExpressions.Regex.IsMatch(
            expr, $@"\b{name}\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    /// <summary>
    /// Sanitized names of formulas that some formula references as a number — written
    /// adjacent to an arithmetic operator, as {@Total} * 2 or x - {@Total}. Only used to
    /// pick the placeholder for a formula that could not be translated, so it deliberately
    /// tests reference *sites* rather than trying to type the formula's own body. Mirrors
    /// the numeric-usage inference the parser applies to synthesized fields.
    /// </summary>
    private static HashSet<string> NumericallyUsedFormulaNames(ReportDefinition report,
        List<FormulaField> formulaFields)
    {
        // Section hooks count as reference sites too: a row-count formula is routinely
        // used only inside a table's suppress or page-break formula ("{@rowcount} -
        // ({?PerPage} - 1)"), never in another formula field, so scanning only the field
        // list left it degrading to "" and breaking that arithmetic.
        var texts = formulaFields.Select(f => f.FormulaText)
            .Concat(report.Sections.SelectMany(s => new[]
            {
                s.SuppressFormula, s.NewPageBeforeFormula, s.NewPageAfterFormula, s.BackColorFormula
            }))
            .Concat([report.RecordSelectionFormula, report.GroupSelectionFormula])
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .ToList();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Not seeded with every formula the file records as numeric: an untranslatable one
        // that nothing does arithmetic on prints blank, which is less wrong than a 0.
        if (texts.Count == 0) return result;

        foreach (var f in formulaFields)
        {
            if (f.Name.Length == 0) continue;
            string reference = System.Text.RegularExpressions.Regex.Escape($"{{@{f.Name}}}");
            var adjacent = new System.Text.RegularExpressions.Regex(
                $@"[-*/]\s*\(*\s*{reference}|{reference}\s*\)*\s*[-*/]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (texts.Any(t => adjacent.IsMatch(t)))
                result.Add(SanitizeName(f.Name));
        }

        // Numeric use is transitive, and the corpus needs it to be: a formula can be
        // subtracted from another while the values it is built out of are only ever
        // added together, and "+" alone is no evidence of arithmetic because Crystal
        // also concatenates with it. Carry the seed set down one reference at a time
        // instead, through bodies with no string literal in them - there every "+" must
        // be an addition, so every formula the body names is part of the same sum.
        // Without this a degraded operand two hops down stays "" and poisons the whole
        // expression with "'-' operator works only on numbers".
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var f in formulaFields)
            {
                if (f.Name.Length == 0 || !result.Contains(SanitizeName(f.Name))) continue;
                string body = f.FormulaText ?? "";
                if (body.Contains('"')) continue;
                foreach (System.Text.RegularExpressions.Match m in
                         FormulaFieldReference.Matches(body))
                {
                    string referenced = SanitizeName(m.Groups[1].Value);
                    if (result.Add(referenced)) grew = true;
                }
            }
        }
        return result;
    }

    /// <summary>A "{@Name}" formula reference inside a Crystal formula body.</summary>
    private static readonly System.Text.RegularExpressions.Regex FormulaFieldReference =
        new(@"\{@([^}]+)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Declared parameters, keyed by the name a placed object or text reference uses and
    /// mapped to the RDL expression for them. The declared ReportParameter name has the
    /// SAP "$[Id]" wrapper stripped, so references must be built the same way to match.
    /// </summary>
    private static Dictionary<string, string> BuildParameterMap(ReportDefinition report) =>
        report.Fields.OfType<ParameterField>()
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => $"Parameters!{SanitizeName(FormulaTranspiler.StripSapParamWrapper(g.Key))}.Value",
                StringComparer.OrdinalIgnoreCase);
}
