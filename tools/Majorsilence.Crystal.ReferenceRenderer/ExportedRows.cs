using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrystalDecisions.CrystalReports.Engine;
using CrystalDecisions.ReportAppServer.DataDefModel;
using CrystalDecisions.Shared;
using RasFieldObject = CrystalDecisions.ReportAppServer.ReportDefModel.FieldObject;

namespace Majorsilence.Crystal.ReferenceRenderer
{
    /// <summary>
    /// --rows for a report whose saved rows the rowset will not give: the runtime's own CSV
    /// export of them, with every database column the report uses placed in its details.
    ///
    /// For some reports the rowset cursor fails on every request, one table or several,
    /// with "Failed to load database information" or "Failed to open the connection", though
    /// the report holds saved data and exports from it: BigCells, ProductTypeSales-Grouped,
    /// StatementOfAccount and the meridian and synthetic probes. So, in the in-memory copy
    /// only: each used column goes in the details as a field object, the details are shown,
    /// and the report is exported as CSV with its group, page and report sections left out,
    /// one line per printed record. The export prints values in their display formats, so
    /// numbers and dates are read back by the column's type into the form SavedRows writes.
    ///
    /// It holds what the report prints: records the record and group selections keep, and
    /// with a top or bottom N only the groups shown. A column no record has a value for is
    /// left out, and columns are named as SavedRows names them. Subreports are not written.
    /// </summary>
    internal static class ExportedRows
    {
        public static void Write(ReportDocument doc, string outputCsvPath)
        {
            var rcd = doc.ReportClientDocument;
            var detailArea = rcd.ReportDefController.ReportDefinition.DetailArea;
            var details = detailArea.Sections[0];
            // The export orders a line's values by where their objects sit, so the details'
            // own objects would fall among the placed ones; they are taken out.
            foreach (CrystalDecisions.ReportAppServer.ReportDefModel.Section section in detailArea.Sections)
                foreach (var obj in section.ReportObjects.Cast<CrystalDecisions.ReportAppServer.ReportDefModel.ReportObject>().ToList())
                    rcd.ReportDefController.ReportObjectController.Remove(obj);
            var columns = new List<(string Table, string Name, CrFieldValueTypeEnum Type)>();
            int left = 0;
            foreach (ISCRTable table in rcd.DatabaseController.Database.Tables)
                foreach (Field field in table.DataFields)
                {
                    // A picture, blob or other non-text column exports nothing, which would
                    // leave every line a value short of the placed columns.
                    if (field.UseCount <= 0 || !Scalar(field.Type)) continue;
                    var fo = new RasFieldObject
                    {
                        DataSourceName = field.FormulaForm,
                        FieldValueType = field.Type,
                        Left = left, Top = 0, Width = 50, Height = 200,
                    };
                    rcd.ReportDefController.ReportObjectController.Add(fo, details, -1);
                    columns.Add((table.Alias, field.Name, field.Type));
                    left += 60;
                }

            foreach (Area area in doc.ReportDefinition.Areas)
                if (area.Kind == AreaSectionKind.Detail)
                {
                    area.AreaFormat.EnableSuppress = false;
                    area.AreaFormat.EnableHideForDrillDown = false;
                    foreach (Section s in area.Sections)
                        s.SectionFormat.EnableSuppress = false;
                }

            string exported = Path.GetTempFileName();
            try
            {
                var options = new ExportOptions
                {
                    ExportFormatType = ExportFormatType.CharacterSeparatedValues,
                    ExportDestinationType = ExportDestinationType.DiskFile,
                    ExportDestinationOptions = new DiskFileDestinationOptions { DiskFileName = exported },
                    ExportFormatOptions = new CharacterSeparatedValuesFormatOptions
                    {
                        ExportMode = CsvExportMode.Standard,
                        GroupSectionsOption = CsvExportSectionsOption.DoNotExport,
                        ReportSectionsOption = CsvExportSectionsOption.DoNotExport,
                        SeparatorText = ",",
                        Delimiter = "\"",
                    },
                };
                doc.Export(options);

                var lines = ParseCsv(File.ReadAllText(exported));
                if (lines.Count > 0 && lines.All(r => r.Count != columns.Count))
                    Console.Error.WriteLine($"No line has the {columns.Count} values placed; the first has {lines[0].Count}: "
                        + string.Join(" | ", lines[0].Take(20)));
                var rows = lines
                    .Where(r => r.Count == columns.Count)
                    .Select(r => r.Select((v, i) => Normalize(v, columns[i].Type)).ToArray())
                    .ToList();

                var used = Enumerable.Range(0, columns.Count).Where(i => rows.Any(r => r[i].Length > 0)).ToList();
                var shared = new HashSet<string>(columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);

                var sb = new StringBuilder();
                sb.AppendLine(string.Join(",", used.Select(i =>
                    Quote(shared.Contains(columns[i].Name) ? columns[i].Table + "." + columns[i].Name : columns[i].Name))));
                foreach (var row in rows)
                    sb.AppendLine(string.Join(",", used.Select(i => Quote(row[i]))));

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputCsvPath))!);
                File.WriteAllText(outputCsvPath, sb.ToString());
                Console.WriteLine($"Wrote {outputCsvPath} ({rows.Count} rows, {used.Count} columns, from the CSV export)");
            }
            finally
            {
                File.Delete(exported);
            }
        }

        private static bool Scalar(CrFieldValueTypeEnum type) => type switch
        {
            CrFieldValueTypeEnum.crFieldValueTypeNumberField or CrFieldValueTypeEnum.crFieldValueTypeCurrencyField
                or CrFieldValueTypeEnum.crFieldValueTypeInt8sField or CrFieldValueTypeEnum.crFieldValueTypeInt8uField
                or CrFieldValueTypeEnum.crFieldValueTypeInt16sField or CrFieldValueTypeEnum.crFieldValueTypeInt16uField
                or CrFieldValueTypeEnum.crFieldValueTypeInt32sField or CrFieldValueTypeEnum.crFieldValueTypeInt32uField
                or CrFieldValueTypeEnum.crFieldValueTypeDateField or CrFieldValueTypeEnum.crFieldValueTypeDateTimeField
                or CrFieldValueTypeEnum.crFieldValueTypeTimeField or CrFieldValueTypeEnum.crFieldValueTypeStringField
                or CrFieldValueTypeEnum.crFieldValueTypeBooleanField => true,
            _ => false,
        };

        // A displayed value back to the form SavedRows writes: a number plain and invariant,
        // a date yyyy-MM-dd with the time after it when there is one. The export formats by
        // this machine's culture, so that is the culture it is read back in.
        private static string Normalize(string value, CrFieldValueTypeEnum type)
        {
            switch (type)
            {
                case CrFieldValueTypeEnum.crFieldValueTypeNumberField:
                case CrFieldValueTypeEnum.crFieldValueTypeCurrencyField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt8sField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt8uField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt16sField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt16uField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt32sField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt32uField:
                    return decimal.TryParse(value, NumberStyles.Currency, CultureInfo.CurrentCulture, out var number)
                        ? number.ToString(CultureInfo.InvariantCulture)
                        : value;
                case CrFieldValueTypeEnum.crFieldValueTypeDateField:
                case CrFieldValueTypeEnum.crFieldValueTypeDateTimeField:
                    return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d)
                        ? (d.TimeOfDay == TimeSpan.Zero
                            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                            : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                        : value;
                default:
                    return value;
            }
        }

        // RFC 4180 lines: fields separated by commas, quoted with doubled quotes inside, a
        // quoted field free to hold commas and line breaks.
        private static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c != '"') field.Append(c);
                    else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(field.ToString()); field.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                }
                else field.Append(c);
            }
            if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
            return rows;
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
