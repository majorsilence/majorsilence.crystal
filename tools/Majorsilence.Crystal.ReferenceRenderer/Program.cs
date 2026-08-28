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
                Console.Error.WriteLine("       ReferenceRenderer --xls  <rpt-path> <output-xls-path>");
                Console.Error.WriteLine("       ReferenceRenderer --pdf  <rpt-path> <output-pdf-path>");
                Console.Error.WriteLine("       ReferenceRenderer --csv  <rpt-path> <output-csv-path>");
                return 1;
            }

            // --csv dumps Crystal's CSV export untouched, as a diagnostic. It is NOT the way
            // to build a data fixture: this export flattens the *rendered* sections, so every
            // line carries the whole report line — header text, column labels, that row's
            // values, footers — and which columns are the detail values is a guess. A version
            // of this tool made that guess and shipped it as a fixture; on a grouped report
            // the repeated group name sits between the labels and the values, so the guess
            // landed one column off and produced a fixture with customer names filed under
            // "Order Amount". Fixtures come from --xls plus FixtureBuilder, which names the
            // columns from our own parsed field list instead of guessing. See BACKLOG.
            if (args[0] == "--csv")
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: ReferenceRenderer --csv <rpt-path> <output-csv-path>");
                    return 1;
                }
                return ExportCsv(args[1], args[2]);
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

        private static int ExportCsv(string rptPath, string outputCsvPath)
        {
            if (!File.Exists(rptPath))
            {
                Console.Error.WriteLine($"Not found: {rptPath}");
                return 1;
            }

            // Same bare Data() as the PNG path above, so the rows dumped here are exactly the
            // ones the reference image was rendered from.
            var datafile = new Data { ExportAs = ExportTypes.CSV };

            var exporter = new Exporter(NullLogger.Instance);
            var (csvBytes, _, _) = exporter.exportReportToStream(rptPath, datafile);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputCsvPath))!);
            File.WriteAllBytes(outputCsvPath, csvBytes);
            Console.WriteLine($"Wrote {outputCsvPath} ({csvBytes.Length} bytes)");
            return 0;
        }
    }
}
