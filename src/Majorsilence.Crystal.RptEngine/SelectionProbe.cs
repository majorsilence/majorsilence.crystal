using System.Collections;
using System.Data;
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
/// its parameters, and a table listing a row number, exported to CSV and read back.
/// </summary>
internal static class SelectionProbe
{
    private const string RowIdField = "CrystalRowId";

    /// <summary>
    /// The data with its top or bottom N ranks (<see cref="TableJoiner.AddTopNRanks"/>),
    /// counted over the rows <paramref name="rdl"/>'s record selection keeps. Unchanged for a
    /// report with no top or bottom N group.
    /// </summary>
    public static async Task<DataTable> RankAsync(ReportDefinition report, string rdl, DataTable data, IDictionary? parms)
    {
        if (!report.Groups.Any(g => g.TopN is not null)) return data;
        var selected = await SelectedRowsAsync(rdl, data, parms);
        return TableJoiner.AddTopNRanks(report, data, selected);
    }

    // Null when the selection keeps every row, or cannot be evaluated here: a subreport's
    // selection can read parameters its parent passes per instance. The ranks are then
    // counted over every row.
    private static async Task<ISet<int>?> SelectedRowsAsync(string rdl, DataTable data, IDictionary? parms)
    {
        try
        {
            return await ProbeAsync(rdl, data, parms);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<ISet<int>?> ProbeAsync(string rdl, DataTable data, IDictionary? parms)
    {
        var source = XDocument.Parse(rdl);
        XNamespace ns = source.Root!.Name.Namespace;
        var dataSet = source.Root.Element(ns + "DataSets")?.Elements(ns + "DataSet")
            .FirstOrDefault(d => (string?)d.Attribute("Name") == "DataSet1");
        if (dataSet is null) return null;
        // With no filter on the dataset every row is kept, and nothing needs rendering.
        if (dataSet.Element(ns + "Filters") is null) return null;

        var probeSet = new XElement(dataSet);
        probeSet.Element(ns + "Fields")?.Add(new XElement(ns + "Field", new XAttribute("Name", RowIdField),
            new XElement(ns + "DataField", RowIdField), new XElement(ns + "TypeName", "Int32")));

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
                        new XElement(ns + "TableColumns", new XElement(ns + "TableColumn", new XElement(ns + "Width", "1in"))),
                        new XElement(ns + "Details", new XElement(ns + "TableRows", new XElement(ns + "TableRow",
                            new XElement(ns + "Height", "0.2in"),
                            new XElement(ns + "TableCells", new XElement(ns + "TableCell",
                                new XElement(ns + "ReportItems", new XElement(ns + "Textbox", new XAttribute("Name", "Id"),
                                    new XElement(ns + "Value", $"=Fields!{RowIdField}.Value")))))))))))));

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
            string csv = System.Text.Encoding.UTF8.GetString(((MemoryStream)gen.GetStream()).ToArray());
            var ids = new HashSet<int>();
            foreach (var line in csv.Split('\n'))
                foreach (var cell in line.Split(','))
                    if (int.TryParse(cell.Trim().Trim('"', '\r'), out int id))
                        ids.Add(id);
            return ids;
        }
    }
}
