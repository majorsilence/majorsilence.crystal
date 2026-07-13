using Majorsilence.Crystal.Cli.Scanning;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Parser.Chunks;

namespace Majorsilence.Crystal.Cli.Commands;

/// <summary>Reverse-engineering aid: dump the flat TSLV tag sequence of one file.</summary>
public static class TagsCommand
{
    public static int Run(string[] args)
    {
        string? file = null;
        int? hexTag = null, stringsTag = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hex" when i + 1 < args.Length: hexTag = int.Parse(args[++i]); break;
                case "--strings" when i + 1 < args.Length: stringsTag = int.Parse(args[++i]); break;
                default:
                    if (file is null && !args[i].StartsWith('-')) { file = args[i]; break; }
                    Console.Error.WriteLine($"error: unexpected argument '{args[i]}'");
                    return 2;
            }
        }

        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine("error: tags requires an existing .rpt file");
            return 2;
        }

        var result = RptParser.Parse(file);
        if (result.RawChunks.Count == 0)
        {
            Console.Error.WriteLine($"error: no TSLV records ({string.Join("; ", result.Errors)})");
            return 1;
        }

        foreach (var rec in result.RawChunks)
        {
            if (hexTag is not null || stringsTag is not null)
            {
                if (rec.Tag == hexTag) DumpHex(rec);
                if (rec.Tag == stringsTag) DumpStrings(rec);
                continue;
            }

            string marker = TslvRecord.IsSectionStart(rec.Tag) ? $"  << {TslvRecord.SectionKindFromTag(rec.Tag)} start"
                : TslvRecord.IsSectionEnd(rec.Tag) ? $"  << {TslvRecord.SectionKindFromTag(rec.Tag)} end"
                : string.Empty;
            Console.WriteLine($"{rec.StreamOffset,8}  tag {rec.Tag,4}  schema {rec.Schema,3}  len {rec.Data.Length,6}{marker}");
        }
        return 0;
    }

    private static void DumpHex(TslvRecord rec)
    {
        Console.WriteLine($"-- tag {rec.Tag} at {rec.StreamOffset} (len {rec.Data.Length}) --");
        for (int i = 0; i < rec.Data.Length; i += 16)
        {
            var span = rec.Data.AsSpan(i, Math.Min(16, rec.Data.Length - i));
            string hex = string.Join(" ", span.ToArray().Select(b => b.ToString("X2")));
            string ascii = new(span.ToArray().Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
            Console.WriteLine($"  {i,6}: {hex,-47}  {ascii}");
        }
    }

    private static void DumpStrings(TslvRecord rec)
    {
        Console.WriteLine($"-- tag {rec.Tag} at {rec.StreamOffset} (len {rec.Data.Length}) --");
        foreach (var (start, _, text) in Mutf8Scanner.Scan(rec.Data))
            Console.WriteLine($"  {start,6}: '{text}'");
    }
}
