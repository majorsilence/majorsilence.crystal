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
        // Crystal formats a report against a printer, and a report that does not store its own
        // margins takes that printer's. Left alone that is the session's default printer, which
        // is not stable: a Remote Desktop session substitutes the client's redirected printer,
        // and a render made there sits 6pt further in on every edge than one made at the
        // console - enough to wreck an ink comparison, and invisible, because every reference
        // looks equally plausible on its own.
        //
        // So the printer is pinned. Every committed reference under tests/reference-renders
        // was rendered against this one: re-rendering ProductPriceList against it reproduces
        // its reference pixel for pixel apart from the print date in the footer, where the
        // redirected inkjet and Microsoft Print to PDF both shift the whole page. --printer
        // overrides it; a printer Crystal does not accept is an error, never a fallback.
        //
        // The one exception is a report Crystal will not bind to any printer: it reads the
        // name back as empty and formats from the report's own page setup. That is
        // printer-independent rather than a fallback - boyum__SalesOpportunity renders
        // pixel-identical against the laser and against the redirected inkjet.
        private const string ReferencePrinter = "Brother DCP-L2550DW series Printer";

        private static string _printer = ReferencePrinter;

        private static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--printer")
            {
                _printer = args[1];
                args = args.Skip(2).ToArray();
            }

            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ReferenceRenderer [--printer <name>] <rpt-path> <output-png-path> [page-index]");
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

            var pdfBytes = Render(rptPath, datafile);

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
            var bytes = Render(rptPath, datafile);

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
            var pdfBytes = Render(rptPath, datafile);

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

            var csvBytes = Render(rptPath, datafile);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputCsvPath))!);
            File.WriteAllBytes(outputCsvPath, csvBytes);
            Console.WriteLine($"Wrote {outputCsvPath} ({csvBytes.Length} bytes)");
            return 0;
        }

        /// <summary>
        /// Loads the report the way CrystalCmd's Exporter does, formats it against the pinned
        /// printer, and exports it.
        /// </summary>
        private static byte[] Render(string rptPath, Data datafile)
        {
            using var report = new CrystalDocumentWrapper(NullLogger.Instance).Create(rptPath, datafile);
            report.PrintOptions.PrinterName = _printer;
            string accepted = report.PrintOptions.PrinterName;
            if (accepted.Length == 0)
                Console.Error.WriteLine("report is not bound to a printer; formatted from its own page setup");
            else
                Console.Error.WriteLine($"formatted against printer: {accepted}");
            if (accepted.Length > 0 && accepted != _printer)
                throw new InvalidOperationException(
                    $"Crystal did not accept printer \"{_printer}\"; it reports \"{report.PrintOptions.PrinterName}\".");

            using var exported = report.ExportToStream(datafile.ExportAs switch
            {
                ExportTypes.PDF => CrystalDecisions.Shared.ExportFormatType.PortableDocFormat,
                ExportTypes.ExcelDataOnly => CrystalDecisions.Shared.ExportFormatType.ExcelRecord,
                ExportTypes.CSV => CrystalDecisions.Shared.ExportFormatType.CharacterSeparatedValues,
                _ => throw new ArgumentOutOfRangeException(nameof(datafile), datafile.ExportAs, "not used by this tool"),
            });
            using var buffer = new MemoryStream();
            exported.CopyTo(buffer);
            report.Close();
            return buffer.ToArray();
        }
    }
}
