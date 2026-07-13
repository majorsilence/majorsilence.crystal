using System.Collections.Concurrent;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Parser;

namespace Majorsilence.Crystal.Cli.Commands;

public static class ConvertCommand
{
    public static int Run(string[] args)
    {
        string? input = null, outDir = null;
        bool recursive = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length: outDir = args[++i]; break;
                case "-r": recursive = true; break;
                default:
                    if (input is null && !args[i].StartsWith('-')) { input = args[i]; break; }
                    Console.Error.WriteLine($"error: unexpected argument '{args[i]}'");
                    return 2;
            }
        }

        if (input is null)
        {
            Console.Error.WriteLine("error: convert requires a .rpt file or directory");
            return 2;
        }

        string root;
        List<string> files;
        if (File.Exists(input))
        {
            root = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
            files = [Path.GetFullPath(input)];
        }
        else if (Directory.Exists(input))
        {
            root = Path.GetFullPath(input);
            files = Directory.EnumerateFiles(root, "*.rpt",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            Console.Error.WriteLine($"error: '{input}' not found");
            return 2;
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("error: no .rpt files found");
            return 2;
        }

        int converted = 0, failed = 0, warned = 0;
        var consoleLock = new object();
        var failures = new ConcurrentBag<string>();

        Parallel.ForEach(files, file =>
        {
            string rel = Path.GetRelativePath(root, file);
            var lines = new List<string>();
            bool ok = false, hasWarnings = false;

            try
            {
                var result = RptParser.Parse(file);
                if (!result.Success || result.Report is null)
                {
                    foreach (var err in result.Errors)
                        lines.Add($"  error: {err.Replace(file, rel)}");
                }
                else
                {
                    foreach (var warn in result.Warnings)
                        lines.Add($"  warn: {warn.Replace(file, rel)}");
                    hasWarnings = result.Warnings.Count > 0;

                    string target = outDir is null
                        ? Path.ChangeExtension(file, ".rdl")
                        : Path.Combine(outDir, Path.ChangeExtension(rel, ".rdl"));
                    string stem = Path.GetFileNameWithoutExtension(target);
                    string rdl = new RdlConverter().Convert(result.Report, $"{stem}_");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, rdl);
                    WriteSubreportCompanions(result.Report, target);
                    ok = true;
                }
            }
            catch (Exception ex)
            {
                lines.Add($"  error: {ex.GetType().Name}: {ex.Message.Replace(file, rel)}");
                if (ex.StackTrace is { } st)
                    lines.AddRange(st.Split('\n').Take(4).Select(l => $"    {l.TrimEnd()}"));
            }

            lock (consoleLock)
            {
                if (ok) { converted++; if (hasWarnings) warned++; }
                else { failed++; failures.Add(rel); }

                if (!ok || hasWarnings)
                {
                    Console.WriteLine($"{(ok ? "warn" : "FAIL")}: {rel}");
                    lines.ForEach(Console.WriteLine);
                }
            }
        });

        Console.WriteLine();
        Console.WriteLine($"converted {converted}/{files.Count} ({warned} with warnings, {failed} failed)");
        return failed == 0 ? 0 : 1;
    }

    // Each parsed subreport becomes a companion .rdl next to its parent, named
    // "<parentStem>_<SubreportName>.rdl" — matching the <ReportName> the parent emits.
    private static void WriteSubreportCompanions(Majorsilence.Crystal.Model.ReportDefinition report, string mainRdlPath)
    {
        string dir = Path.GetDirectoryName(mainRdlPath)!;
        string stem = Path.GetFileNameWithoutExtension(mainRdlPath);
        foreach (var sub in report.Sections
                     .SelectMany(s => s.Objects)
                     .OfType<Majorsilence.Crystal.Model.Objects.SubreportObject>()
                     .Where(s => s.Report is not null))
        {
            string name = RdlConverter.SubreportRdlName($"{stem}_", sub.SubreportName);
            string path = Path.Combine(dir, name + ".rdl");
            File.WriteAllText(path, new RdlConverter().Convert(sub.Report!, $"{name}_"));
            WriteSubreportCompanions(sub.Report!, path);
        }
    }
}
