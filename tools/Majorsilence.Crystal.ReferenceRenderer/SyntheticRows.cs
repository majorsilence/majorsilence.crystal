using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CrystalDecisions.CrystalReports.Engine;
using CrystalDecisions.ReportAppServer.DataDefModel;
using CrystalDecisions.ReportAppServer.DataSetConversion;
using CrystalDecisions.Shared;
using ParameterField = CrystalDecisions.Shared.ParameterField;

namespace Majorsilence.Crystal.ReferenceRenderer
{
    /// <summary>
    /// --synthetic: rows made up for a report that saved none and whose database is not here,
    /// handed to the runtime as each table's data source, so that it renders a reference from
    /// them; the same rows are written as the data fixture, so ours renders from them too.
    ///
    /// Every table gets the same number of rows, and the fields a link joins - between two
    /// tables, or between a subreport and its parent - share their values row for row: the
    /// first row's key is 1 in each, the second's 2. So each join pairs row i with row i, and
    /// the fixture, which is the joined rows (as SavedRows writes them), is the tables' rows
    /// side by side. A table no link reaches is given one row, so that the runtime's cross
    /// join of it adds no rows. Other values are made from the field's name and type, a few
    /// to a column so a group on one has several records.
    ///
    /// A parameter the report needs is given a value: where the record selection compares it
    /// with a field, a value of that field - the first row's for an equality, the smallest or
    /// largest for a bound, the whole span for a range - so the selection keeps records; with
    /// a formula, a bound every value passes; otherwise its first default, or a plain value
    /// of its type.
    ///
    /// The fixture holds the raw rows, before any selection, as SavedRows' do. Each
    /// subreport's go beside it, named for the subreport, and the main report's parameter
    /// values in a .params.csv.
    ///
    /// What it cannot do: a Command's own parameters go when its rows are replaced, so a
    /// formula that reads one fails (the boyum SAP reports); SQL Expression fields need the
    /// database; and the runtime gives up joining some deep chains of tables.
    /// </summary>
    internal static class SyntheticRows
    {
        private sealed class TableGen
        {
            public string Report = "";
            public string Alias = "";
            public List<(string Name, CrFieldValueTypeEnum Type, int Length)> Fields = new List<(string, CrFieldValueTypeEnum, int)>();
            public DataTable Data = new DataTable();
        }

        public static int Run(string rptPath, string outputPngPath, string outputCsvPath, int rowCount, string printer)
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

                var keys = new UnionFind();
                var tables = new List<TableGen>();
                var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var linkedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                Collect("", rcd.DatabaseController.Database, tables, keys, connected);
                foreach (ReportDocument sub in doc.Subreports)
                {
                    var sc = rcd.SubreportController.GetSubreport(sub.Name);
                    Collect(sub.Name, sc.DatabaseController.Database, tables, keys, connected);
                    foreach (CrystalDecisions.ReportAppServer.ReportDefModel.ISCRSubreportLink link in
                             rcd.SubreportController.GetSubreportLinks(sub.Name))
                    {
                        linkedParameters.Add(sub.Name + "|" + link.LinkedParameterName.Trim('{', '}', '?'));
                        if (SplitRef(link.MainReportFieldName) is { } main && SplitRef(link.SubreportFieldName) is { } subField)
                            keys.Union(Key("", main.Table, main.Field), Key(sub.Name, subField.Table, subField.Field));
                    }
                }

                // Dates start in the earliest year a record selection names, so that a
                // selection on a span of years - the meridian probes keep 2022 to 2023 - keeps
                // records.
                var years = new[] { doc.DataDefinition.RecordSelectionFormula }
                    .Concat(doc.Subreports.Cast<ReportDocument>().Select(r => r.DataDefinition.RecordSelectionFormula))
                    .SelectMany(f => Regex.Matches(f ?? "", @"Date(?:Time)?\s*\(\s*(\d{4})").Cast<Match>())
                    .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
                _origin = new DateTime(years.Count > 0 ? years.Min() : 2024, 1, 1);

                foreach (var t in tables)
                {
                    int rows = connected.Contains(t.Report + "|" + t.Alias) ? rowCount : 1;
                    Fill(t, rows, keys);
                }

                // Each table's rows go in through the report's own database controller, which
                // names the table by its alias. The engine's Table.SetDataSource finds a table's
                // data by its location, which two Command tables share ("Command"), so each
                // took the last one's rows and Dunning's CompanyInfo lost its Name; a DataSet
                // for the whole report bound no Command table at all.
                foreach (var t in tables)
                {
                    t.Data.TableName = t.Alias;
                    var set = new System.Data.DataSet();
                    set.Tables.Add(t.Data);
                    try
                    {
                        var controller = t.Report.Length == 0
                            ? rcd.DatabaseController
                            : rcd.SubreportController.GetSubreport(t.Report).DatabaseController;
                        controller.SetDataSource(DataSetConverter.Convert(set), t.Alias, t.Alias);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"table {(t.Report.Length == 0 ? "" : t.Report + ".")}{t.Alias}: {ex.Message.Trim()}");
                    }
                    set.Tables.Remove(t.Data);
                }

                // Every parameter, a subreport's among them, is read from the main report: a
                // subreport's own definitions cannot say whether they are linked.
                string? Selection(string report) => report.Length == 0
                    ? doc.DataDefinition.RecordSelectionFormula
                    : doc.Subreports[report].DataDefinition.RecordSelectionFormula;
                var chosen = new List<(string Name, object Value)>();
                foreach (ParameterField p in doc.ParameterFields)
                    if (!linkedParameters.Contains((p.ReportName ?? "") + "|" + p.Name))
                        SetParameter(doc, p, Selection(p.ReportName ?? ""), tables.Where(t => t.Report == (p.ReportName ?? "")).ToList(), chosen);

                WriteFixture(tables.Where(t => t.Report.Length == 0).ToList(), outputCsvPath);
                string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputCsvPath))!,
                    Path.GetFileNameWithoutExtension(outputCsvPath));
                // The main report's parameter values, which a report often prints, so the render
                // under test is given the same ones: name and value, a range left out.
                File.WriteAllText($"{stem}.params.csv", string.Concat(chosen.Select(c =>
                    Quote(c.Name) + "," + Quote(Format(c.Value)) + Environment.NewLine)));
                foreach (var name in tables.Select(t => t.Report).Where(r => r.Length > 0).Distinct())
                    WriteFixture(tables.Where(t => t.Report == name).ToList(), $"{stem}.sub.{name}.csv");

                doc.PrintOptions.PrinterName = printer;
                string accepted = doc.PrintOptions.PrinterName;
                Console.Error.WriteLine(accepted.Length == 0
                    ? "report is not bound to a printer; formatted from its own page setup"
                    : $"formatted against printer: {accepted}");
                if (accepted.Length > 0 && accepted != printer)
                    throw new InvalidOperationException($"Crystal did not accept printer \"{printer}\"; it reports \"{accepted}\".");

                using (var pdf = doc.ExportToStream(ExportFormatType.PortableDocFormat))
                using (var buffer = new MemoryStream())
                {
                    pdf.CopyTo(buffer);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);
                    long length = buffer.Length;
                    buffer.Position = 0;
                    PDFtoImage.Conversion.SavePng(outputPngPath, buffer, page: 0);
                    Console.WriteLine($"Wrote {outputPngPath} ({length} PDF bytes rasterized)");
                }
                doc.Close();
            }
            return 0;
        }

        // A report's tables and the links between them. Tables the first one's links reach,
        // directly or through others, are marked connected; in a report of one table, it is.
        private static void Collect(string report, CrystalDecisions.ReportAppServer.DataDefModel.Database database, List<TableGen> tables, UnionFind keys,
            HashSet<string> connected)
        {
            var mine = new List<TableGen>();
            foreach (ISCRTable table in database.Tables)
            {
                var t = new TableGen { Report = report, Alias = table.Alias };
                foreach (ISCRField field in table.DataFields)
                {
                    int length = field is DBField db ? db.Length : 0;
                    t.Fields.Add((field.Name, field.Type, length));
                }
                mine.Add(t);
            }
            tables.AddRange(mine);
            if (mine.Count == 0) return;

            var adjacent = mine.ToDictionary(t => t.Alias, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            foreach (ISCRTableLink link in database.TableLinks)
            {
                var source = link.SourceFieldNames.Cast<string>().ToList();
                var target = link.TargetFieldNames.Cast<string>().ToList();
                for (int i = 0; i < Math.Min(source.Count, target.Count); i++)
                    keys.Union(Key(report, link.SourceTableAlias, FieldOf(source[i], link.SourceTableAlias)),
                        Key(report, link.TargetTableAlias, FieldOf(target[i], link.TargetTableAlias)));
                if (adjacent.ContainsKey(link.SourceTableAlias) && adjacent.ContainsKey(link.TargetTableAlias))
                {
                    adjacent[link.SourceTableAlias].Add(link.TargetTableAlias);
                    adjacent[link.TargetTableAlias].Add(link.SourceTableAlias);
                }
            }

            var queue = new Queue<string>(new[] { mine[0].Alias });
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mine[0].Alias };
            while (queue.Count > 0)
                foreach (var next in adjacent[queue.Dequeue()])
                    if (seen.Add(next)) queue.Enqueue(next);
            foreach (var alias in seen) connected.Add(report + "|" + alias);
        }

        private static void Fill(TableGen t, int rows, UnionFind keys)
        {
            foreach (var f in t.Fields)
                t.Data.Columns.Add(f.Name, ClrType(f.Type));
            for (int i = 0; i < rows; i++)
            {
                var row = t.Data.NewRow();
                foreach (var f in t.Fields)
                {
                    bool keyed = keys.IsJoined(Key(t.Report, t.Alias, f.Name));
                    row[f.Name] = keyed ? KeyValue(f.Type, i) : Value(f.Name, f.Type, f.Length, i);
                }
                t.Data.Rows.Add(row);
            }
        }

        private static Type ClrType(CrFieldValueTypeEnum type) => type switch
        {
            CrFieldValueTypeEnum.crFieldValueTypeBooleanField => typeof(bool),
            CrFieldValueTypeEnum.crFieldValueTypeCurrencyField => typeof(decimal),
            CrFieldValueTypeEnum.crFieldValueTypeNumberField => typeof(decimal),
            CrFieldValueTypeEnum.crFieldValueTypeInt8sField => typeof(sbyte),
            CrFieldValueTypeEnum.crFieldValueTypeInt8uField => typeof(byte),
            CrFieldValueTypeEnum.crFieldValueTypeInt16sField => typeof(short),
            CrFieldValueTypeEnum.crFieldValueTypeInt16uField => typeof(ushort),
            CrFieldValueTypeEnum.crFieldValueTypeInt32sField => typeof(int),
            CrFieldValueTypeEnum.crFieldValueTypeInt32uField => typeof(uint),
            CrFieldValueTypeEnum.crFieldValueTypeStringField => typeof(string),
            CrFieldValueTypeEnum.crFieldValueTypeDateField => typeof(DateTime),
            CrFieldValueTypeEnum.crFieldValueTypeDateTimeField => typeof(DateTime),
            CrFieldValueTypeEnum.crFieldValueTypeTimeField => typeof(DateTime),
            CrFieldValueTypeEnum.crFieldValueTypeBlobField => typeof(byte[]),
            _ => typeof(string),
        };

        private static DateTime _origin = new DateTime(2024, 1, 1);

        // A joined field's value in row i: the same in every field joined to it, whatever
        // their types, so that a number joins a string holding the same digits.
        private static object KeyValue(CrFieldValueTypeEnum type, int i) => type switch
        {
            CrFieldValueTypeEnum.crFieldValueTypeStringField => (i + 1).ToString(CultureInfo.InvariantCulture),
            CrFieldValueTypeEnum.crFieldValueTypeDateField or CrFieldValueTypeEnum.crFieldValueTypeDateTimeField
                or CrFieldValueTypeEnum.crFieldValueTypeTimeField => _origin.AddDays(i),
            CrFieldValueTypeEnum.crFieldValueTypeBooleanField => i % 2 == 0,
            _ => Convert.ChangeType(i + 1, ClrType(type), CultureInfo.InvariantCulture),
        };

        // Any other field's value in row i, from its name and type. A string column takes
        // four values, so a group on it has several records each; numbers and dates vary.
        private static object Value(string name, CrFieldValueTypeEnum type, int length, int i)
        {
            int h = name.Aggregate(0, (a, c) => (a * 31 + c) & 0xFFFF);
            switch (type)
            {
                case CrFieldValueTypeEnum.crFieldValueTypeStringField:
                    string stem = new string(name.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
                    if (stem.Length > 12) stem = stem.Substring(0, 12).Trim();
                    string s = $"{stem} {(char)('A' + (i + h) % 4)}";
                    return length > 0 && s.Length > length ? s.Substring(s.Length - length) : s;
                case CrFieldValueTypeEnum.crFieldValueTypeNumberField:
                case CrFieldValueTypeEnum.crFieldValueTypeCurrencyField:
                    return Math.Round(25m + (i * 37 + h * 11) % 97 * 12.75m, 2);
                case CrFieldValueTypeEnum.crFieldValueTypeInt8sField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt8uField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt16sField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt16uField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt32sField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt32uField:
                    return Convert.ChangeType(1 + (i * 7 + h) % 20, ClrType(type), CultureInfo.InvariantCulture);
                case CrFieldValueTypeEnum.crFieldValueTypeDateField:
                    return _origin.AddDays((i * 23 + h * 5) % 360);
                case CrFieldValueTypeEnum.crFieldValueTypeDateTimeField:
                    return _origin.AddDays((i * 23 + h * 5) % 360).AddHours(8 + (i * 5 + h) % 10);
                case CrFieldValueTypeEnum.crFieldValueTypeTimeField:
                    return new DateTime(1899, 12, 30, 8, 0, 0).AddMinutes((i * 25 + h) % 480);
                case CrFieldValueTypeEnum.crFieldValueTypeBooleanField:
                    return (i + h) % 2 == 0;
                default:
                    return DBNull.Value;
            }
        }

        private static readonly Regex FieldThenParameter = new Regex(
            @"\{([^{}?]+?)\}\s*(<>|>=|<=|=|>|<|\bin\b)\s*\{\?([^{}]+)\}", RegexOptions.IgnoreCase);
        private static readonly Regex ParameterThenField = new Regex(
            @"\{\?([^{}]+)\}\s*(<>|>=|<=|=|>|<)\s*\{([^{}?]+?)\}", RegexOptions.IgnoreCase);

        // A parameter's value: its first default, or where the record selection compares it
        // with a field, a value of that field that keeps records; with a formula, whose
        // values are not known here, a bound every value passes; failing those, a plain one.
        // A Command's own parameters need values as much as the report's do; a subreport's
        // linked ones take theirs from the parent.
        private static void SetParameter(ReportDocument doc, ParameterField p, string? selection, List<TableGen> tables,
            List<(string Name, object Value)> chosen)
        {
            (string Field, string Op)? compared = null;
            if (!string.IsNullOrEmpty(selection))
            {
                foreach (Match m in FieldThenParameter.Matches(selection!))
                    if (compared is null && string.Equals(m.Groups[3].Value, p.Name, StringComparison.OrdinalIgnoreCase))
                        compared = (m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant());
                foreach (Match m in ParameterThenField.Matches(selection!))
                    if (compared is null && string.Equals(m.Groups[1].Value, p.Name, StringComparison.OrdinalIgnoreCase))
                        compared = (m.Groups[3].Value, Flip(m.Groups[2].Value));
            }

            var values = new ParameterValues();
            bool range = p.DiscreteOrRangeKind == DiscreteOrRangeKind.RangeValue;
            var column = compared is { } c ? ColumnValues(tables, c.Field) : null;
            string op = compared?.Op ?? "=";

            if (column is { Count: > 0 } && (range || op == "in"))
                values.Add(new ParameterRangeValue { StartValue = Coerce(column.Min(), p), EndValue = Coerce(column.Max(), p) });
            else if (column is { Count: > 0 })
                values.Add(new ParameterDiscreteValue
                {
                    Value = Coerce(op is ">" or ">=" ? column.Min() : op is "<" or "<=" ? column.Max() : column[0], p),
                });
            else if (compared is { Field: var f } && f.StartsWith("@") && op is ">" or ">=" or "<" or "<=")
                values.Add(new ParameterDiscreteValue { Value = Extreme(p, upper: op is "<" or "<=") });
            else if (p.DefaultValues.Count > 0)
                values.Add(p.DefaultValues[0]);
            else if (range)
                values.Add(new ParameterRangeValue { StartValue = Plain(p, false), EndValue = Plain(p, true) });
            else
                values.Add(new ParameterDiscreteValue { Value = Plain(p, false) });

            try
            {
                Console.Error.WriteLine($"parameter {(string.IsNullOrEmpty(p.ReportName) ? "" : p.ReportName + ".")}{p.Name} = "
                    + (values[0] is ParameterRangeValue r ? $"{r.StartValue} to {r.EndValue}" : Convert.ToString(((ParameterDiscreteValue)values[0]).Value, CultureInfo.InvariantCulture)));
                if (string.IsNullOrEmpty(p.ReportName) && values[0] is ParameterDiscreteValue d)
                    chosen.Add((p.Name, d.Value));
                if (string.IsNullOrEmpty(p.ReportName)) doc.SetParameterValue(p.Name, values);
                else doc.SetParameterValue(p.Name, values, p.ReportName);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"parameter {p.Name}: {ex.Message.Trim()}");
            }
        }

        private static string Flip(string op) => op switch { ">" => "<", "<" => ">", ">=" => "<=", "<=" => ">=", _ => op };

        // The values a "Table.Field" reference has in the generated rows, in row order with
        // nulls left out; null when no table here has it.
        private static List<IComparable>? ColumnValues(List<TableGen> tables, string reference)
        {
            if (SplitRef(reference) is not { } r) return null;
            var t = tables.FirstOrDefault(x => string.Equals(x.Alias, r.Table, StringComparison.OrdinalIgnoreCase));
            if (t is null || !t.Data.Columns.Contains(r.Field)) return null;
            return t.Data.Rows.Cast<DataRow>().Select(row => row[r.Field]).OfType<IComparable>().ToList();
        }

        private static object Coerce(object value, ParameterField p) => p.ParameterValueType switch
        {
            ParameterValueKind.StringParameter => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            ParameterValueKind.NumberParameter or ParameterValueKind.CurrencyParameter =>
                value is DateTime ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            ParameterValueKind.BooleanParameter => value is bool b ? b : true,
            _ => value is DateTime d ? d : _origin,
        };

        private static object Extreme(ParameterField p, bool upper) => p.ParameterValueType switch
        {
            ParameterValueKind.StringParameter => upper ? "￿" : "",
            ParameterValueKind.NumberParameter or ParameterValueKind.CurrencyParameter => upper ? 1e12m : -1e12m,
            ParameterValueKind.BooleanParameter => true,
            _ => upper ? new DateTime(2999, 12, 31) : new DateTime(1900, 1, 1),
        };

        private static object Plain(ParameterField p, bool upper) => p.ParameterValueType switch
        {
            ParameterValueKind.StringParameter => upper ? "zzzz" : "",
            ParameterValueKind.NumberParameter or ParameterValueKind.CurrencyParameter => upper ? 1000000m : 0m,
            ParameterValueKind.BooleanParameter => true,
            _ => upper ? _origin.AddYears(1) : _origin,
        };

        // "Table.Field" or "{Table.Field}" as its two halves.
        private static (string Table, string Field)? SplitRef(string reference)
        {
            string r = reference.Trim().Trim('{', '}');
            int dot = r.IndexOf('.');
            return dot > 0 && dot < r.Length - 1 ? (r.Substring(0, dot), r.Substring(dot + 1)) : ((string, string)?)null;
        }

        // A link names its fields by themselves or with their table in front.
        private static string FieldOf(string name, string alias) =>
            name.StartsWith(alias + ".", StringComparison.OrdinalIgnoreCase) ? name.Substring(alias.Length + 1) : name;

        private static string Key(string report, string alias, string field) =>
            (report + "|" + alias + "|" + field).ToLowerInvariant();

        // The joined rows, as SavedRows writes them: the tables side by side, row i of each
        // (a table of one row repeated), every column named by itself or "Table.Column" where
        // two tables share the name. A column with no value in any row is left out.
        private static void WriteFixture(List<TableGen> tables, string outputCsvPath)
        {
            int rows = tables.Count == 0 ? 0 : tables.Max(t => t.Data.Rows.Count);
            var columns = tables.SelectMany(t => t.Fields.Select(f => (Table: t, f.Name))).ToList();
            var shared = new HashSet<string>(columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            object Cell((TableGen Table, string Name) c, int i) =>
                c.Table.Data.Rows[Math.Min(i, c.Table.Data.Rows.Count - 1)][c.Name];
            var used = columns.Where(c => Enumerable.Range(0, rows).Any(i => !(Cell(c, i) is DBNull))).ToList();

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", used.Select(c => Quote(shared.Contains(c.Name) ? c.Table.Alias + "." + c.Name : c.Name))));
            for (int i = 0; i < rows; i++)
                sb.AppendLine(string.Join(",", used.Select(c => Quote(Format(Cell(c, i))))));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputCsvPath))!);
            File.WriteAllText(outputCsvPath, sb.ToString());
            Console.WriteLine($"Wrote {outputCsvPath} ({rows} rows, {used.Count} columns, made up)");
        }

        private static string Format(object value) => value switch
        {
            DBNull _ => "",
            DateTime d => d.TimeOfDay == TimeSpan.Zero
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

        private sealed class UnionFind
        {
            private readonly Dictionary<string, string> _parent = new Dictionary<string, string>();

            private string Find(string x)
            {
                if (!_parent.TryGetValue(x, out var p)) return x;
                return p == x ? x : _parent[x] = Find(p);
            }

            public void Union(string a, string b)
            {
                string ra = Find(a), rb = Find(b);
                _parent[ra] = ra;
                _parent[rb] = ra;
            }

            public bool IsJoined(string x) => _parent.ContainsKey(x);
        }
    }
}
