using System.Collections.Concurrent;
using System.Text.Json;
using Majorsilence.Crystal.Cli.Scanning;
using Majorsilence.Crystal.Cli.Scanning.Detectors;
using Majorsilence.Crystal.Parser;

namespace Majorsilence.Crystal.Cli.Commands;

public static class ScanCommand
{
    private const int ConsoleHitCap = 15;

    public static int Run(string[] args)
    {
        string? dir = null, jsonOut = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json" when i + 1 < args.Length: jsonOut = args[++i]; break;
                default:
                    if (dir is null && !args[i].StartsWith('-')) { dir = args[i]; break; }
                    Console.Error.WriteLine($"error: unexpected argument '{args[i]}'");
                    return 2;
            }
        }

        if (dir is null || !Directory.Exists(dir))
        {
            Console.Error.WriteLine("error: scan requires an existing directory");
            return 2;
        }

        string root = Path.GetFullPath(dir);
        var files = Directory.EnumerateFiles(root, "*.rpt", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("error: no .rpt files found");
            return 2;
        }

        IFeatureDetector[] detectors =
        [
            new NonSumAggregateDetector(),
            new SuppressFormulaCandidateDetector(),
            new Tag254UnknownBitsDetector(),
            new SpecialObjectTagsDetector(),
            new UnknownSectionObjectTagsDetector(),
            new ExtraOleEntriesDetector(),
            new ObjectFormatHookDetector(),
            new ChartObjectDetector(),
        ];

        int parsed = 0, failedCount = 0, done = 0;
        var buckets = new ConcurrentDictionary<string, ConcurrentBag<string>>();
        var tagFiles = new ConcurrentDictionary<int, int>();
        var tagTotals = new ConcurrentDictionary<int, int>();
        var hits = new ConcurrentBag<DetectorHit>();
        var detectorErrors = new ConcurrentDictionary<string, int>();

        Console.WriteLine($"scanning {files.Count} files under {root} ...");

        Parallel.ForEach(files, file =>
        {
            string rel = Path.GetRelativePath(root, file);
            var result = RptParser.Parse(file);

            if (!result.Success)
            {
                Interlocked.Increment(ref failedCount);
                string key = result.Errors.Count > 0
                    ? StripPath(result.Errors[0], file, rel)
                    : "(no error message)";
                buckets.GetOrAdd(key, _ => []).Add(rel);
            }
            else
            {
                Interlocked.Increment(ref parsed);

                foreach (var group in result.RawChunks.GroupBy(r => r.Tag))
                {
                    tagFiles.AddOrUpdate(group.Key, 1, (_, v) => v + 1);
                    tagTotals.AddOrUpdate(group.Key, group.Count(), (_, v) => v + group.Count());
                }

                var ctx = new ScanContext { FilePath = file, RelativePath = rel, Result = result };
                foreach (var detector in detectors)
                {
                    try
                    {
                        foreach (var detail in detector.Inspect(ctx))
                            hits.Add(new DetectorHit(detector.Id, rel, detail, result.RawChunks.Count));
                    }
                    catch (Exception ex)
                    {
                        detectorErrors.AddOrUpdate($"{detector.Id}: {ex.GetType().Name}: {ex.Message}", 1, (_, v) => v + 1);
                    }
                }
            }

            int n = Interlocked.Increment(ref done);
            if (n % 250 == 0) Console.WriteLine($"  ... {n}/{files.Count}");
        });

        var report = BuildReport(files.Count, parsed, failedCount, buckets, tagFiles, tagTotals, hits, detectors);
        PrintReport(report, detectorErrors);

        if (jsonOut is not null)
        {
            File.WriteAllText(jsonOut, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"json report written to {jsonOut}");
        }

        return 0;
    }

    // Bucket keys must not contain corpus paths: strip both absolute and relative forms.
    private static string StripPath(string message, string absolute, string relative) =>
        message.Replace(absolute, "<file>").Replace(relative, "<file>")
               .Replace(Path.GetFileName(absolute), "<file>");

    private static ScanReport BuildReport(
        int total, int parsed, int failed,
        ConcurrentDictionary<string, ConcurrentBag<string>> buckets,
        ConcurrentDictionary<int, int> tagFiles,
        ConcurrentDictionary<int, int> tagTotals,
        ConcurrentBag<DetectorHit> hits,
        IFeatureDetector[] detectors) => new()
    {
        TotalFiles = total,
        Parsed = parsed,
        Failed = failed,
        ExceptionBuckets = buckets.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()),
        TagHistogram = tagFiles.Keys
            .OrderBy(t => t)
            .ToDictionary(t => t, t => new ScanReport.TagStat(tagFiles[t], tagTotals.GetValueOrDefault(t))),
        Detectors = detectors.Select(d => new ScanReport.DetectorSection
        {
            Id = d.Id,
            BacklogItem = d.BacklogItem,
            // smallest files first — the best reverse-engineering subjects
            Hits = hits.Where(h => h.DetectorId == d.Id)
                       .OrderBy(h => h.ChunkCount).ThenBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase)
                       .ToList(),
            Summary = d.Summarize().ToList(),
        }).ToList(),
    };

    private static void PrintReport(ScanReport report, ConcurrentDictionary<string, int> detectorErrors)
    {
        Console.WriteLine();
        Console.WriteLine($"scanned {report.TotalFiles}: {report.Parsed} parsed, {report.Failed} failed " +
                          $"({100.0 * report.Parsed / report.TotalFiles:F1}% ok)");

        if (report.ExceptionBuckets.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("-- parse failure buckets --");
            foreach (var (key, filesInBucket) in report.ExceptionBuckets.OrderByDescending(kv => kv.Value.Count))
            {
                Console.WriteLine($"  {filesInBucket.Count,5}  {key}");
                foreach (var f in filesInBucket.Take(3))
                    Console.WriteLine($"         e.g. {f}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("-- detector results (files unlocking backlog items) --");
        foreach (var section in report.Detectors)
        {
            var fileCount = section.Hits.Select(h => h.RelativePath).Distinct().Count();
            Console.WriteLine();
            Console.WriteLine($"[{section.BacklogItem}] {section.Id}: {section.Hits.Count} hits in {fileCount} files");
            foreach (var hit in section.Hits.Take(ConsoleHitCap))
                Console.WriteLine($"    {hit.RelativePath} — {hit.Detail}");
            if (section.Hits.Count > ConsoleHitCap)
                Console.WriteLine($"    (+{section.Hits.Count - ConsoleHitCap} more — see --json output)");
            foreach (var line in section.Summary)
                Console.WriteLine($"    # {line}");
        }

        if (!detectorErrors.IsEmpty)
        {
            Console.WriteLine();
            Console.WriteLine("-- detector errors --");
            foreach (var (msg, count) in detectorErrors.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {count,5}  {msg}");
        }
    }
}
