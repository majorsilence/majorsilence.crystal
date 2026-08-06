using System.Collections.Concurrent;

namespace Majorsilence.Crystal.Cli.Scanning.Detectors;

/// <summary>
/// Every report object wrapper is immediately followed by a tag-266 ... tag-267
/// bracket (flat sequence, not nested in a child chunk). The bracket's contents
/// are fixed-size numeric records (tag 269, tag 270 observed) with no MUTF-8
/// strings — unlike the tag-255 section formula hooks, this is NOT a formula-name
/// table. The working hypothesis is Crystal's "Highlighting Expert" (static
/// value/threshold conditional formatting), stored as binary condition records
/// rather than formula references. This detector fingerprints the bracket's
/// "shape" (child tag:length pairs) corpus-wide: the dominant shape is the idle
/// (no conditional formatting configured) case; any other shape, or any MUTF-8
/// string found inside the bracket, is a candidate for a report that actually
/// uses the feature.
/// </summary>
public sealed class ObjectFormatHookDetector : IFeatureDetector
{
    public string Id => "object-format-hook";
    public string BacklogItem => "Object-level conditional formatting (tag 266-270 bracket)";

    private const int MaxExampleFiles = 5;

    private readonly ConcurrentDictionary<string, int> _shapeCounts = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _shapeExamples = new();

    // Per-tag, per-offset distinct-byte-value tracking — proves whether a payload
    // is templated filler (one dominant value per offset) or carries real content
    // (many distinct values per offset).
    private readonly ConcurrentDictionary<(int Tag, int Offset), ConcurrentDictionary<byte, int>> _byteHistogram = new();

    public IEnumerable<string> Inspect(ScanContext ctx)
    {
        var chunks = ctx.Chunks;
        var starts = new Stack<int>();

        for (int i = 0; i < chunks.Count; i++)
        {
            int tag = chunks[i].Tag;
            if (tag == 266) { starts.Push(i); continue; }
            if (tag != 267 || starts.Count == 0) continue;

            int start = starts.Pop();
            var inner = chunks.Skip(start + 1).Take(i - start - 1).ToList();
            string shape = string.Join(",", inner.Select(r => $"{r.Tag}:{r.Data.Length}"));

            _shapeCounts.AddOrUpdate(shape, 1, (_, v) => v + 1);
            var files = _shapeExamples.GetOrAdd(shape, _ => new ConcurrentQueue<string>());
            if (files.Count < MaxExampleFiles && !files.Contains(ctx.RelativePath))
                files.Enqueue(ctx.RelativePath);

            foreach (var rec in inner.Where(r => r.Tag is 269 or 274))
            {
                for (int off = 0; off < rec.Data.Length; off++)
                {
                    var hist = _byteHistogram.GetOrAdd((rec.Tag, off), _ => new ConcurrentDictionary<byte, int>());
                    hist.AddOrUpdate(rec.Data[off], 1, (_, v) => v + 1);
                }
            }

            foreach (var rec in inner.Prepend(chunks[start]))
            {
                foreach (var (_, _, text) in Scanning.Mutf8Scanner.Scan(rec.Data))
                    yield return $"string '{text}' inside tag-{rec.Tag} of a 266..267 bracket at {chunks[start].StreamOffset}";
            }
        }
    }

    public IEnumerable<string> Summarize()
    {
        var ordered = _shapeCounts.OrderByDescending(kv => kv.Value).ToList();
        if (ordered.Count == 0) yield break;

        yield return $"bracket shapes observed: {ordered.Count} distinct, {ordered.Sum(kv => kv.Value)} total brackets";
        yield return $"dominant (idle) shape: [{ordered[0].Key}] × {ordered[0].Value}";

        foreach (var (shape, count) in ordered.Skip(1).Take(5))
        {
            string files = _shapeExamples.TryGetValue(shape, out var q) ? string.Join("; ", q) : "";
            yield return $"variant shape [{shape}] × {count}  e.g. {files}";
        }

        foreach (var tag in new[] { 269, 274 })
        {
            var offsets = _byteHistogram.Keys.Where(k => k.Tag == tag && k.Offset >= 0).OrderBy(k => k.Offset).ToList();
            if (offsets.Count == 0) continue;
            yield return $"tag-{tag} payload byte diversity (offset: distinct-value-count[range]):";
            foreach (var key in offsets)
            {
                var hist = _byteHistogram[key];
                if (hist.Count <= 1) continue;   // constant byte — not interesting
                var top = hist.OrderByDescending(kv => kv.Value).First();
                yield return $"    [{key.Offset}] {hist.Count} distinct values, dominant 0x{top.Key:X2}×{top.Value}, " +
                             $"range 0x{hist.Keys.Min():X2}-0x{hist.Keys.Max():X2}";
            }
        }
    }
}
