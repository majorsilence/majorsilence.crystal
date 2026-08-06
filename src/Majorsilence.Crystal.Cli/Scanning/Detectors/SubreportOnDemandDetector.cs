using System.Collections.Concurrent;

namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// tag-163 (subreport wrapper) byte[88] — candidate "on demand" flag, isolated by
/// diffing benbrahim777__USAvsFranceOnDemand.rpt (byte=0x01) against otherwise-identical
/// subreport wrappers in non-on-demand files (byte=0x00). Corpus-wide value histogram,
/// keyed by record length too since byte[88] only means anything for the record length
/// where it's been diffed against a known counter-example.
/// </summary>
public sealed class SubreportOnDemandDetector : IFeatureDetector
{
    public string Id => "subreport-ondemand-byte";
    public string BacklogItem => "On-demand subreports";

    private const int MaxExampleFiles = 5;
    private readonly ConcurrentDictionary<(int Len, byte Value), int> _histogram = new();
    private readonly ConcurrentDictionary<(int Len, byte Value), ConcurrentQueue<string>> _examples = new();

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        foreach (var rec in ctx.Chunks.Where(r => r.Tag == 163))
        {
            if (rec.Data.Length <= 88) continue;
            var key = (rec.Data.Length, rec.Data[88]);   // absolute offset — only clean for len=107
            _histogram.AddOrUpdate(key, 1, (_, v) => v + 1);
            var files = _examples.GetOrAdd(key, _ => new ConcurrentQueue<string>());
            if (files.Count < MaxExampleFiles && !files.Contains(ctx.RelativePath))
                files.Enqueue(ctx.RelativePath);
        }
        yield break;
    }

    public IEnumerable<string> Summarize()
    {
        foreach (var (key, count) in _histogram.OrderByDescending(kv => kv.Value))
        {
            string files = _examples.TryGetValue(key, out var q) ? string.Join("; ", q) : "";
            yield return $"len={key.Len} byte[88]=0x{key.Value:X2} x{count}  e.g. {files}";
        }
    }
}
