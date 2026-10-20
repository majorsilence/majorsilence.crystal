using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrystalDecisions.CrystalReports.Engine;
using CrystalDecisions.ReportAppServer.Controllers;
using CrystalDecisions.ReportAppServer.DataDefModel;

namespace Majorsilence.Crystal.ReferenceRenderer
{
    /// <summary>
    /// --rows: the rows saved with a report, as a data fixture for tests/reference-data/.
    ///
    /// They come from the runtime's own rowset (RowsetController), with every database field
    /// of every table asked for, so a fixture holds each column the report saved, typed as
    /// the runtime reads it, not only the ones it displays. A grouped report's cursor gives
    /// one row per group rather than one per record, so the groups are removed from the
    /// in-memory copy first; nothing is saved. Group and record selection are not applied
    /// here: the fixture is the raw saved rows, and the pipeline under test applies them.
    ///
    /// Columns that hold no value in any row are left out. A column is named as the existing
    /// fixtures name them, by its bare name, or "Table.Column" where two tables share the
    /// name. Dates are written yyyy-MM-dd, with the time after them when there is one.
    ///
    /// A subreport's rows go beside the main report's, in a file named for the subreport.
    ///
    /// For the ten reports that had fixtures built from the ExcelDataOnly export
    /// (FixtureBuilder), these give the same rows with the same values on every column those
    /// had, and more columns. Unlike that export, they also work for a report whose details
    /// are a master/detail layout.
    /// </summary>
    internal static class SavedRows
    {
        public static int Run(string rptPath, string outputCsvPath)
        {
            if (!File.Exists(rptPath))
            {
                Console.Error.WriteLine($"Not found: {rptPath}");
                return 1;
            }

            using (var doc = new ReportDocument())
            {
                doc.Load(rptPath);
                var rcd = doc.ReportClientDocument;
                Write(rcd.DataDefController, rcd.DatabaseController, rcd.RowsetController, outputCsvPath);

                // Each subreport's rows beside the main report's, as
                // <fixture>.sub.<the name the runtime gives the subreport>.csv, which is the
                // name SubreportData takes them by.
                string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputCsvPath))!,
                    Path.GetFileNameWithoutExtension(outputCsvPath));
                foreach (ReportDocument sub in doc.Subreports)
                {
                    var sc = rcd.SubreportController.GetSubreport(sub.Name);
                    Write(sc.DataDefController, sc.DatabaseController, sc.RowsetController,
                        $"{stem}.sub.{sub.Name}.csv");
                }
            }
            return 0;
        }

        private static void Write(DataDefController dataDef, DatabaseController database,
            RowsetController rowset, string outputCsvPath)
        {
            while (dataDef.DataDefinition.Groups.Count > 0)
                dataDef.GroupController.Remove(dataDef.DataDefinition.Groups[0]);

            var metaData = new RowsetMetaData();
            var fields = new Fields();
            var columns = new List<(string Table, string Name)>();
            foreach (ISCRTable table in database.Database.Tables)
                foreach (Field field in table.DataFields)
                {
                    fields.Add(field);
                    columns.Add((table.Alias, field.Name));
                }
            metaData.DataFields = fields;

            var rows = new List<object[]>();
            var cursor = rowset.CreateCursor(new GroupPath(), metaData, 1000);
            cursor.MoveFirst();
            while (!cursor.IsEOF)
            {
                var record = cursor.CurrentRecord;
                rows.Add(Enumerable.Range(0, columns.Count).Select(i => record[i]).ToArray());
                cursor.MoveNext();
            }

            var used = Enumerable.Range(0, columns.Count)
                .Where(i => rows.Any(r => r[i] != null && !(r[i] is string s && s.Length == 0)))
                .ToList();
            var shared = new HashSet<string>(columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", used.Select(i =>
                Quote(shared.Contains(columns[i].Name) ? columns[i].Table + "." + columns[i].Name : columns[i].Name))));
            foreach (var row in rows)
                sb.AppendLine(string.Join(",", used.Select(i => Quote(Format(row[i])))));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputCsvPath))!);
            File.WriteAllText(outputCsvPath, sb.ToString());
            Console.WriteLine($"Wrote {outputCsvPath} ({rows.Count} rows, {used.Count} columns)");
        }

        private static string Format(object? value) => value switch
        {
            null => "",
            DateTime d => d.TimeOfDay == TimeSpan.Zero
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
