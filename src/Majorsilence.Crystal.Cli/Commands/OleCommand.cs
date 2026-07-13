using System.Text;
using Majorsilence.Crystal.Parser.OleStorage;

namespace Majorsilence.Crystal.Cli.Commands;

/// <summary>Reverse-engineering aid: dump the OLE compound-document tree of one file.</summary>
public static class OleCommand
{
    public static int Run(string[] args)
    {
        string? file = null, extract = null, outPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--extract" when i + 1 < args.Length: extract = args[++i]; break;
                case "-o" when i + 1 < args.Length: outPath = args[++i]; break;
                default:
                    if (file is null && !args[i].StartsWith('-')) { file = args[i]; break; }
                    Console.Error.WriteLine($"error: unexpected argument '{args[i]}'");
                    return 2;
            }
        }

        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine("error: ole requires an existing .rpt file");
            return 2;
        }

        using var ole = OleReader.Open(file);

        if (extract is not null)
        {
            byte[] data = ole.ReadStreamAt(extract);
            string target = outPath ?? Printable(extract).Replace('/', '_');
            File.WriteAllBytes(target, data);
            Console.WriteLine($"wrote {data.Length} bytes to {target}");
            return 0;
        }

        foreach (var entry in ole.EnumerateEntries(recursive: true))
        {
            int depth = entry.Path.Count(c => c == '/');
            string indent = new(' ', depth * 2);
            string name = Printable(entry.Path[(entry.Path.LastIndexOf('/') + 1)..]);
            Console.WriteLine(entry.IsStorage
                ? $"{indent}[{name}]"
                : $"{indent}{name}  ({entry.Length} bytes)");
        }
        return 0;
    }

    private static string Printable(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(c < 0x20 ? $"\\x{(int)c:X2}" : c);
        return sb.ToString();
    }
}
