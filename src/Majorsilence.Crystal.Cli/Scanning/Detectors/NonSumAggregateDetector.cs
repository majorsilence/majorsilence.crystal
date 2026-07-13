using System.Collections.Concurrent;

namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// Flags files whose tag-237 summary-field records use an aggregate function
/// other than Sum (tag-236 child, byte 22: 0x01=Sum, 0x02=Count, 0x03=DistinctCount,
/// 0x04=Min, 0x05=Max, 0x06=Average). Also accumulates a corpus-wide histogram of
/// every function code seen.
/// </summary>
public sealed class NonSumAggregateDetector : IFeatureDetector
{
    public string Id => "non-sum-aggregate";
    public string BacklogItem => "Non-Sum group footer aggregates";

    private readonly ConcurrentDictionary<int, int> _functionCodes = new();

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        foreach (var rec in ctx.Chunks.Where(r => r.Tag == 237))
        {
            var children = rec.ParseChildren();
            var ch236 = children.FirstOrDefault(c => c.Tag == 236)
                        ?? children.SelectMany(c => c.ParseChildren()).FirstOrDefault(c => c.Tag == 236);
            if (ch236 is null || ch236.Data.Length <= 22) continue;

            int fn = ch236.Data[22];
            _functionCodes.AddOrUpdate(fn, 1, (_, v) => v + 1);
            if (fn == 0x01) continue;

            string? name = ch236.ParseChildren().FirstOrDefault(c => c.Tag == 113)?.ReadMutf8String(0, out _);
            var strings = Mutf8Scanner.Scan(ch236.Data).Select(s => s.Text).Take(4);
            yield return $"fn=0x{fn:X2} name='{name}' strings=[{string.Join(", ", strings)}]";
        }
    }

    public IEnumerable<string> Summarize()
    {
        foreach (var (code, count) in _functionCodes.OrderBy(kv => kv.Key))
            yield return $"function code 0x{code:X2}: {count} tag-236 records";
    }
}
