using System;
using System.IO;
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
                return 1;
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
    }
}
