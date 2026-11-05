using System.Collections;
using System.Data;
using System.Text;
using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine;

/// <summary>
/// Which of the pushed rows the report's own record selection keeps, found by letting the
/// engine apply it. A top or bottom N ranks only those rows, as Crystal does, and the engine
/// gives no public way to read its filtered rows back, so this renders a probe: the report's
/// <c>DataSet1</c> as converted (its query, fields and the filters record selection became),
/// its parameters, and a table listing a row number, exported to CSV and read back. The same
/// table lists the value of each group on a formula, so that runs of a group in original
/// order can be numbered on it.
/// </summary>
internal static class SelectionProbe
{
    private const string RowIdField = "CrystalRowId";

    /// <summary>
    /// The data with its top or bottom N ranks (<see cref="TableJoiner.AddTopNRanks"/>) and
    /// its runs of groups in original order (<see cref="TableJoiner.AddGroupRuns"/>), counted
    /// over the rows <paramref name="rdl"/>'s record selection keeps. Unchanged for a report
    /// with neither.
    /// </summary>
    public static async Task<DataTable> RankAsync(ReportDefinition report, string rdl, DataTable data, IDictionary? parms)
    {
        if (!report.Groups.Any(g => g.TopN is not null || RdlConverter.GroupsByRun(report, g))) return data;
        // Crystal numbers the runs on a formula's value on each record; the engine evaluates
        // the formula, so the probe reads it back for every group on one.
        var formulaGroups = report.Groups.Any(g => RdlConverter.GroupsByRun(report, g))
            ? report.Groups.Select((g, index) => (Group: g, Index: index)).Where(g => g.Group.FieldName.StartsWith('@')).ToList()
            : [];
        var (selected, formulaValues) = await SelectedRowsAsync(rdl, data, parms, formulaGroups);
        return TableJoiner.AddGroupRuns(report, TableJoiner.AddTopNRanks(report, data, selected), selected, formulaValues);
    }

    // The selection null when it keeps every row, or cannot be evaluated here: a subreport's
    // selection can read parameters its parent passes per instance. The ranks are then
    // counted over every row. The formula values null when they cannot be evaluated either,
    // and the groups then break on their values.
    private static async Task<(ISet<int>? Selected, IReadOnlyDictionary<int, IReadOnlyList<object?>>? FormulaValues)> SelectedRowsAsync(
        string rdl, DataTable data, IDictionary? parms, List<(GroupDefinition Group, int Index)> formulaGroups)
    {
        if (formulaGroups.Count > 0)
        {
            try
            {
                return await ProbeAsync(rdl, data, parms, formulaGroups);
            }
            catch (Exception)
            {
                // A formula the probe cannot evaluate leaves the record selection to find alone.
            }
        }
        try
        {
            return ((await ProbeAsync(rdl, data, parms, [])).Selected, null);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static async Task<(ISet<int>? Selected, IReadOnlyDictionary<int, IReadOnlyList<object?>>? FormulaValues)> ProbeAsync(
        string rdl, DataTable data, IDictionary? parms, List<(GroupDefinition Group, int Index)> formulaGroups)
    {
        var source = XDocument.Parse(rdl);
        XNamespace ns = source.Root!.Name.Namespace;
        var dataSet = source.Root.Element(ns + "DataSets")?.Elements(ns + "DataSet")
            .FirstOrDefault(d => (string?)d.Attribute("Name") == "DataSet1");
        if (dataSet is null) return (null, null);
        bool filtered = dataSet.Element(ns + "Filters") is not null;

        // Each formula group's value as the converted report groups on it: the last of the
        // expressions of its Grouping, after any run.
        var expressions = new List<(int Level, string Expression)>();
        foreach (var (group, index) in formulaGroups)
        {
            string? expression = source.Descendants(ns + "Grouping")
                .FirstOrDefault(g => (string?)g.Attribute("Name") == $"Group{index + 1}")
                ?.Element(ns + "GroupExpressions")?.Elements(ns + "GroupExpression").LastOrDefault()?.Value;
            if (expression is null) return (null, null);
            expressions.Add((group.Level, expression));
        }
        // With no filter on the dataset and no formula to evaluate, every row is kept, and
        // nothing needs rendering.
        if (!filtered && expressions.Count == 0) return (null, null);

        var probeSet = new XElement(dataSet);
        probeSet.Element(ns + "Fields")?.Add(new XElement(ns + "Field", new XAttribute("Name", RowIdField),
            new XElement(ns + "DataField", RowIdField), new XElement(ns + "TypeName", "Int32")));

        var cells = expressions.Select(e => e.Expression).Prepend($"=Fields!{RowIdField}.Value")
            .Select((value, i) => new XElement(ns + "TableCell",
                new XElement(ns + "ReportItems", new XElement(ns + "Textbox", new XAttribute("Name", $"Probe{i}"),
                    new XElement(ns + "Value", value)))));
        var probe = new XDocument(new XElement(ns + "Report",
            new XElement(ns + "PageHeight", "11in"),
            new XElement(ns + "PageWidth", "8.5in"),
            new XElement(ns + "Width", "2in"),
            source.Root.Element(ns + "DataSources"),
            new XElement(ns + "DataSets", probeSet),
            source.Root.Element(ns + "ReportParameters"),
            new XElement(ns + "Body",
                new XElement(ns + "Height", "1in"),
                new XElement(ns + "ReportItems",
                    new XElement(ns + "Table", new XAttribute("Name", "Probe"),
                        new XElement(ns + "DataSetName", "DataSet1"),
                        new XElement(ns + "TableColumns", expressions.Select(_ => 0).Prepend(0)
                            .Select(_ => new XElement(ns + "TableColumn", new XElement(ns + "Width", "1in")))),
                        new XElement(ns + "Details", new XElement(ns + "TableRows", new XElement(ns + "TableRow",
                            new XElement(ns + "Height", "0.2in"),
                            new XElement(ns + "TableCells", cells)))))))));

        var withIds = data.Copy();
        var idColumn = withIds.Columns.Add(RowIdField, typeof(int));
        for (int i = 0; i < withIds.Rows.Count; i++)
            withIds.Rows[i][idColumn] = i;

        var parsed = await new RDLParser(probe.ToString()) { SkipDatabaseSchemaValidation = true }.Parse();
        using (parsed)
        {
            await parsed.DataSets["DataSet1"].SetData(withIds);
            await parsed.RunGetData(parms);
            using var gen = new MemoryStreamGen();
            await parsed.RunRender(gen, OutputPresentationType.CSV);
            string csv = Encoding.UTF8.GetString(((MemoryStream)gen.GetStream()).ToArray());
            var ids = new HashSet<int>();
            var values = expressions.Select(_ => new object?[data.Rows.Count]).ToList();
            foreach (var record in CsvRecords(csv))
            {
                if (record.Count != expressions.Count + 1 || !int.TryParse(record[0], out int id)
                    || id < 0 || id >= data.Rows.Count)
                    continue;
                ids.Add(id);
                for (int e = 0; e < expressions.Count; e++)
                    values[e][id] = record[e + 1];
            }
            if (expressions.Count == 0) return (ids, null);
            var byLevel = expressions.Select((e, i) => (e.Level, Values: (IReadOnlyList<object?>)values[i]))
                .ToDictionary(e => e.Level, e => e.Values);
            return (filtered ? ids : null, byLevel);
        }
    }

    // The records of the engine's CSV: fields separated by commas, a quoted field's quotes
    // doubled, records ended by a line feed that is not inside quotes.
    private static IEnumerable<List<string>> CsvRecords(string csv)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { record.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                record.Add(field.ToString());
                field.Clear();
                yield return record;
                record = [];
            }
            else if (c != '\r') field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }
}
