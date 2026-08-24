using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Majorsilence.CrystalCmd.Common;
using Majorsilence.CrystalCmd.Server.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Majorsilence.Crystal.ReferenceRenderer
{
    internal class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ReferenceRenderer <rpt-path> <output-png-path> [page-index]");
                Console.Error.WriteLine("       ReferenceRenderer --data <rpt-path> <output-csv-path>");
                return 1;
            }

            // --data exports the report's own saved rows instead of rendering them. The
            // reference PNGs are rendered from that saved data, so our render has to be fed
            // the same rows to be comparable at all — without it every data-bound item comes
            // out empty and a visual comparison is meaningless. See BACKLOG.
            if (args[0] == "--data" || args[0] == "--data-raw")
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: ReferenceRenderer --data|--data-raw <rpt-path> <output-csv-path>");
                    return 1;
                }
                // --data-raw writes Crystal's export untouched, for working out the column
                // layout of a report shape the detail-column finder does not yet handle.
                return ExportData(args[1], args[2], raw: args[0] == "--data-raw");
            }

            // --pdf writes the real engine's PDF untouched. Rasterizing loses the text
            // positions, and those are the only ground truth available for where Crystal
            // actually places an object: pdftotext -bbox recovers them from this file.
            // --xls dumps Crystal's ExcelDataOnly export untouched. Unlike CSV, which
            // flattens rendered sections, this one is meant to be the data behind the
            // report - which is what a data fixture needs.
            if (args[0] == "--xls")
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: ReferenceRenderer --xls <rpt-path> <output-xls-path>");
                    return 1;
                }
                return ExportXls(args[1], args[2]);
            }

            if (args[0] == "--pdf")
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: ReferenceRenderer --pdf <rpt-path> <output-pdf-path>");
                    return 1;
                }
                return ExportPdf(args[1], args[2]);
            }

            string rptPath = args[0];
            string outputPngPath = args[1];
            int pageIndex = args.Length >= 3 ? int.Parse(args[2]) : 0;

            if (!File.Exists(rptPath))
            {
                Console.Error.WriteLine($"Not found: {rptPath}");
                return 1;
            }

            // A bare Data() has no data sources/parameters at all, so the report
            // renders with whatever sample data is already saved in the .rpt file
            // (the same thing you'd see opening it in the Designer without refreshing).
            var datafile = new Data { ExportAs = ExportTypes.PDF };

            var exporter = new Exporter(NullLogger.Instance);
            var (pdfBytes, _, _) = exporter.exportReportToStream(rptPath, datafile);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);

            using var pdfStream = new MemoryStream(pdfBytes);
            PDFtoImage.Conversion.SavePng(outputPngPath, pdfStream, page: pageIndex);

            Console.WriteLine($"Wrote {outputPngPath} ({pdfBytes.Length} PDF bytes rasterized)");
            return 0;
        }

        private static int ExportXls(string rptPath, string outputXlsPath)
        {
            if (!File.Exists(rptPath))
            {
                Console.Error.WriteLine($"Not found: {rptPath}");
                return 1;
            }

            var datafile = new Data { ExportAs = ExportTypes.ExcelDataOnly };
            var exporter = new Exporter(NullLogger.Instance);
            var (bytes, _, _) = exporter.exportReportToStream(rptPath, datafile);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputXlsPath))!);
            File.WriteAllBytes(outputXlsPath, bytes);
            Console.WriteLine($"Wrote {outputXlsPath} ({bytes.Length} bytes)");
            return 0;
        }

        private static int ExportPdf(string rptPath, string outputPdfPath)
        {
            if (!File.Exists(rptPath))
            {
                Console.Error.WriteLine($"Not found: {rptPath}");
                return 1;
            }

            var datafile = new Data { ExportAs = ExportTypes.PDF };
            var exporter = new Exporter(NullLogger.Instance);
            var (pdfBytes, _, _) = exporter.exportReportToStream(rptPath, datafile);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPdfPath))!);
            File.WriteAllBytes(outputPdfPath, pdfBytes);
            Console.WriteLine($"Wrote {outputPdfPath} ({pdfBytes.Length} bytes)");
            return 0;
        }

        private static int ExportData(string rptPath, string outputCsvPath, bool raw = false)
        {
            if (!File.Exists(rptPath))
            {
                Console.Error.WriteLine($"Not found: {rptPath}");
                return 1;
            }

            // Same bare Data() as the PNG path above, so the rows exported here are exactly
            // the ones the reference image was rendered from.
            //
            // CSV rather than ExcelDataOnly: that one writes legacy BIFF8 (an OLE compound
            // file), which would need a spreadsheet parser to read back. CSV is plain text,
            // but note what Crystal puts in it — every row carries the *whole* report line,
            // section by section: report-header text, then the page-header column labels,
            // then that row's detail values, then the footers. The labels sit immediately
            // before the values and there are as many of one as the other, which is what
            // makes the detail columns recoverable; see BACKLOG for the consumer side.
            var datafile = new Data { ExportAs = ExportTypes.CSV };

            var exporter = new Exporter(NullLogger.Instance);
            var (csvBytes, _, _) = exporter.exportReportToStream(rptPath, datafile);

            if (raw)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputCsvPath))!);
                File.WriteAllBytes(outputCsvPath, csvBytes);
                Console.WriteLine($"Wrote raw {outputCsvPath} ({csvBytes.Length} bytes)");
                return 0;
            }

            var rows = ParseCsv(System.Text.Encoding.UTF8.GetString(csvBytes));
            if (rows.Count == 0)
            {
                Console.Error.WriteLine("Export produced no rows — does this report have saved data?");
                return 1;
            }

            if (!TryFindDetailColumns(rows, out int labelStart, out int valueStart, out int width))
            {
                Console.Error.WriteLine(
                    $"Could not identify the detail columns in {rows[0].Count} exported columns. " +
                    "Expected a run of identical columns (the page-header labels) immediately " +
                    "followed by an equally long run of varying ones (the row values).");
                return 1;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Join(",",
                Enumerable.Range(0, width).Select(i => Quote(rows[0][labelStart + i]))));
            foreach (var row in rows)
                sb.AppendLine(string.Join(",",
                    Enumerable.Range(0, width).Select(i => Quote(row[valueStart + i]))));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputCsvPath))!);
            File.WriteAllText(outputCsvPath, sb.ToString());

            Console.WriteLine($"Wrote {outputCsvPath}: {rows.Count} rows x {width} columns " +
                              $"({string.Join(", ", Enumerable.Range(0, width).Select(i => rows[0][labelStart + i]))})");
            return 0;
        }

        /// <summary>
        /// Locates the detail columns in Crystal's section-interleaved CSV: the page-header
        /// labels are identical on every row and sit immediately before that row's values,
        /// one label per value. Returns the label run's start, the value run's start, and
        /// their shared width.
        ///
        /// Only plain list reports satisfy that, and it is a property of the export rather
        /// than a limitation here: the CSV flattens the *rendered sections*, so a grouped
        /// report repeats its group name between the labels and the values, a report with
        /// no column headings has no labels to key on, and a cross-tab's data does not
        /// appear at all. Returning false for those is correct — a fixture built on a
        /// mis-guessed alignment makes the visual suite assert against fiction. Use
        /// --data-raw to see a shape's actual column layout.
        /// </summary>
        private static bool TryFindDetailColumns(List<List<string>> rows, out int labelStart,
            out int valueStart, out int width)
        {
            labelStart = valueStart = width = 0;
            int columns = rows[0].Count;
            var isConstant = new bool[columns];
            for (int c = 0; c < columns; c++)
                isConstant[c] = rows.All(r => c < r.Count && r[c] == rows[0][c]);

            // Widest label/value pairing wins, so a constant title column ahead of the
            // labels or a constant footer behind the values cannot shift the alignment.
            for (int w = columns / 2; w >= 1; w--)
            {
                for (int start = 0; start + 2 * w <= columns; start++)
                {
                    bool ok = true;
                    for (int i = 0; i < w && ok; i++)
                    {
                        // Labels must be constant AND actually be labels: a run of blank
                        // constant columns otherwise matches and yields nameless fixture
                        // columns, which bind to nothing downstream and look like a
                        // conversion failure rather than a bad fixture.
                        if (!isConstant[start + i] || rows[0][start + i].Trim().Length == 0) ok = false;
                        if (ok && isConstant[start + w + i]) ok = false;
                    }
                    if (!ok) continue;
                    labelStart = start;
                    valueStart = start + w;
                    width = w;
                    return true;
                }
            }
            return false;
        }

        private static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (inQuotes)
                {
                    if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (ch == '"') inQuotes = false;
                    else field.Append(ch);
                }
                else if (ch == '"') inQuotes = true;
                else if (ch == ',') { row.Add(field.ToString()); field.Clear(); }
                else if (ch == '\n')
                {
                    row.Add(field.ToString()); field.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                }
                else if (ch != '\r') field.Append(ch);
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
            }

            // Ragged trailing rows (Crystal sometimes emits a short final line) would break
            // the column analysis, so keep only the full-width ones.
            int widthMode = rows.GroupBy(r => r.Count).OrderByDescending(g => g.Count()).First().Key;
            return rows.Where(r => r.Count == widthMode).ToList();
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
