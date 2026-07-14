using System.Collections.Concurrent;
using System.Text.Json;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.Cli.Commands;

/// <summary>
/// Loads every .rdl under a directory with the Majorsilence.Reporting engine
/// (the conversion target) and reports its parse/expression errors — the checks
/// XML well-formedness cannot catch. Data-connection problems are expected
/// (converted reports have empty connect strings) and are counted separately.
/// </summary>
public static class VerifyCommand
{
    // fyiReporting/Majorsilence severity convention: 8 fatal, 4 error, below = warning/info
    private const int ErrorSeverity = 4;

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
            Console.Error.WriteLine("error: verify requires an existing directory of .rdl files");
            return 2;
        }

        string root = Path.GetFullPath(dir);
        var files = Directory.EnumerateFiles(root, "*.rdl", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("error: no .rdl files found");
            return 2;
        }

        int clean = 0, withErrors = 0, crashed = 0, done = 0;
        var buckets = new ConcurrentDictionary<string, ConcurrentBag<string>>();

        Console.WriteLine($"verifying {files.Count} files under {root} with Majorsilence.Reporting ...");

        Parallel.ForEach(files, file =>
        {
            string rel = Path.GetRelativePath(root, file);
            try
            {
                // Folder lets the engine resolve <Subreport><ReportName> companions
                var parser = new RDLParser(File.ReadAllText(file))
                {
                    Folder = Path.GetDirectoryName(file)!
                };
                var report = parser.Parse().GetAwaiter().GetResult();

                var errors = (report.ErrorItems?.Cast<string>() ?? [])
                    .Where(IsSchemaError)
                    .ToList();
                report.ErrorReset();

                if (errors.Count == 0)
                {
                    Interlocked.Increment(ref clean);
                }
                else
                {
                    Interlocked.Increment(ref withErrors);
                    foreach (var err in errors)
                        buckets.GetOrAdd(Bucket(err, file, rel), _ => []).Add(rel);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref crashed);
                string frame = ex.StackTrace?.Split('\n')
                    .FirstOrDefault(l => l.Contains("Majorsilence.Reporting"))?.Trim() ?? "";
                buckets.GetOrAdd($"{ex.GetType().Name}: {Bucket(ex.Message, file, rel)} @ {frame}", _ => []).Add(rel);
            }

            int n = Interlocked.Increment(ref done);
            if (n % 500 == 0) Console.WriteLine($"  ... {n}/{files.Count}");
        });

        Console.WriteLine();
        Console.WriteLine($"verified {files.Count}: {clean} clean, {withErrors} with engine errors, {crashed} crashed");
        if (!buckets.IsEmpty)
        {
            Console.WriteLine();
            Console.WriteLine("-- engine error buckets --");
            foreach (var (key, hits) in buckets.OrderByDescending(kv => kv.Value.Count))
            {
                Console.WriteLine($"  {hits.Count,5}  {key}");
                foreach (var f in hits.Distinct().Take(3))
                    Console.WriteLine($"         e.g. {f}");
            }
        }

        if (jsonOut is not null)
        {
            var dto = buckets.ToDictionary(kv => kv.Key,
                kv => kv.Value.Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList());
            File.WriteAllText(jsonOut, JsonSerializer.Serialize(
                new { Total = files.Count, Clean = clean, WithErrors = withErrors, Crashed = crashed, Buckets = dto },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"json report written to {jsonOut}");
        }

        return withErrors == 0 && crashed == 0 ? 0 : 1;
    }

    // The engine prefixes messages with a severity word; anything below Error is
    // advisory. Data-source connection issues only surface at RunGetData, which
    // verify does not attempt (converted reports have empty connect strings).
    private static bool IsSchemaError(string message) =>
        message.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
        message.StartsWith("Fatal", StringComparison.OrdinalIgnoreCase);

    // Bucket key: strip paths and volatile identifiers so identical defects group together
    private static string Bucket(string message, string absolute, string relative)
    {
        string m = message.Replace(absolute, "<file>").Replace(relative, "<file>")
            .Replace(Path.GetFileNameWithoutExtension(absolute), "<name>");
        m = System.Text.RegularExpressions.Regex.Replace(m, @"'[^']{1,60}'", "'<id>'");
        m = System.Text.RegularExpressions.Regex.Replace(m, @"\d+", "N");
        return m.Length > 220 ? m[..220] : m;
    }
}
