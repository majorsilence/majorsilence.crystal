using Majorsilence.Crystal.Cli.Scanning;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Parser.Chunks;
using Majorsilence.Crystal.Parser.Decryption;
using Majorsilence.Crystal.Parser.OleStorage;

namespace Majorsilence.Crystal.Cli.Commands;

/// <summary>Reverse-engineering aid: dump the flat TSLV tag sequence of one file.</summary>
public static class TagsCommand
{
    public static int Run(string[] args)
    {
        string? file = null;
        int? hexTag = null, stringsTag = null, subdoc = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hex" when i + 1 < args.Length: hexTag = int.Parse(args[++i]); break;
                case "--strings" when i + 1 < args.Length: stringsTag = int.Parse(args[++i]); break;
                case "--subdoc" when i + 1 < args.Length: subdoc = int.Parse(args[++i]); break;
                case "--allstrings": stringsTag = -1; break;
                case "--deepstrings": stringsTag = -2; break;
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

        List<TslvRecord> chunks;
        if (subdoc is not null)
        {
            // Dump the inner TSLV stream of an embedded subreport instead of the parent's
            using var ole = OleReader.Open(file);
            byte[] contents = ole.ReadStreamAt($"Subdocument {subdoc}/Contents");
            chunks = TslvReader.ReadAll(ContentDecryptor.Decrypt(contents));
        }
        else
        {
            var result = RptParser.Parse(file);
            if (result.RawChunks.Count == 0)
            {
                Console.Error.WriteLine($"error: no TSLV records ({string.Join("; ", result.Errors)})");
                return 1;
            }
            chunks = result.RawChunks;
        }

        foreach (var rec in chunks)
        {
            if (hexTag is not null || stringsTag is not null)
            {
                if (rec.Tag == hexTag) DumpHex(rec);
                if (rec.Tag == stringsTag || stringsTag == -1) DumpStrings(rec, skipEmpty: stringsTag == -1);
                if (stringsTag == -2) DumpStringsDeep(rec, $"{rec.Tag}", 0);
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

    // Recurse into nested TSLV children (each level XOR-decodes with its own tag)
    // and report every MUTF-8 string found, prefixed by the tag path.
    private static void DumpStringsDeep(TslvRecord rec, string path, int depth)
    {
        foreach (var (start, _, text) in Mutf8Scanner.Scan(rec.Data))
            Console.WriteLine($"  {rec.StreamOffset,8}  [{path}] +{start}: '{text}'");
        if (depth >= 4) return;
        List<TslvRecord> children;
        try { children = rec.ParseChildren(); } catch { return; }
        foreach (var child in children.Where(c => c.Data.Length >= 6))
            DumpStringsDeep(child, $"{path}>{child.Tag}", depth + 1);
    }

    private static void DumpStrings(TslvRecord rec, bool skipEmpty = false)
    {
        var found = Mutf8Scanner.Scan(rec.Data);
        if (skipEmpty && found.Count == 0) return;
        Console.WriteLine($"-- tag {rec.Tag} at {rec.StreamOffset} (len {rec.Data.Length}) --");
        foreach (var (start, _, text) in found)
            Console.WriteLine($"  {start,6}: '{text}'");
    }
}
