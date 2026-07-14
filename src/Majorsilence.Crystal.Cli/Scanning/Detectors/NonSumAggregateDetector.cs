using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// Finds summary FieldObjects and their aggregate functions. A summary object's
/// tag-159 wrapper embeds a plain field-reference string of the form
/// "&lt;Function&gt; of Table.Column" (e.g. "Sum of Orders.Order Amount"),
/// followed by a 12-byte metadata block that is all zeros for plain fields.
/// Hits report every non-Sum summary plus the metadata bytes so the binary
/// function code can be located by diffing.
/// </summary>
public sealed partial class NonSumAggregateDetector : IFeatureDetector
{
    public string Id => "non-sum-aggregate";
    public string BacklogItem => "Non-Sum group footer aggregates";

    [GeneratedRegex(@"^([A-Za-z][A-Za-z ]{1,30}?) of (\S.*\..+)$")]
    private static partial Regex SummaryRef();

    private readonly ConcurrentDictionary<string, int> _prefixHistogram = new();

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        foreach (var rec in ctx.Chunks.Where(r => r.Tag == 159))
        {
            foreach (var (start, end, text) in Mutf8Scanner.Scan(rec.Data, minChars: 6))
            {
                var m = SummaryRef().Match(text);
                if (!m.Success) continue;

                string prefix = m.Groups[1].Value;
                _prefixHistogram.AddOrUpdate(prefix, 1, (_, v) => v + 1);

                if (prefix == "Sum") continue;
                var tail = rec.Data.Skip(end).Take(12).Select(b => b.ToString("X2"));
                yield return $"'{prefix}' of '{m.Groups[2].Value}' tail=[{string.Join(" ", tail)}]";
            }
        }
    }

    public IEnumerable<string> Summarize()
    {
        foreach (var (prefix, count) in _prefixHistogram.OrderByDescending(kv => kv.Value))
            yield return $"summary prefix '{prefix} of': {count} objects";
    }
}
