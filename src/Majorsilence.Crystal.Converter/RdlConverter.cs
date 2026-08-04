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
        WriteBody(w, report);
        WritePageHeader(w, report);
        WritePageFooter(w, report);

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
                w.WriteEndElement();
            }
            foreach (var f in formulaFields)
            {
                string safeName = SanitizeName(f.Name.Length > 0 ? f.Name : "Formula");
                string expr = FormulaTranspiler.ToRdlExpression(f);
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
                    : $"Fields!{SanitizeName(f.SummarizedFieldName)}.Value";
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
            w.WriteAttributeString("Name", SanitizeName(p.Name));
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
                w.WriteStartElement("ValidValues", RdlNs);
                w.WriteStartElement("NonQueried", RdlNs);
                w.WriteStartElement("ParameterValues", RdlNs);
                foreach (var (value, label) in p.PickListValues)
                {
                    w.WriteStartElement("ParameterValue", RdlNs);
                    w.WriteElementString("Value", RdlNs, value);
                    w.WriteElementString("Label", RdlNs, label);
                    w.WriteEndElement();
                }
                w.WriteEndElement(); // ParameterValues
                w.WriteEndElement(); // NonQueried
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

    private void WriteBody(XmlWriter w, ReportDefinition report)
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

        w.WriteStartElement("Body", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(
            detailsSections.Sum(s => s.HeightTwips) + 720));

        w.WriteStartElement("ReportItems", RdlNs);

        // Emit a Table if we have details and at least one column worth of objects
        var detailObjects = detailsSections.SelectMany(s => s.Objects).ToList();
        var consumedByTable = new List<ReportObject>();
        if (detailObjects.Count > 0)
            WriteDetailsTable(w, report, detailsSections, groupHeaders, groupFooters, consumedByTable);

        // Emit free-form text/field objects from non-detail body sections.
        // When a Table was emitted, GroupHeader/GroupFooter content is already inside
        // the TableGroup Header/Footer rows — exclude them to avoid duplication.
        // When there is no table, include them so their TextObjects still appear.
        bool hasTable = detailObjects.Count > 0;
        foreach (var section in report.Sections.Where(s =>
            s.Type != SectionType.Details &&
            s.Type != SectionType.PageHeader &&
            s.Type != SectionType.PageFooter &&
            (s.Type != SectionType.GroupHeader || !hasTable) &&
            (s.Type != SectionType.GroupFooter || !hasTable)))
        {
            WriteFreeFormObjects(w, section, report);
        }

        // Non-field objects (subreports, images) placed in group sections that the
        // table rows had no empty cell for — emit as positioned body items so they
        // are not silently dropped.
        if (hasTable)
        {
            foreach (var section in report.Sections.Where(s =>
                s.Type is SectionType.GroupHeader or SectionType.GroupFooter))
            {
                var leftovers = section.Objects
                    .Where(o => o is SubreportObject { Report: not null } or ImageObject)
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
        w.WriteEndElement(); // Body
    }

    private void WriteDetailsTable(XmlWriter w, ReportDefinition report,
        List<Section> detailsSections, List<Section> groupHeaders, List<Section> groupFooters,
        List<ReportObject> consumedExtras)
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

        // Set of DataSet field names (sanitized): used to guard detail-row cell references.
        var runningTotalNames = report.Fields.OfType<RunningTotalField>()
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dataSetSafeNames = dbFields.Select(f => SanitizeName(f.ColumnName))
            .Concat(formulaFieldNames.Select(SanitizeName))
            .Concat(runningTotalNames.Select(SanitizeName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        var knownFieldsForGroups = report.Fields
            .Select(f => f is DatabaseField db ? db.ColumnName : f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

                w.WriteStartElement("TableGroup", RdlNs);

                string fieldRef = $"Fields!{SanitizeName(grpFieldNorm)}.Value";
                string groupExpr = DatePartGroupExpression(fieldRef, grp.Condition);

                w.WriteStartElement("Grouping", RdlNs);
                w.WriteAttributeString("Name", $"Group{gi + 1}");
                w.WriteStartElement("GroupExpressions", RdlNs);
                w.WriteElementString("GroupExpression", RdlNs, groupExpr);
                w.WriteEndElement(); // GroupExpressions
                w.WriteEndElement(); // Grouping

                string sortDir = grp.SortOrder == GroupSortOrder.Descending ? "Descending" : "Ascending";
                w.WriteStartElement("SortExpressions", RdlNs);
                w.WriteStartElement("SortExpression", RdlNs);
                w.WriteElementString("Value", RdlNs, groupExpr);
                w.WriteElementString("Direction", RdlNs, sortDir);
                w.WriteEndElement(); // SortExpression
                w.WriteEndElement(); // SortExpressions

                if (ghSection is not null)
                {
                    // Use TextObject content from the GroupHeader section when available
                    var ghTextObj = ghSection.Objects.OfType<TextObject>()
                        .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));
                    string ghCellValue = ghTextObj is not null
                        ? ResolveTextWithFieldRefs(ghTextObj.Text, knownFieldsForGroups, groupNameMapForTable, report.ReportComments)
                        : $"=Fields!{SanitizeName(grpFieldNorm)}.Value";
                    ObjectFormat? ghFormat = ghTextObj?.Format;

                    // Non-field objects in the group header (subreports, images) get
                    // placed into whatever cells would otherwise be empty.
                    var ghExtras = QueueGroupRowExtras(ghSection, ghTextObj);

                    w.WriteStartElement("Header", RdlNs);
                    w.WriteElementString("RepeatOnNewPage", RdlNs, "false");
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
                            WriteTableCell(w, ghFo.SummaryFunction is not null
                                ? $"={RdlAggregateFunction(ghFo.SummaryFunction)}(Fields!{ghField}.Value)"
                                : $"=Fields!{ghField}.Value", ghFo.Format);
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
                            cellValue = $"={RdlAggregateFunction(fo.SummaryFunction)}(Fields!{SanitizeName(foNorm)}.Value)";
                        else if (fo is not null && SpecialFieldExpression(fo.FieldName, report.ReportComments) is string sfe)
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
            string safeName = SanitizeName(columns[ci]);
            string cellVal = dataSetSafeNames.Contains(safeName)
                ? $"=Fields!{safeName}.Value"
                : string.Empty;  // running total / unrecognised field — no DataSet entry yet
            WriteTableCell(w, cellVal, fo?.Format);
        }
        foreach (var img in detailImageObjects)
            WriteImageTableCell(w, img);
        w.WriteEndElement(); // TableCells
        w.WriteEndElement(); // TableRow
        w.WriteEndElement(); // TableRows
        w.WriteEndElement(); // Details

        w.WriteEndElement(); // Table
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

        var knownFields = report?.Fields
            .Select(f => f is DatabaseField db ? db.ColumnName : f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>();

        // Build "Group #N Name" → Fields!GroupField.Value lookup from report groups
        var groupNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (report is not null)
        {
            for (int gi = 0; gi < report.Groups.Count; gi++)
                groupNameMap[$"Group #{gi + 1} Name"] = $"Fields!{SanitizeName(NormalizeFieldName(report.Groups[gi].FieldName))}.Value";
        }

        foreach (var obj in section.Objects)
        {
            switch (obj)
            {
                case TextObject text:
                    w.WriteStartElement("Textbox", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(text.Name.Length > 0 ? text.Name : $"text_{++_textboxCounter}"));
                    WriteObjectPosition(w, text.Bounds);
                    WriteItemVisibility(w, hiddenExpr);
                    w.WriteElementString("Value", RdlNs, ResolveTextWithFieldRefs(text.Text, knownFields, groupNameMap, report?.ReportComments ?? string.Empty));
                    w.WriteElementString("CanGrow", RdlNs, "true");
                    WriteObjectStyle(w, text.Format);
                    w.WriteEndElement();
                    break;

                case FieldObject field:
                    w.WriteStartElement("Textbox", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(field.Name.Length > 0 ? field.Name : $"field_{++_textboxCounter}"));
                    WriteObjectPosition(w, field.Bounds);
                    WriteItemVisibility(w, hiddenExpr);
                    // Only emit a field expression when the field exists in the DataSet
                    string fieldValue;
                    // Strip @/# prefix for Crystal formula/running-total field references
                    string lookupName = NormalizeFieldName(field.FieldName);
                    if (knownFields.Contains(lookupName))
                        // Summary fields (grand totals in report header/footer) aggregate
                        // over the whole DataSet scope; plain fields render the raw value.
                        fieldValue = field.SummaryFunction is not null
                            ? $"={RdlAggregateFunction(field.SummaryFunction)}(Fields!{SanitizeName(lookupName)}.Value)"
                            : $"=Fields!{SanitizeName(lookupName)}.Value";
                    else if (groupNameMap.TryGetValue(field.FieldName, out string? groupFieldExpr))
                        fieldValue = "=" + groupFieldExpr;
                    else if (SpecialFieldExpression(field.FieldName, report?.ReportComments ?? string.Empty) is string specialExpr)
                        fieldValue = specialExpr;
                    else
                        fieldValue = $"[{field.FieldName}]";
                    w.WriteElementString("Value", RdlNs, fieldValue);
                    w.WriteElementString("CanGrow", RdlNs, "true");
                    WriteObjectStyle(w, field.Format);
                    w.WriteEndElement();
                    break;

                case CrossTabObject crossTab:
                    WriteMatrix(w, crossTab, hiddenExpr);
                    break;

                case LineObject line:
                    w.WriteStartElement("Line", RdlNs);
                    w.WriteAttributeString("Name", SanitizeName(line.Name.Length > 0 ? line.Name : $"line_{++_textboxCounter}"));
                    WriteObjectPosition(w, line.Bounds);
                    WriteItemVisibility(w, hiddenExpr);
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
                    WriteItemVisibility(w, hiddenExpr);
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
                    WriteItemVisibility(w, hiddenExpr);
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
                    WriteItemVisibility(w, hiddenExpr);
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

    private void WritePageHeader(XmlWriter w, ReportDefinition report)
    {
        var section = report.Sections.FirstOrDefault(s => s.Type == SectionType.PageHeader);
        if (section is null) return;

        w.WriteStartElement("PageHeader", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(section.HeightTwips));
        w.WriteElementString("PrintOnFirstPage", RdlNs, "true");
        w.WriteElementString("PrintOnLastPage", RdlNs, "true");
        w.WriteStartElement("ReportItems", RdlNs);
        WriteFreeFormObjects(w, section, report);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private void WritePageFooter(XmlWriter w, ReportDefinition report)
    {
        var section = report.Sections.FirstOrDefault(s => s.Type == SectionType.PageFooter);
        if (section is null) return;

        w.WriteStartElement("PageFooter", RdlNs);
        w.WriteElementString("Height", RdlNs, TwipsToRdl(section.HeightTwips));
        w.WriteElementString("PrintOnFirstPage", RdlNs, "true");
        w.WriteElementString("PrintOnLastPage", RdlNs, "true");
        w.WriteStartElement("ReportItems", RdlNs);
        WriteFreeFormObjects(w, section, report);
        w.WriteEndElement();
        w.WriteEndElement();
    }

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
                expr = $"=Parameters!{SanitizeName(parentParam.Name)}.Value";

            if (expr is not null)
                bindings.Add((SanitizeName(childParam.Name), expr));
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
    private void WriteMatrix(XmlWriter w, CrossTabObject crossTab, string? hiddenExpr)
    {
        if (crossTab.RowGroupFields.Count == 0 || crossTab.ColumnGroupFields.Count == 0 ||
            crossTab.Cells.Count == 0)
            return;   // a Matrix needs all three parts

        string rowField = SanitizeName(NormalizeFieldName(crossTab.RowGroupFields[0]));
        string colField = SanitizeName(NormalizeFieldName(crossTab.ColumnGroupFields[0]));
        var cell = crossTab.Cells[0];
        string cellExpr = $"={RdlAggregateFunction(cell.Function)}(Fields!{SanitizeName(NormalizeFieldName(cell.FieldName))}.Value)";

        w.WriteStartElement("Matrix", RdlNs);
        w.WriteAttributeString("Name", SanitizeName(crossTab.Name.Length > 0 ? crossTab.Name : $"matrix_{++_textboxCounter}"));
        WriteObjectPosition(w, crossTab.Bounds);
        WriteItemVisibility(w, hiddenExpr);
        w.WriteElementString("DataSetName", RdlNs, "DataSet1");

        w.WriteStartElement("ColumnGroupings", RdlNs);
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
        w.WriteEndElement(); // ColumnGroupings

        w.WriteStartElement("RowGroupings", RdlNs);
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
        w.WriteEndElement(); // RowGroupings

        w.WriteStartElement("MatrixRows", RdlNs);
        w.WriteStartElement("MatrixRow", RdlNs);
        w.WriteElementString("Height", RdlNs, "14pt");
        w.WriteStartElement("MatrixCells", RdlNs);
        w.WriteStartElement("MatrixCell", RdlNs);
        w.WriteStartElement("ReportItems", RdlNs);
        WriteMatrixTextbox(w, cellExpr, bold: false);
        w.WriteEndElement(); // ReportItems
        w.WriteEndElement(); // MatrixCell
        w.WriteEndElement(); // MatrixCells
        w.WriteEndElement(); // MatrixRow
        w.WriteEndElement(); // MatrixRows

        w.WriteStartElement("MatrixColumns", RdlNs);
        w.WriteStartElement("MatrixColumn", RdlNs);
        w.WriteElementString("Width", RdlNs, "1in");
        w.WriteEndElement(); // MatrixColumn
        w.WriteEndElement(); // MatrixColumns

        w.WriteEndElement(); // Matrix
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
    private static string? TranspileSuppressFormula(string? crystalFormula)
    {
        if (string.IsNullOrWhiteSpace(crystalFormula)) return null;
        string expr = FormulaTranspiler.ToRdlExpression(new FormulaField
        {
            Name = "SectionSuppress",
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
        _ => "Sum"   // null (plain numeric column heuristic) and Sum
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
    private static string? SpecialFieldExpression(string fieldName, string? reportComments = null) =>
        fieldName.ToLowerInvariant() switch
        {
            "page number"          => "=Globals!PageNumber",
            "total page count"     => "=Globals!TotalPages",
            "page n of m"          => "=\"Page \" & Globals!PageNumber & \" of \" & Globals!TotalPages",
            "print date"           => "=Format(Globals!ExecutionTime, \"d\")",
            "print time"           => "=Format(Globals!ExecutionTime, \"T\")",
            "modification date"    => "=Format(Globals!ExecutionTime, \"d\")",
            "report title"         => "=Globals!ReportName",
            "record number"        => "=Globals!RowNumber",
            "report comments"      => string.IsNullOrEmpty(reportComments)
                                        ? "\"\""   // empty when no comments in SummaryInfo
                                        : $"={QuoteLiteral(reportComments)}",
            _                      => null,
        };

    // Convert Crystal text with embedded {FieldName} field references to an RDL expression.
    // Pure literal text (no braces) is returned as-is.
    // Mixed text like "Total for {Customer Name}:" becomes:
    //   ="Total for " & Fields!Customer_Name.Value & ":"
    private static string ResolveTextWithFieldRefs(string text, HashSet<string> knownFields,
        Dictionary<string, string>? groupNameMap = null, string? reportComments = null)
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
            string resolvedRef = knownFields.Contains(refNorm) ? refNorm
                : knownFields.Contains(refBare) ? refBare
                : refNorm;
            if (knownFields.Contains(resolvedRef))
                parts.Add($"Fields!{SanitizeName(resolvedRef)}.Value");
            else if (groupNameMap is not null && groupNameMap.TryGetValue(refName, out string? groupExpr))
                parts.Add(groupExpr);
            else if (SpecialFieldExpression(refName, reportComments) is string sfe)
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
}
