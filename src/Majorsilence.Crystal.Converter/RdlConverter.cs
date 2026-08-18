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

    // Prefix prepended to subreport ReportName references so they match the
    // companion .rdl filenames a batch caller writes (e.g. "ParentStem_").
    private string _subreportNamePrefix = string.Empty;

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
        using var writer = XmlWriter.Create(sb, settings);
        WriteReport(writer, report);
        writer.Flush();
        return sb.ToString();
    }

    private void WriteReport(XmlWriter w, ReportDefinition report)
    {
        w.WriteStartElement("Report", RdlNs);
        w.WriteElementString("Description", RdlNs, report.ReportTitle);
        w.WriteElementString("Author", RdlNs, report.Author);
        w.WriteElementString("Name", RdlNs, SanitizeName(report.ReportTitle));

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

    private void WritePage(XmlWriter w, PageLayout page)
    {
        w.WriteElementString("PageHeight", RdlNs, TwipsToRdl(page.HeightTwips));
        w.WriteElementString("PageWidth", RdlNs, TwipsToRdl(page.WidthTwips));
        w.WriteElementString("TopMargin", RdlNs, TwipsToRdl(page.TopMarginTwips));
        w.WriteElementString("BottomMargin", RdlNs, TwipsToRdl(page.BottomMarginTwips));
        w.WriteElementString("LeftMargin", RdlNs, TwipsToRdl(page.LeftMarginTwips));
        w.WriteElementString("RightMargin", RdlNs, TwipsToRdl(page.RightMarginTwips));
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
            w.WriteElementString("ConnectString", RdlNs,
                ds.OdbcDsn != null ? $"DSN={ds.OdbcDsn}" :
                ds.ServerName != null ? $"Data Source={ds.ServerName};Initial Catalog={ds.DatabaseName}" : "");
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
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
        string commandText = ds.SqlQuery ?? BuildSelectFromTables(ds);
        // If no table info in the DataSource (QESession encrypted), fall back to DB fields
        if (commandText == "SELECT * FROM <TableName>")
            commandText = BuildSelectFromFields(report.Fields.OfType<DatabaseField>().ToList());
        w.WriteElementString("CommandText", RdlNs, commandText);
        w.WriteEndElement(); // Query

        var dbFields = report.Fields.OfType<DatabaseField>().ToList();
        var formulaFields = report.Fields.OfType<FormulaField>().ToList();
        var runningTotals = report.Fields.OfType<RunningTotalField>().ToList();

        if (dbFields.Count > 0 || formulaFields.Count > 0 || runningTotals.Count > 0)
        {
            w.WriteStartElement("Fields", RdlNs);
            foreach (var f in dbFields)
            {
                w.WriteStartElement("Field", RdlNs);
                w.WriteAttributeString("Name", SanitizeName(f.ColumnName));
                w.WriteElementString("DataField", RdlNs, f.ColumnName);
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
            var dbFieldNames = dbFields.Select(f => SanitizeName(f.ColumnName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rtFieldNames = runningTotals.Select(f => SanitizeName(f.Name.Length > 0 ? f.Name : "RunTotal"))
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
                string expr = FormulaTranspiler.ToRdlExpression(f);
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
                w.WriteStartElement("Field", RdlNs);
                w.WriteAttributeString("Name", safeName);
                w.WriteElementString("Value", RdlNs, expr);
                w.WriteEndElement();
            }
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
                w.WriteStartElement("Field", RdlNs);
                w.WriteAttributeString("Name", safeName);
                w.WriteElementString("Value", RdlNs, $"=RunningValue({innerExpr}, {aggFn}, Nothing)");
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
            if (!string.IsNullOrWhiteSpace(filterExpr) && filterExpr.Length > 1 && isBooleanExpr)
            {
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
            w.WriteAttributeString("Name", SanitizeName(FormulaTranspiler.StripSapParamWrapper(p.Name)));
            // Map Crystal data type to SSRS parameter type
            string rdlType = p.DataType switch
            {
                "Float64" or "Float32" or "Currency" or "Int16" or "Int32" => "Float",
                "Boolean" => "Boolean",
                "DateTime" => "DateTime",
                _ => "String"
            };
            w.WriteElementString("DataType", RdlNs, rdlType);
            if (!string.IsNullOrEmpty(p.PromptText))
                w.WriteElementString("Prompt", RdlNs, p.PromptText);
            else
                w.WriteElementString("Prompt", RdlNs, p.Name);
            if (p.PickListValues.Count > 0)
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

        bool hasTable = detailObjects.Count > 0;

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
        static bool NeedsTableRouting(Section s) =>
            s.Objects.OfType<FieldObject>().Any()
            // A TextObject with embedded {Field} references resolves to the same Fields!
            // expressions a FieldObject does (ResolveTextWithFieldRefs), so it needs the
            // same data scope — iPayment's PageHeader "Credit Card Transaction Details
            // for {BusinessPartner.CardName}" failed exactly like a placed FieldObject.
            || s.Objects.OfType<TextObject>().Any(t => t.Text.Contains('{'))
            || ((s.Type == SectionType.PageHeader || s.Type == SectionType.PageFooter)
                && s.Objects.OfType<SubreportObject>().Any(sub => sub.Report is not null));

        // GroupHeader/GroupFooter normally need none of this: when a Details table exists
        // their content goes into TableGroup Header/Footer rows, which are already inside
        // the data region. But with an empty Details section (the cross-tab reports —
        // everything lives in a GroupHeader beside a Matrix) there are no TableGroup rows
        // to land in, so the section falls through to the same scope-less free-form Body
        // path, and a group-name FieldObject there fails identically. Route them too, but
        // only in that no-table case, so the normal tabular path is untouched.
        var fieldBoundPageHeaders = report.Sections
            .Where(s => (s.Type == SectionType.PageHeader || s.Type == SectionType.ReportHeader
                         || (!hasTable && s.Type == SectionType.GroupHeader))
                        && NeedsTableRouting(s))
            .ToList();
        var fieldBoundPageFooters = report.Sections
            .Where(s => (s.Type == SectionType.PageFooter || s.Type == SectionType.ReportFooter
                         || (!hasTable && s.Type == SectionType.GroupFooter))
                        && NeedsTableRouting(s))
            .ToList();

        // Free-form text/field objects from non-detail body sections. When a Table was
        // emitted, GroupHeader/GroupFooter content is already inside the TableGroup
        // Header/Footer rows — exclude them to avoid duplication. ReportFooter likewise
        // moves into the Table's own top-level Footer (see WriteDetailsTable) so it prints
        // once after the last detail row instead of at this fixed Body position, which —
        // since Crystal's Report Footer is meant to print once at the very end of a
        // report that can span many pages — would otherwise land it on page 1, on top of
        // the Report Header. Sections already claimed by fieldBoundPageHeaders/Footers
        // above (a field-bound ReportHeader/ReportFooter, most commonly) are excluded here
        // too, for the same reason. When there is no table, include everything so
        // TextObjects still appear.
        var freeFormSections = report.Sections.Where(s =>
            s.Type != SectionType.Details &&
            s.Type != SectionType.PageHeader &&
            s.Type != SectionType.PageFooter &&
            (s.Type != SectionType.GroupHeader || !hasTable) &&
            (s.Type != SectionType.GroupFooter || !hasTable) &&
            (s.Type != SectionType.ReportFooter || !hasTable) &&
            !fieldBoundPageHeaders.Contains(s) &&
            !fieldBoundPageFooters.Contains(s))
            .ToList();

        bool needsHeaderOnlyTable = !hasTable && (fieldBoundPageHeaders.Count > 0 || fieldBoundPageFooters.Count > 0);

        w.WriteStartElement("Body", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(
            detailsSections.Sum(s => s.HeightTwips) + 720));

        // A Table guarantees at least one item once emitted, but a Body with neither a
        // Table nor any free-form content with something real to show would otherwise
        // emit an empty <ReportItems> — fatal (Severity 8) to the engine, same class of
        // bug as the PageHeader/PageFooter case (see HasRenderableContent). ReportItems is
        // optional under Body too, so omit it entirely rather than write an empty shell.
        if (hasTable || needsHeaderOnlyTable || freeFormSections.Any(HasRenderableContent))
        {
            w.WriteStartElement("ReportItems", RdlNs);

            if (hasTable || needsHeaderOnlyTable)
            {
                // The Table is a normal absolutely-positioned Body item like everything
                // else — it doesn't auto-flow below whatever else is in the Body. Report
                // Header content (title/logo/tagline) is written elsewhere in this same
                // Body at Top=0 (correctly — it's meant to be the first thing on the
                // page), so without an explicit Top here the Table starts at that same
                // Top=0 and renders directly on top of it. Push it down by the Report
                // Header area's total height (0 when there isn't one).
                int reportHeaderHeightTwips = report.Sections
                    .Where(s => s.Type == SectionType.ReportHeader)
                    .Sum(s => s.HeightTwips);

                if (hasTable)
                    WriteDetailsTable(w, report, detailsSections, groupHeaders, groupFooters, consumedByTable,
                        reportHeaderHeightTwips, fieldBoundPageHeaders, fieldBoundPageFooters);
                else
                    WriteHeaderOnlyTable(w, report, fieldBoundPageHeaders, fieldBoundPageFooters,
                        reportHeaderHeightTwips);
            }

            foreach (var section in freeFormSections)
                WriteFreeFormObjects(w, section, report);

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
                        .Where(o => !consumedByTable.Contains(o))
                        .ToList();
                    if (leftovers.Count == 0) continue;
                    WriteFreeFormObjects(w, new Section
                    {
                        Type = section.Type,
                        Suppress = section.Suppress,
                        SuppressFormula = section.SuppressFormula,
                        Objects = leftovers
                    }, report);
                }
            }

            w.WriteEndElement(); // ReportItems
        }
        w.WriteEndElement(); // Body

        // Callers only ever filter this by SectionType (WritePageHeader/WritePageFooter
        // each look for their own kind), so one combined list is enough.
        return fieldBoundPageHeaders.Concat(fieldBoundPageFooters).ToList();
    }

    private void WriteDetailsTable(XmlWriter w, ReportDefinition report,
        List<Section> detailsSections, List<Section> groupHeaders, List<Section> groupFooters,
        List<ReportObject> consumedExtras, int topOffsetTwips = 0, List<Section>? fieldBoundPageHeaders = null,
        List<Section>? fieldBoundPageFooters = null)
    {
        var dbFields = report.Fields.OfType<DatabaseField>().ToList();
        var formulaFieldNames = report.Fields.OfType<FormulaField>()
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var detailFieldObjects = detailsSections
            .SelectMany(s => s.Objects.OfType<FieldObject>())
            .ToList();

        // Image objects in detail sections become extra table columns after the field columns
        var detailImageObjects = detailsSections
            .SelectMany(s => s.Objects.OfType<ImageObject>())
            .Where(i => i.Source == ImageSourceKind.Database || i.ImageData is not null)
            .ToList();

        if (detailFieldObjects.Count == 0 && dbFields.Count == 0 && detailImageObjects.Count == 0) return;

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
        var columns = detailFieldObjects.Count > 0
            ? detailFieldObjects.Select(f => NormalizeFieldName(f.FieldName)).ToList()
            : dbFields.Select(f => f.ColumnName).ToList();

        int defaultColWidth = report.Page.WidthTwips > 0
            ? report.Page.WidthTwips / Math.Max(1, columns.Count + detailImageObjects.Count)
            : 1440;

        // Build column-width lookup from detail FieldObject bounds (normalized names, twips)
        var colWidthByName = detailFieldObjects
            .Where(fo => fo.Bounds.Width > 0)
            .GroupBy(fo => NormalizeFieldName(fo.FieldName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Bounds.Width, StringComparer.OrdinalIgnoreCase);

        // Precompute each column's width so we can sum them for the Table element
        var colWidths = columns.Select(col =>
            colWidthByName.TryGetValue(col, out int bw) ? bw : defaultColWidth).ToList();
        colWidths.AddRange(detailImageObjects.Select(img =>
            img.Bounds.Width > 0 ? img.Bounds.Width : defaultColWidth));
        int totalTableWidthTwips = colWidths.Sum();
        int totalCols = columns.Count + detailImageObjects.Count;

        w.WriteStartElement("Table", RdlNs);
        w.WriteAttributeString("Name", "Table1");
        if (topOffsetTwips > 0)
            w.WriteElementString("Top", RdlNs, TwipsToRdl(topOffsetTwips));
        w.WriteElementString("DataSetName", RdlNs, "DataSet1");
        w.WriteElementString("Width", RdlNs, TwipsToRdl(totalTableWidthTwips));

        // TableColumns — use measured widths when available, fall back to uniform split
        w.WriteStartElement("TableColumns", RdlNs);
        for (int ci = 0; ci < totalCols; ci++)
        {
            w.WriteStartElement("TableColumn", RdlNs);
            w.WriteElementString("Width", RdlNs, TwipsToRdl(colWidths[ci]));
            w.WriteEndElement();
        }
        w.WriteEndElement();

        w.WriteStartElement("Header", RdlNs);
        w.WriteElementString("RepeatOnNewPage", RdlNs, "true");
        w.WriteStartElement("TableRows", RdlNs);

        if (fieldBoundPageHeaders is not null)
            foreach (var fieldBoundPageHeader in fieldBoundPageHeaders)
                WriteTableFreeFormRow(w, fieldBoundPageHeader, report, totalCols);

        w.WriteStartElement("TableRow", RdlNs);
        w.WriteElementString("Height", RdlNs, "14pt");
        w.WriteStartElement("TableCells", RdlNs);
        foreach (var col in columns)
            WriteTableCell(w, col, isBold: true);
        for (int ci = columns.Count; ci < totalCols; ci++)
            WriteTableCell(w, string.Empty);
        w.WriteEndElement(); // TableCells
        w.WriteEndElement(); // TableRow
        w.WriteEndElement(); // TableRows
        w.WriteEndElement(); // Header

        // Build helpers for TextObject field-ref resolution used in group rows.
        // Include both DB columns and formula field names so {@FormulaName} refs resolve.
        var knownFieldsForGroups = BuildKnownFieldsMap(report);
        var groupNameMapForTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int gi2 = 0; gi2 < report.Groups.Count; gi2++)
            groupNameMapForTable[$"Group #{gi2 + 1} Name"] =
                $"Fields!{SanitizeName(NormalizeFieldName(report.Groups[gi2].FieldName))}.Value";

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
                var ghSection = groupHeaders.Count > gi ? groupHeaders[gi] : groupHeaders.FirstOrDefault();
                var gfSectionForBreaks = groupFooters.Count > gi ? groupFooters[gi] : groupFooters.FirstOrDefault();

                w.WriteStartElement("TableGroup", RdlNs);

                string fieldRef = $"Fields!{SanitizeName(grpFieldNorm)}.Value";
                string groupExpr = DatePartGroupExpression(fieldRef, grp.Condition);

                w.WriteStartElement("Grouping", RdlNs);
                w.WriteAttributeString("Name", $"Group{gi + 1}");
                w.WriteStartElement("GroupExpressions", RdlNs);
                w.WriteElementString("GroupExpression", RdlNs, groupExpr);
                w.WriteEndElement(); // GroupExpressions
                // A page-break formula supersedes the static checkbox (same precedence as
                // section suppression) and gates the break via PageBreakCondition —
                // PageBreakAtStart/AtEnd choose *where*, PageBreakCondition gates *whether*.
                // RDL's Grouping has only one PageBreakCondition shared by both directions;
                // when Crystal attaches formulas to both NewPageBefore and NewPageAfter,
                // the before-formula wins (rare — most reports use at most one direction).
                string? npbExpr = ghSection is not null ? TranspileNewPageBeforeFormula(ghSection.NewPageBeforeFormula) : null;
                string? npaExpr = gfSectionForBreaks is not null ? TranspileNewPageAfterFormula(gfSectionForBreaks.NewPageAfterFormula) : null;
                if (npbExpr is not null || ghSection?.NewPageBefore == true)
                    w.WriteElementString("PageBreakAtStart", RdlNs, "true");
                if (npaExpr is not null || gfSectionForBreaks?.NewPageAfter == true)
                    w.WriteElementString("PageBreakAtEnd", RdlNs, "true");
                // PageBreakCondition is parsed in Grouping context, where the engine bans
                // aggregate functions outright — and Crystal's most common page-break
                // formula, "Not OnFirstRecord" ("break before each group except the very
                // first"), transpiles to RowNumber(), which the engine classifies as one.
                // Emit the static break without the condition in that case: RDL's plain
                // PageBreakAtStart is the same behavior minus the except-the-first nuance,
                // an acceptable approximation vs. a fatal parse error.
                if ((npbExpr ?? npaExpr) is string pageBreakExpr
                    && !pageBreakExpr.Contains("RowNumber(") && !pageBreakExpr.Contains("CountRows("))
                    w.WriteElementString("PageBreakCondition", RdlNs, pageBreakExpr);
                w.WriteEndElement(); // Grouping

                // TableGroup's own sort key: <Sorting><SortBy><SortExpression>/<Direction></SortBy></Sorting>
                // (same shape as Details' <Sorting>, written by WriteDetailSortExpressions) — NOT
                // the schema-invalid <SortExpressions> directly under <TableGroup> this used to emit,
                // which the engine silently ignored (a Severity-4 "unknown element" warning, not an
                // Error/Fatal, so it was never caught by the Error/Fatal-only engine-compat checks).
                w.WriteStartElement("Sorting", RdlNs);
                w.WriteStartElement("SortBy", RdlNs);
                w.WriteElementString("SortExpression", RdlNs, groupExpr);
                if (grp.SortOrder == GroupSortOrder.Descending)
                    w.WriteElementString("Direction", RdlNs, "Descending");
                w.WriteEndElement(); // SortBy
                w.WriteEndElement(); // Sorting

                if (ghSection is not null)
                {
                    // Use TextObject content from the GroupHeader section when available
                    var ghTextObj = ghSection.Objects.OfType<TextObject>()
                        .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));
                    string ghCellValue = ghTextObj is not null
                        ? ResolveTextWithFieldRefs(ghTextObj.Text, knownFieldsForGroups, groupNameMapForTable, report.ReportComments, report.ReportTitle)
                        : $"=Fields!{SanitizeName(grpFieldNorm)}.Value";
                    ObjectFormat? ghFormat = ghTextObj?.Format;

                    // Non-field objects in the group header (subreports, images) get
                    // placed into whatever cells would otherwise be empty.
                    var ghExtras = QueueGroupRowExtras(ghSection, ghTextObj);

                    w.WriteStartElement("Header", RdlNs);
                    w.WriteElementString("RepeatOnNewPage", RdlNs, ghSection.RepeatGroupHeader ? "true" : "false");
                    w.WriteStartElement("TableRows", RdlNs);
                    w.WriteStartElement("TableRow", RdlNs);
                    w.WriteElementString("Height", RdlNs, TwipsToRdl(ghSection.HeightTwips > 0 ? ghSection.HeightTwips : 240));
                    WriteRowVisibility(w, ghSection);
                    w.WriteStartElement("TableCells", RdlNs);
                    WriteTableCell(w, ghCellValue, ghFormat ?? new ObjectFormat { Bold = true });
                    // Fill remaining columns from matching GroupHeader FieldObjects —
                    // Crystal often places group summaries (e.g. "Count of X") here.
                    for (int ci = 1; ci < totalCols; ci++)
                    {
                        var ghFo = ci < columns.Count
                            ? ghSection.Objects.OfType<FieldObject>().FirstOrDefault(f =>
                                string.Equals(NormalizeFieldName(f.FieldName), columns[ci], StringComparison.OrdinalIgnoreCase))
                            : null;
                        if (ghFo is not null && dbFieldMap.ContainsKey(NormalizeFieldName(ghFo.FieldName)))
                        {
                            string ghField = SanitizeName(NormalizeFieldName(ghFo.FieldName));
                            WriteTableCell(w, BuildSummaryExpression(ghFo.SummaryFunction, ghField), ghFo.Format);
                        }
                        else if (!TryWriteQueuedObjectCell(w, ghExtras, report, consumedExtras))
                        {
                            WriteTableCell(w, string.Empty);
                        }
                    }
                    w.WriteEndElement(); // TableCells
                    w.WriteEndElement(); // TableRow
                    w.WriteEndElement(); // TableRows
                    w.WriteEndElement(); // Header
                }

                // Group footer row with aggregate expressions for known DB fields
                var gfSection = groupFooters.Count > gi ? groupFooters[gi] : groupFooters.FirstOrDefault();
                if (gfSection is not null)
                {
                    var gfExtras = QueueGroupRowExtras(gfSection, usedTextObject: null);

                    w.WriteStartElement("Footer", RdlNs);
                    w.WriteStartElement("TableRows", RdlNs);
                    w.WriteStartElement("TableRow", RdlNs);
                    w.WriteElementString("Height", RdlNs, TwipsToRdl(gfSection.HeightTwips > 0 ? gfSection.HeightTwips : 240));
                    WriteRowVisibility(w, gfSection);
                    w.WriteStartElement("TableCells", RdlNs);
                    for (int ci = 0; ci < columns.Count; ci++)
                    {
                        // Find the matching FieldObject from the group footer section (normalize @ prefix)
                        var fo = gfSection.Objects.OfType<FieldObject>()
                            .FirstOrDefault(f => string.Equals(NormalizeFieldName(f.FieldName), columns[ci], StringComparison.OrdinalIgnoreCase));
                        string foNorm = fo is not null ? NormalizeFieldName(fo.FieldName) : columns[ci];
                        string cellValue;
                        if (fo is not null && dbFieldMap.ContainsKey(foNorm))
                            // Group footer fields are assumed summarized even without an explicit
                            // SummaryFunction (unrecognised tags default to Sum) — BuildSummaryExpression's
                            // "null = plain field" rule doesn't apply here, so only route through it for
                            // Percentage, which needs its two-part expression regardless.
                            cellValue = fo.SummaryFunction == AggregateFunction.Percentage
                                ? BuildSummaryExpression(fo.SummaryFunction, SanitizeName(foNorm))
                                : $"={RdlAggregateFunction(fo.SummaryFunction)}(Fields!{SanitizeName(foNorm)}.Value)";
                        else if (fo is not null && SpecialFieldExpression(fo.FieldName, report.ReportComments, report.ReportTitle) is string sfe)
                            cellValue = sfe;
                        else if (dbFieldMap.TryGetValue(columns[ci], out var dbf) && IsNumericType(dbf.DataType))
                            // Fallback: Crystal summary fields (e.g. SumofXYZ) use unrecognised tags; generate
                            // Sum() for any numeric column if the group footer section exists
                            cellValue = $"=Sum(Fields!{SanitizeName(columns[ci])}.Value)";
                        else
                            cellValue = string.Empty;
                        if (cellValue.Length == 0 && TryWriteQueuedObjectCell(w, gfExtras, report, consumedExtras))
                            continue;
                        WriteTableCell(w, cellValue, fo?.Format);
                    }
                    for (int ci = columns.Count; ci < totalCols; ci++)
                    {
                        if (!TryWriteQueuedObjectCell(w, gfExtras, report, consumedExtras))
                            WriteTableCell(w, string.Empty);
                    }
                    w.WriteEndElement(); // TableCells
                    w.WriteEndElement(); // TableRow
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
            .Select(s => TranspileSuppressFormula(s.SuppressFormula))
            .FirstOrDefault(e => e is not null);
        bool detailNewPageBefore = detailsSections.Any(s => s.NewPageBefore);
        bool detailNewPageAfter = detailsSections.Any(s => s.NewPageAfter);

        w.WriteStartElement("Details", RdlNs);
        if (detailNewPageBefore) w.WriteElementString("PageBreakAtStart", RdlNs, "true");
        if (detailNewPageAfter)  w.WriteElementString("PageBreakAtEnd",   RdlNs, "true");
        WriteDetailSortExpressions(w, report.SortFields);
        w.WriteStartElement("TableRows", RdlNs);
        w.WriteStartElement("TableRow", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(
            detailsSections.FirstOrDefault()?.HeightTwips ?? 240));
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
        for (int ci = 0; ci < columns.Count; ci++)
        {
            var fo = detailFieldObjects.FirstOrDefault(f =>
                string.Equals(NormalizeFieldName(f.FieldName), columns[ci], StringComparison.OrdinalIgnoreCase))
                ?? (ci < detailFieldObjects.Count ? detailFieldObjects[ci] : null);
            string cellVal = dataSetFieldsByName.TryGetValue(columns[ci], out string? realColumnName)
                ? $"=Fields!{SanitizeName(realColumnName)}.Value"
                : string.Empty;  // running total / unrecognised field — no DataSet entry yet
            WriteTableCell(w, cellVal, fo?.Format);
        }
        foreach (var img in detailImageObjects)
            WriteImageTableCell(w, img);
        w.WriteEndElement(); // TableCells
        w.WriteEndElement(); // TableRow
        w.WriteEndElement(); // TableRows
        w.WriteEndElement(); // Details

        WriteTableReportFooter(w, report, totalCols, fieldBoundPageFooters ?? new List<Section>());

        w.WriteEndElement(); // Table
    }

    // Some Crystal "document card" templates (SAP Business One invoices, transfers, ...)
    // leave Details entirely empty — the whole per-record display lives in Page
    // Header/Footer FieldObjects instead (see fieldBoundPageHeaders/fieldBoundPageFooters
    // in WriteBody). There's no real Details Table to route that content into in that
    // case, so this builds the smallest one that can host it: a single full-width
    // column, a Header holding the field-bound Page Header content (RepeatOnNewPage,
    // matching Crystal's own "prints every page" semantics), and one blank Details row —
    // RDL requires at least one ("For TableRows at least one TableRow is required" — a
    // hard rule even though Crystal's own Details has nothing in it either).
    private void WriteHeaderOnlyTable(XmlWriter w, ReportDefinition report,
        List<Section> fieldBoundPageHeaders, List<Section> fieldBoundPageFooters, int topOffsetTwips)
    {
        int width = report.Page.WidthTwips - report.Page.LeftMarginTwips - report.Page.RightMarginTwips;
        if (width <= 0) width = 1440;

        w.WriteStartElement("Table", RdlNs);
        w.WriteAttributeString("Name", "Table1");
        if (topOffsetTwips > 0)
            w.WriteElementString("Top", RdlNs, TwipsToRdl(topOffsetTwips));
        w.WriteElementString("DataSetName", RdlNs, "DataSet1");
        w.WriteElementString("Width", RdlNs, TwipsToRdl(width));

        w.WriteStartElement("TableColumns", RdlNs);
        w.WriteStartElement("TableColumn", RdlNs);
        w.WriteElementString("Width", RdlNs, TwipsToRdl(width));
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("Header", RdlNs);
        w.WriteElementString("RepeatOnNewPage", RdlNs, "true");
        w.WriteStartElement("TableRows", RdlNs);
        foreach (var section in fieldBoundPageHeaders)
            WriteTableFreeFormRow(w, section, report, totalCols: 1);
        w.WriteEndElement(); // TableRows
        w.WriteEndElement(); // Header

        w.WriteStartElement("Details", RdlNs);
        w.WriteStartElement("TableRows", RdlNs);
        w.WriteStartElement("TableRow", RdlNs);
        w.WriteElementString("Height", RdlNs, "1pt");
        w.WriteStartElement("TableCells", RdlNs);
        WriteTableCell(w, string.Empty);
        w.WriteEndElement(); // TableCells
        w.WriteEndElement(); // TableRow
        w.WriteEndElement(); // TableRows
        w.WriteEndElement(); // Details

        WriteTableReportFooter(w, report, totalCols: 1, fieldBoundPageFooters);

        w.WriteEndElement(); // Table
    }

    // Crystal's Report Footer prints once, after the very last detail row, however many
    // pages that turns out to be — the same "once, wherever the data ends" semantics as an
    // RDL Table's own top-level Footer (sibling to Header, not the per-TableGroup Footer
    // used for group summaries above). Content spans the full row via ColSpan since these
    // are normally free-form title/tagline-style objects, not one-value-per-column data.
    //
    // fieldBoundPageFooters piggybacks on this same band for the mirror-image reason
    // fieldBoundPageHeaders piggybacks on the Table's own Header (see WriteBody): a
    // PageFooter section with FieldObjects needs Fields! access RDL's own <PageFooter>
    // can never provide. Written before the once-only ReportFooter text, matching the
    // Header side's "repeating content first" ordering.
    private void WriteTableReportFooter(XmlWriter w, ReportDefinition report, int totalCols,
        List<Section> fieldBoundPageFooters)
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
            WriteTableFreeFormRow(w, pfSection, report, totalCols);

        if (text.Length == 0)
        {
            w.WriteEndElement(); // TableRows
            w.WriteEndElement(); // Footer
            return;
        }

        string? hiddenExpr = TranspileSuppressFormula(section!.SuppressFormula)
                             ?? (section.Suppress ? "true" : null);

        w.WriteStartElement("TableRow", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(section.HeightTwips > 0 ? section.HeightTwips : 240));
        if (hiddenExpr is not null)
        {
            w.WriteStartElement("Visibility", RdlNs);
            w.WriteElementString("Hidden", RdlNs, hiddenExpr);
            w.WriteEndElement();
        }
        w.WriteStartElement("TableCells", RdlNs);
        w.WriteStartElement("TableCell", RdlNs);
        if (totalCols > 1)
            w.WriteElementString("ColSpan", RdlNs, totalCols.ToString());
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
    private void WriteTableFreeFormRow(XmlWriter w, Section section, ReportDefinition report, int totalCols)
    {
        string? hiddenExpr = TranspileSuppressFormula(section.SuppressFormula)
                             ?? (section.Suppress ? "true" : null);

        w.WriteStartElement("TableRow", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(section.HeightTwips > 0 ? section.HeightTwips : 240));
        if (hiddenExpr is not null)
        {
            w.WriteStartElement("Visibility", RdlNs);
            w.WriteElementString("Hidden", RdlNs, hiddenExpr);
            w.WriteEndElement();
        }
        w.WriteStartElement("TableCells", RdlNs);
        w.WriteStartElement("TableCell", RdlNs);
        if (totalCols > 1)
            w.WriteElementString("ColSpan", RdlNs, totalCols.ToString());
        w.WriteStartElement("ReportItems", RdlNs);
        w.WriteStartElement("Rectangle", RdlNs);
        w.WriteAttributeString("Name", $"Rectangle_{++_textboxCounter}");
        w.WriteStartElement("ReportItems", RdlNs);
        WriteFreeFormObjects(w, section, report);
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

    private void WriteTableCell(XmlWriter w, string value, ObjectFormat? format = null, bool isBold = false)
    {
        w.WriteStartElement("TableCell", RdlNs);
        w.WriteStartElement("ReportItems", RdlNs);
        w.WriteStartElement("Textbox", RdlNs);
        w.WriteAttributeString("Name", $"Textbox_{++_textboxCounter}");
        w.WriteStartElement("Value", RdlNs);
        w.WriteString(value);
        w.WriteEndElement();
        w.WriteElementString("CanGrow", RdlNs, "true");
        bool bold = format?.Bold ?? isBold;
        var effectiveFormat = format is not null
            ? (bold == format.Bold ? format : new ObjectFormat { FontName = format.FontName, FontSize = format.FontSize, Bold = bold, Italic = format.Italic, Underline = format.Underline })
            : (bold ? new ObjectFormat { Bold = true } : null);
        WriteObjectStyle(w, effectiveFormat);
        w.WriteEndElement(); // Textbox
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // TableCell
    }

    // Non-field objects placed in a group header/footer section that would otherwise
    // be dropped by the tabular layout — filled into empty cells of the group row.
    private static Queue<ReportObject> QueueGroupRowExtras(Section section, TextObject? usedTextObject) =>
        new(section.Objects.Where(o => o switch
        {
            SubreportObject sub => sub.Report is not null,
            ImageObject img => img.Source == ImageSourceKind.Database || img.ImageData is not null,
            TextObject t => !ReferenceEquals(t, usedTextObject) && !string.IsNullOrWhiteSpace(t.Text),
            ChartObject => true,
            // A Percentage-of-total field shares its column name with the plain summary
            // field it's a percentage of (e.g. two "ORDER_AMOUNT" FieldObjects, one Sum one
            // Percentage) — the column-matching loop only has one cell per column name and
            // always picks the first, so the second field needs the same overflow handling
            // subreports/images/charts already get.
            FieldObject { SummaryFunction: AggregateFunction.Percentage } => true,
            _ => false
        }));

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
                WriteTableCell(w, text.Text, text.Format);
                return true;
            case ChartObject chart:
                WriteChartTableCell(w, chart, report);
                return true;
            case FieldObject fo:
                WriteTableCell(w, BuildSummaryExpression(fo.SummaryFunction, SanitizeName(NormalizeFieldName(fo.FieldName))), fo.Format);
                return true;
            default:
                return false;
        }
    }

    private void WriteSubreportTableCell(XmlWriter w, SubreportObject sub, ReportDefinition report)
    {
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

    private void WriteObjectStyle(XmlWriter w, ObjectFormat? fmt)
    {
        if (fmt is null) return;
        bool hasStyle = fmt.Bold || fmt.Italic || fmt.Underline
                     || fmt.FontName is not null || fmt.FontSize.HasValue
                     || fmt.ForeColor is not null
                     || fmt.HAlign != HorizontalAlignment.Left;
        if (!hasStyle) return;

        w.WriteStartElement("Style", RdlNs);
        if (fmt.ForeColor is not null)
            w.WriteElementString("Color", RdlNs, fmt.ForeColor);
        if (fmt.FontName is not null)
            w.WriteElementString("FontFamily", RdlNs, fmt.FontName);
        if (fmt.FontSize.HasValue)
            w.WriteElementString("FontSize", RdlNs, $"{fmt.FontSize.Value}pt");
        if (fmt.Bold)
            w.WriteElementString("FontWeight", RdlNs, "Bold");
        if (fmt.Italic)
            w.WriteElementString("FontStyle", RdlNs, "Italic");
        if (fmt.Underline)
            w.WriteElementString("TextDecoration", RdlNs, "Underline");
        if (fmt.HAlign != HorizontalAlignment.Left)
            w.WriteElementString("TextAlign", RdlNs, fmt.HAlign.ToString());
        w.WriteEndElement(); // Style
    }

    private void WriteFreeFormObjects(XmlWriter w, Section section, ReportDefinition? report = null)
    {
        // Free-form containers (PageHeader/PageFooter, Body items) have no row to
        // hide, so section-level suppression lands on each emitted item instead.
        string? hiddenExpr = TranspileSuppressFormula(section.SuppressFormula)
                             ?? (section.Suppress ? "true" : null);

        var knownFields = report is not null
            ? BuildKnownFieldsMap(report)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Build "Group #N Name" → Fields!GroupField.Value lookup from report groups
        var groupNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (report is not null)
        {
            for (int gi = 0; gi < report.Groups.Count; gi++)
                groupNameMap[$"Group #{gi + 1} Name"] = $"Fields!{SanitizeName(NormalizeFieldName(report.Groups[gi].FieldName))}.Value";
        }

        // Crystal's free-form objects (page-header column labels, report-header title
        // blocks, ...) don't carry a usable absolute Left in this binary format — every
        // object sampled across the corpus reads Left=0. When a section has more than one
        // object and every one of them is Left=0 (the degenerate, unusable case — a
        // section that genuinely has one real non-zero Left already works fine and is
        // left untouched), lay them out left-to-right by declaration order using the one
        // dimension that *does* parse correctly (Width) — the same convention
        // WriteDetailsTable already relies on for the Details table's own columns.
        if (section.Objects.Count > 1 && section.Objects.All(o => o.Bounds.Left == 0))
        {
            // Images anchor the left edge (a logo beside a title/tagline) regardless of
            // Crystal's internal declaration order — confirmed against corpus files where
            // the image object is declared *after* the title text but still renders
            // leftmost. Stable-partition images first, then everything else, each group
            // keeping its own relative order.
            var flowOrder = section.Objects.OfType<ImageObject>().Cast<ReportObject>()
                .Concat(section.Objects.Where(o => o is not ImageObject));
            int runningLeft = 0;
            foreach (var obj in flowOrder)
            {
                obj.Bounds = obj.Bounds with { Left = runningLeft };
                runningLeft += obj.Bounds.Width;
            }
        }

        foreach (var obj in section.Objects)
        {
            // A per-object suppress override (Crystal's ReportObjects[x].ObjectFormat.EnableSuppress)
            // wins over whatever the section itself would otherwise apply.
            string? itemHidden = obj.SuppressOverride switch
            {
                true => "true",
                false => "false",
                null => hiddenExpr
            };

            switch (obj)
            {
                case TextObject text:
                    w.WriteStartElement("Textbox", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(text.Name.Length > 0 ? text.Name : $"text_{++_textboxCounter}"));
                    WriteObjectPosition(w, text.Bounds);
                    WriteItemVisibility(w, itemHidden);
                    w.WriteElementString("Value", RdlNs, ResolveTextWithFieldRefs(text.Text, knownFields, groupNameMap, report?.ReportComments ?? string.Empty, report?.ReportTitle ?? string.Empty));
                    w.WriteElementString("CanGrow", RdlNs, "true");
                    WriteObjectStyle(w, text.Format);
                    w.WriteEndElement();
                    break;

                case FieldObject field:
                    w.WriteStartElement("Textbox", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(field.Name.Length > 0 ? field.Name : $"field_{++_textboxCounter}"));
                    WriteObjectPosition(w, field.Bounds);
                    WriteItemVisibility(w, itemHidden);
                    // Only emit a field expression when the field exists in the DataSet
                    string fieldValue;
                    // Strip @/# prefix for Crystal formula/running-total field references
                    string lookupName = NormalizeFieldName(field.FieldName);
                    if (knownFields.TryGetValue(lookupName, out string? realFieldName))
                        // Summary fields (grand totals in report header/footer) aggregate
                        // over the whole DataSet scope; plain fields render the raw value.
                        // Use the DataSet's own declared casing (realFieldName), not the
                        // FieldObject's — this engine's Fields lookup is case-sensitive, and
                        // Crystal's placed-object FieldName doesn't always match a DB column's
                        // stored case exactly (e.g. FieldName "Personal" vs column "personal").
                        fieldValue = BuildSummaryExpression(field.SummaryFunction, SanitizeName(realFieldName));
                    else if (groupNameMap.TryGetValue(field.FieldName, out string? groupFieldExpr))
                        fieldValue = "=" + groupFieldExpr;
                    else if (SpecialFieldExpression(field.FieldName, report?.ReportComments ?? string.Empty, report?.ReportTitle ?? string.Empty) is string specialExpr)
                        fieldValue = specialExpr;
                    else
                        fieldValue = $"[{field.FieldName}]";
                    w.WriteElementString("Value", RdlNs, fieldValue);
                    w.WriteElementString("CanGrow", RdlNs, "true");
                    WriteObjectStyle(w, field.Format);
                    w.WriteEndElement();
                    break;

                case CrossTabObject crossTab:
                    WriteMatrix(w, crossTab, itemHidden);
                    break;

                case ChartObject chart:
                    WriteChart(w, chart, itemHidden, report);
                    break;

                case LineObject line:
                    w.WriteStartElement("Line", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(line.Name.Length > 0 ? line.Name : $"line_{++_textboxCounter}"));
                    WriteObjectPosition(w, line.Bounds);
                    WriteItemVisibility(w, itemHidden);
                    w.WriteStartElement("Style", RdlNs);
                    w.WriteStartElement("BorderStyle", RdlNs);
                    w.WriteElementString("Default", RdlNs, "Solid");
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteEndElement();
                    break;

                case BoxObject box:
                    w.WriteStartElement("Rectangle", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(box.Name.Length > 0 ? box.Name : $"box_{++_textboxCounter}"));
                    WriteObjectPosition(w, box.Bounds);
                    WriteItemVisibility(w, itemHidden);
                    w.WriteStartElement("Style", RdlNs);
                    w.WriteStartElement("BorderStyle", RdlNs);
                    w.WriteElementString("Default", RdlNs, "Solid");
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteEndElement();
                    break;

                case SubreportObject sub when sub.Report is not null:
                    w.WriteStartElement("Subreport", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(sub.Name.Length > 0 ? sub.Name : $"subreport_{++_textboxCounter}"));
                    WriteObjectPosition(w, sub.Bounds);
                    WriteItemVisibility(w, itemHidden);
                    // Companion .rdl written by the batch caller under this name
                    w.WriteElementString("ReportName", RdlNs, SubreportRdlName(_subreportNamePrefix, sub.SubreportName));
                    if (report is not null)
                        WriteSubreportParameters(w, sub, report);
                    w.WriteEndElement();
                    break;

                case ImageObject image:
                    // Unresolved embedded images (missing storage / unknown format) are skipped
                    if (image.Source == ImageSourceKind.Embedded && image.ImageData is null)
                        break;
                    w.WriteStartElement("Image", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(image.Name.Length > 0 ? image.Name : $"image_{++_textboxCounter}"));
                    WriteObjectPosition(w, image.Bounds);
                    WriteItemVisibility(w, itemHidden);
                    WriteImageSourceElements(w, image);
                    w.WriteEndElement();
                    break;
            }
        }
    }

    private void WriteObjectPosition(XmlWriter w, ObjectBounds bounds)
    {
        w.WriteElementString("Top", RdlNs, TwipsToRdl(bounds.Top));
        w.WriteElementString("Left", RdlNs, TwipsToRdl(bounds.Left));
        w.WriteElementString("Width", RdlNs, TwipsToRdl(bounds.Width));
        w.WriteElementString("Height", RdlNs, TwipsToRdl(bounds.Height));
    }

    private void WritePageHeader(XmlWriter w, ReportDefinition report, List<Section> consumedByTable)
    {
        var section = report.Sections.FirstOrDefault(s => s.Type == SectionType.PageHeader && !consumedByTable.Contains(s));
        if (section is null || !HasRenderableContent(section)) return;

        w.WriteStartElement("PageHeader", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(section.HeightTwips));
        w.WriteElementString("PrintOnFirstPage", RdlNs, "true");
        w.WriteElementString("PrintOnLastPage", RdlNs, "true");
        w.WriteStartElement("ReportItems", RdlNs);
        WriteFreeFormObjects(w, section, report);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private void WritePageFooter(XmlWriter w, ReportDefinition report, List<Section> consumedByTable)
    {
        var section = report.Sections.FirstOrDefault(s => s.Type == SectionType.PageFooter && !consumedByTable.Contains(s));
        if (section is null || !HasRenderableContent(section)) return;

        w.WriteStartElement("PageFooter", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(section.HeightTwips));
        w.WriteElementString("PrintOnFirstPage", RdlNs, "true");
        w.WriteElementString("PrintOnLastPage", RdlNs, "true");
        w.WriteStartElement("ReportItems", RdlNs);
        WriteFreeFormObjects(w, section, report);
        w.WriteEndElement();
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
            CrossTabObject ct => ct.RowGroupFields.Count > 0 && ct.ColumnGroupFields.Count > 0 && ct.Cells.Count > 0,
            _ => true
        });

    // Bind child subreport parameters to parent values by naming convention.
    // Crystal stores the actual link table in encrypted streams, but linked child
    // parameters are conventionally named after the parent thing they bind to:
    // "Pm-Table.Column" (wizard links), "@Formula" (formula links), or the bare
    // parent field/parameter name. Unresolvable parameters stay promptable.
    private void WriteSubreportParameters(XmlWriter w, SubreportObject sub, ReportDefinition parent)
    {
        var bindings = new List<(string ChildParam, string ParentExpr)>();
        foreach (var childParam in sub.Report!.Fields.OfType<ParameterField>())
        {
            string candidate = childParam.Name.StartsWith("Pm-", StringComparison.OrdinalIgnoreCase)
                ? childParam.Name[3..]
                : childParam.Name;
            int dot = candidate.IndexOf('.');
            if (dot >= 0) candidate = candidate[(dot + 1)..];      // "Table.Column" → "Column"
            candidate = candidate.Trim('@', '?', '$', '[', ']');

            string? expr = null;
            if (parent.Fields.OfType<FormulaField>().Any(f =>
                    string.Equals(NormalizeFieldName(f.Name), candidate, StringComparison.OrdinalIgnoreCase)) ||
                parent.Fields.OfType<DatabaseField>().Any(f =>
                    string.Equals(f.ColumnName, candidate, StringComparison.OrdinalIgnoreCase)))
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

        if (bindings.Count == 0) return;
        w.WriteStartElement("Parameters", RdlNs);
        foreach (var (childParam, parentExpr) in bindings)
        {
            w.WriteStartElement("Parameter", RdlNs);
            w.WriteAttributeString("Name", childParam);
            w.WriteElementString("Value", RdlNs, parentExpr);
            w.WriteEndElement();
        }
        w.WriteEndElement(); // Parameters
    }

    // Crystal cross-tab → SSRS 2005 Matrix. v1 scope: the first row group, first
    // column group, and first summarized cell; additional axes/cells are ignored.
    // RDL Matrix grouping levels are nested outermost-first in document order:
    // each <ColumnGrouping>/<RowGrouping> element is one axis level, so N row
    // fields or M column fields become N/M dynamic grouping levels. When more
    // than one summary cell is defined, they become an extra innermost *static*
    // column level (one <StaticColumn> per cell) — MatrixCells/MatrixColumns
    // count must then equal the cell count (engine rule: count == max(1,
    // ColumnGroupings.StaticCount)), which is why the single loop below over
    // `crossTab.Cells` produces exactly one MatrixCell/MatrixColumn per cell
    // whether there are 1 or many (no separate single-cell code path needed).
    private void WriteMatrix(XmlWriter w, CrossTabObject crossTab, string? hiddenExpr)
    {
        if (crossTab.RowGroupFields.Count == 0 || crossTab.ColumnGroupFields.Count == 0 ||
            crossTab.Cells.Count == 0)
            return;   // a Matrix needs all three parts

        w.WriteStartElement("Matrix", RdlNs);
        w.WriteAttributeString("Name", SanitizeName(crossTab.Name.Length > 0 ? crossTab.Name : $"matrix_{++_textboxCounter}"));
        WriteObjectPosition(w, crossTab.Bounds);
        WriteItemVisibility(w, hiddenExpr);
        w.WriteElementString("DataSetName", RdlNs, "DataSet1");

        w.WriteStartElement("ColumnGroupings", RdlNs);
        foreach (string rawColField in crossTab.ColumnGroupFields)
        {
            string colField = SanitizeName(NormalizeFieldName(rawColField));
            w.WriteStartElement("ColumnGrouping", RdlNs);
            w.WriteElementString("Height", RdlNs, "14pt");
            w.WriteStartElement("DynamicColumns", RdlNs);
            w.WriteStartElement("Grouping", RdlNs);
            w.WriteAttributeString("Name", $"MatrixColumn_{colField}");
            w.WriteStartElement("GroupExpressions", RdlNs);
            w.WriteElementString("GroupExpression", RdlNs, $"=Fields!{colField}.Value");
            w.WriteEndElement(); // GroupExpressions
            w.WriteEndElement(); // Grouping
            w.WriteStartElement("ReportItems", RdlNs);
            WriteMatrixTextbox(w, $"=Fields!{colField}.Value", bold: true);
            w.WriteEndElement(); // ReportItems
            w.WriteEndElement(); // DynamicColumns
            w.WriteEndElement(); // ColumnGrouping
        }
        if (crossTab.Cells.Count > 1)
        {
            w.WriteStartElement("ColumnGrouping", RdlNs);
            w.WriteElementString("Height", RdlNs, "14pt");
            w.WriteStartElement("StaticColumns", RdlNs);
            foreach (var cell in crossTab.Cells)
            {
                w.WriteStartElement("StaticColumn", RdlNs);
                w.WriteStartElement("ReportItems", RdlNs);
                WriteMatrixTextbox(w, CellLabel(cell), bold: true);
                w.WriteEndElement(); // ReportItems
                w.WriteEndElement(); // StaticColumn
            }
            w.WriteEndElement(); // StaticColumns
            w.WriteEndElement(); // ColumnGrouping
        }
        w.WriteEndElement(); // ColumnGroupings

        w.WriteStartElement("RowGroupings", RdlNs);
        foreach (string rawRowField in crossTab.RowGroupFields)
        {
            string rowField = SanitizeName(NormalizeFieldName(rawRowField));
            w.WriteStartElement("RowGrouping", RdlNs);
            w.WriteElementString("Width", RdlNs, "1in");
            w.WriteStartElement("DynamicRows", RdlNs);
            w.WriteStartElement("Grouping", RdlNs);
            w.WriteAttributeString("Name", $"MatrixRow_{rowField}");
            w.WriteStartElement("GroupExpressions", RdlNs);
            w.WriteElementString("GroupExpression", RdlNs, $"=Fields!{rowField}.Value");
            w.WriteEndElement(); // GroupExpressions
            w.WriteEndElement(); // Grouping
            w.WriteStartElement("ReportItems", RdlNs);
            WriteMatrixTextbox(w, $"=Fields!{rowField}.Value", bold: true);
            w.WriteEndElement(); // ReportItems
            w.WriteEndElement(); // DynamicRows
            w.WriteEndElement(); // RowGrouping
        }
        w.WriteEndElement(); // RowGroupings

        w.WriteStartElement("MatrixRows", RdlNs);
        w.WriteStartElement("MatrixRow", RdlNs);
        w.WriteElementString("Height", RdlNs, "14pt");
        w.WriteStartElement("MatrixCells", RdlNs);
        foreach (var cell in crossTab.Cells)
        {
            string cellExpr = BuildSummaryExpression(cell.Function, SanitizeName(NormalizeFieldName(cell.FieldName)));
            w.WriteStartElement("MatrixCell", RdlNs);
            w.WriteStartElement("ReportItems", RdlNs);
            WriteMatrixTextbox(w, cellExpr, bold: false);
            w.WriteEndElement(); // ReportItems
            w.WriteEndElement(); // MatrixCell
        }
        w.WriteEndElement(); // MatrixCells
        w.WriteEndElement(); // MatrixRow
        w.WriteEndElement(); // MatrixRows

        w.WriteStartElement("MatrixColumns", RdlNs);
        foreach (var _ in crossTab.Cells)
        {
            w.WriteStartElement("MatrixColumn", RdlNs);
            w.WriteElementString("Width", RdlNs, "1in");
            w.WriteEndElement(); // MatrixColumn
        }
        w.WriteEndElement(); // MatrixColumns

        w.WriteEndElement(); // Matrix
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
    private void WriteChart(XmlWriter w, ChartObject chart, string? hiddenExpr, ReportDefinition? report)
    {
        w.WriteStartElement("Chart", RdlNs);
        w.WriteAttributeString("Name", SanitizeName(chart.Name.Length > 0 ? chart.Name : $"chart_{++_textboxCounter}"));
        WriteObjectPosition(w, chart.Bounds);
        WriteItemVisibility(w, hiddenExpr);
        WriteChartContent(w, chart, report);
        w.WriteEndElement(); // Chart
    }

    // Chart placed in a group header/footer row of a tabular report — same content as
    // WriteChart but without absolute position (the table grid positions the cell).
    private void WriteChartTableCell(XmlWriter w, ChartObject chart, ReportDefinition? report)
    {
        w.WriteStartElement("TableCell", RdlNs);
        w.WriteStartElement("ReportItems", RdlNs);
        w.WriteStartElement("Chart", RdlNs);
        w.WriteAttributeString("Name", SanitizeName(chart.Name.Length > 0 ? chart.Name : $"chart_{++_textboxCounter}"));
        WriteChartContent(w, chart, report);
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

    private void WriteChartContent(XmlWriter w, ChartObject chart, ReportDefinition? report)
    {
        string seriesField = SanitizeName(ResolveDisplayFieldName(chart.SeriesField, report));
        string valueExpr = BuildSummaryExpression(chart.SeriesFunction, seriesField);

        w.WriteElementString("Type", RdlNs, chart.Kind switch
        {
            ChartKind.Pie => "Pie",
            ChartKind.Bar => "Bar",
            ChartKind.Line => "Line",
            _ => "Column"
        });
        if (chart.Title.Length > 0)
        {
            w.WriteStartElement("Title", RdlNs);
            w.WriteElementString("Caption", RdlNs, chart.Title);
            w.WriteEndElement(); // Title
        }

        w.WriteStartElement("CategoryGroupings", RdlNs);
        foreach (string rawCategoryField in chart.CategoryFields)
        {
            string categoryField = SanitizeName(ResolveDisplayFieldName(rawCategoryField, report));
            w.WriteStartElement("CategoryGrouping", RdlNs);
            w.WriteStartElement("DynamicCategories", RdlNs);
            w.WriteStartElement("Grouping", RdlNs);
            w.WriteAttributeString("Name", $"ChartCategory_{categoryField}");
            w.WriteStartElement("GroupExpressions", RdlNs);
            w.WriteElementString("GroupExpression", RdlNs, $"=Fields!{categoryField}.Value");
            w.WriteEndElement(); // GroupExpressions
            w.WriteEndElement(); // Grouping
            w.WriteElementString("Label", RdlNs, $"=Fields!{categoryField}.Value");
            w.WriteEndElement(); // DynamicCategories
            w.WriteEndElement(); // CategoryGrouping
        }
        w.WriteEndElement(); // CategoryGroupings

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

    private void WriteMatrixTextbox(XmlWriter w, string value, bool bold)
    {
        w.WriteStartElement("Textbox", RdlNs);
        w.WriteAttributeString("Name", $"Textbox_{++_textboxCounter}");
        w.WriteElementString("Value", RdlNs, value);
        if (bold)
        {
            w.WriteStartElement("Style", RdlNs);
            w.WriteElementString("FontWeight", RdlNs, "Bold");
            w.WriteEndElement();
        }
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
    private static void WriteRowVisibility(XmlWriter w, Section section)
    {
        string? expr = TranspileSuppressFormula(section.SuppressFormula)
                       ?? (section.Suppress ? "true" : null);
        if (expr is null) return;
        w.WriteStartElement("Visibility", RdlNs);
        w.WriteElementString("Hidden", RdlNs, expr);
        w.WriteEndElement();
    }

    // Transpile a Crystal suppress formula into an RDL Hidden expression.
    // Returns null when there is no formula or it cannot be transpiled
    // (variable-based formulas fall back to "" — never hide on those).
    private static string? TranspileSuppressFormula(string? crystalFormula) =>
        TranspileSectionFormula(crystalFormula, "SectionSuppress");

    private static string? TranspileNewPageBeforeFormula(string? crystalFormula) =>
        TranspileSectionFormula(crystalFormula, "SectionNewPageBefore");

    private static string? TranspileNewPageAfterFormula(string? crystalFormula) =>
        TranspileSectionFormula(crystalFormula, "SectionNewPageAfter");

    private static string? TranspileBackColorFormula(string? crystalFormula) =>
        TranspileSectionFormula(crystalFormula, "SectionBackColor");

    private static string? TranspileSectionFormula(string? crystalFormula, string debugName)
    {
        if (string.IsNullOrWhiteSpace(crystalFormula)) return null;
        string expr = FormulaTranspiler.ToRdlExpression(new FormulaField
        {
            Name = debugName,
            FormulaText = crystalFormula,
            Syntax = FormulaSyntax.Crystal
        });
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
    // a single RDL function — Crystal's "Percentage of Total" always divides by the
    // DataSet-wide sum here (the inner function from the compound prefix is discarded
    // during parsing, and Crystal's optional custom "divide by" field isn't otherwise
    // distinguishable) — so it needs its own two-part expression rather than a simple
    // "=Func(...)" wrap. Sum() with no explicit scope auto-scopes to the enclosing table
    // group when nested in one, which gives the group's share of the grand total.
    private static string BuildSummaryExpression(AggregateFunction? fn, string sanitizedFieldRef) => fn switch
    {
        null => $"=Fields!{sanitizedFieldRef}.Value",
        AggregateFunction.Percentage =>
            $"=Sum(Fields!{sanitizedFieldRef}.Value) / Sum(Fields!{sanitizedFieldRef}.Value, \"DataSet1\") * 100",
        _ => $"={RdlAggregateFunction(fn)}(Fields!{sanitizedFieldRef}.Value)"
    };

    private static bool IsNumericType(string? dataType) => dataType switch
    {
        "Float64" or "Float32" or "Decimal" or "Currency" or
        "Integer" or "Int16" or "Int32" or "Int64" => true,
        _ => false
    };

    private static string TwipsToRdl(int twips)
    {
        double inches = twips / 1440.0;
        return $"{inches:F3}in";
    }

    private static string MapDataSourceKind(DataSourceKind kind) => kind switch
    {
        DataSourceKind.Odbc => "ODBC",
        DataSourceKind.OleDb => "OLEDB",
        DataSourceKind.Native => "SQL",
        DataSourceKind.Dataset => "SQL",
        _ => "SQL"
    };

    private static string BuildSelectFromTables(DataSource ds)
    {
        if (ds.Tables.Count == 0) return "SELECT * FROM <TableName>";
        var cols = ds.Tables
            .SelectMany(t => t.Columns.Select(c => $"{t.Alias}.{c.Name}"))
            .ToList();
        string select = cols.Count > 0 ? string.Join(", ", cols) : "*";
        string from = string.Join(", ", ds.Tables.Select(t =>
            t.Alias.Length > 0 ? $"{t.Name} {t.Alias}" : t.Name));
        return $"SELECT {select} FROM {from}";
    }

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

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Item1";
        string s = System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
        return char.IsDigit(s[0]) ? "_" + s : s;
    }

    // Map a Crystal group condition to an SSRS group/sort expression.
    // EachValue → direct field reference; date-part conditions wrap with DatePart/Format.
    private static string DatePartGroupExpression(string fieldRef, GroupCondition cond) =>
        cond switch
        {
            GroupCondition.Daily     => $"=Format({fieldRef}, \"yyyy-MM-dd\")",
            GroupCondition.Weekly    => $"=Year({fieldRef}) & \"-W\" & DatePart(\"ww\", {fieldRef})",
            GroupCondition.Monthly   => $"=Format({fieldRef}, \"yyyy-MM\")",
            GroupCondition.Quarterly => $"=Year({fieldRef}) & \"-Q\" & DatePart(\"q\", {fieldRef})",
            GroupCondition.Annually  => $"=Year({fieldRef})",
            _                        => $"={fieldRef}",
        };

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
            // Globals!ReportName is the RDL's own (sanitized, underscored) internal report
            // Name attribute — not Crystal's actual title text. Crystal's real title lives
            // in OLE SummaryInformation (ReportDefinition.ReportTitle); only fall back to
            // Globals!ReportName when that's genuinely absent.
            "report title"         => string.IsNullOrEmpty(reportTitle)
                                        ? "=Globals!ReportName"
                                        : $"={QuoteLiteral(reportTitle)}",
            "record number"        => "=RowNumber()",
            "report comments"      => string.IsNullOrEmpty(reportComments)
                                        ? "\"\""   // empty when no comments in SummaryInfo
                                        : $"={QuoteLiteral(reportComments)}",
            _                      => null,
        };

    // Convert Crystal text with embedded {FieldName} field references to an RDL expression.
    // Pure literal text (no braces) is returned as-is.
    // Mixed text like "Total for {Customer Name}:" becomes:
    //   ="Total for " & Fields!Customer_Name.Value & ":"
    private static string ResolveTextWithFieldRefs(string text, Dictionary<string, string> knownFields,
        Dictionary<string, string>? groupNameMap = null, string? reportComments = null, string? reportTitle = null)
    {
        if (!text.Contains('{')) return text;

        var parts = new List<string>();
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
            string? resolvedRealName = knownFields.TryGetValue(refNorm, out var byNorm) ? byNorm
                : knownFields.TryGetValue(refBare, out var byBare) ? byBare
                : null;
            if (resolvedRealName is not null)
                parts.Add($"Fields!{SanitizeName(resolvedRealName)}.Value");
            else if (groupNameMap is not null && groupNameMap.TryGetValue(refName, out string? groupExpr))
                parts.Add(groupExpr);
            else if (SpecialFieldExpression(refName, reportComments, reportTitle) is string sfe)
                parts.Add(sfe.TrimStart('='));
            else
                parts.Add(QuoteLiteral($"{{{refName}}}"));
            pos = close + 1;
        }

        if (parts.Count == 0) return text;
        // Always build an expression — even when all parts are string literals the resolved
        // content may differ from the original text (e.g. {report comments} → "Annual Summary").
        return "=" + string.Join(" & ", parts);
    }

    private static string QuoteLiteral(string s) =>
        s.Length == 0 ? "\"\"" : $"\"{s.Replace("\"", "\"\"")}\"";

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
            .Select(f => f is DatabaseField db ? db.ColumnName : f.Name)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
}
