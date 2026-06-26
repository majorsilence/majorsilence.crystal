using System.Text;
using Majorsilence.Crystal.Parser.OleStorage;

// Empirical decryption analysis for Crystal Reports .rpt files.
// Tries multiple cipher candidates and scores each by readable-string yield.

var paths = args.Length > 0
    ? args
    : new[]
    {
        "/home/peter/source/repos/CrystalCmd/thereport.rpt",
        "/home/peter/source/repos/CrystalCmd/the_java_dataset_report.rpt"
    };

foreach (var path in paths)
{
    Console.WriteLine($"\n{'=',60}");
    Console.WriteLine($"FILE: {Path.GetFileName(path)}");
    Console.WriteLine($"{'=',60}");

    using var ole = OleReader.Open(path);

    foreach (var streamName in new[] { "Contents", "QESession" })
    {
        if (!ole.HasStream(streamName)) continue;
        byte[] raw = ole.ReadStream(streamName);
        Console.WriteLine($"\n--- Stream: {streamName} ({raw.Length} bytes) ---");
        AnalyseStream(streamName, raw);
    }
}

static void AnalyseStream(string streamName, byte[] raw)
{
    int dataStart;
    List<byte[]> keyCandidates;

    if (streamName == "Contents" && raw.Length >= 24 && raw[0] == 0xFC)
    {
        dataStart = raw[9];
        if (dataStart == 0 || dataStart > raw.Length) dataStart = 24;

        keyCandidates =
        [
            raw[16..24],
            raw[10..24],
            raw[10..16],
            raw[16..(raw.Length < 32 ? raw.Length : 32)],
        ];

        Console.WriteLine($"  CR version={BitConverter.ToInt32(raw, 4)}, data-start=0x{dataStart:X2}");
        Console.WriteLine($"  Key candidate (bytes 16-23): {Hex(raw[16..24])}");
    }
    else if (streamName == "QESession" && raw.Length >= 16 &&
             raw[0] == 'Q' && raw[1] == 'E' && raw[2] == 'N' && raw[3] == 'G')
    {
        dataStart = 16;
        if (raw.Length > 18 && raw[16] == 0x01 && raw[17] == 0x00) dataStart = 18;

        keyCandidates =
        [
            raw[4..8],
            raw[8..12],
            raw[8..16],
            raw[4..16],
        ];

        Console.WriteLine($"  QENG version={BitConverter.ToUInt32(raw, 4)}, flags=0x{BitConverter.ToUInt32(raw, 8):X8}");
        Console.WriteLine($"  Data starts at offset {dataStart} ({raw.Length - dataStart} payload bytes)");
    }
    else
    {
        dataStart = 0;
        keyCandidates = [raw[..Math.Min(8, raw.Length)]];
    }

    byte[] ciphertext = raw[dataStart..];
    if (ciphertext.Length == 0)
    {
        Console.WriteLine("  No payload.");
        return;
    }

    var results = new List<(string label, byte[] plaintext, double score)>();

    foreach (var key in keyCandidates)
    {
        results.Add(($"RollingXOR({Hex(key)})", XorCycle(ciphertext, key), 0));
        results.Add(($"RC4({Hex(key)})", RC4(ciphertext, key), 0));
        results.Add(($"RC4rev({Hex(Reverse(key))})", RC4(ciphertext, Reverse(key)), 0));
    }

    foreach (byte b in new byte[] { 0x00, 0x01, 0x55, 0xAA, 0xFF, 0x36, 0x5A, 0x7F })
        results.Add(($"ConstXOR(0x{b:X2})", XorConst(ciphertext, b), 0));

    uint seed16 = raw.Length >= 20 ? BitConverter.ToUInt32(raw, 16) : 0;
    uint seed8  = raw.Length >= 12 ? BitConverter.ToUInt32(raw, 8) : 0;
    results.Add(($"LCG_Borland(seed=0x{seed16:X8})", LCGDecrypt(ciphertext, seed16, 0), 0));
    results.Add(($"LCG_Borland(seed=flags)", LCGDecrypt(ciphertext, seed8, 0), 0));
    results.Add(($"LCG_MINSTD(seed=0x{seed16:X8})", LCGDecrypt(ciphertext, seed16, 1), 0));

    for (int i = 0; i < results.Count; i++)
    {
        var (label, pt, _) = results[i];
        results[i] = (label, pt, ReadableScore(pt));
    }

    results.Sort((a, b) => b.score.CompareTo(a.score));

    Console.WriteLine($"\n  Top 8 candidates (scored by readable-string density):");
    foreach (var (label, pt, score) in results.Take(8))
    {
        var strings = ExtractAscii(pt, minLen: 4).Take(6).ToList();
        int show = Math.Min(48, pt.Length);
        Console.WriteLine($"  [{score:F3}] {label}");
        Console.WriteLine($"         hex: {Hex(pt[..show])}");
        Console.WriteLine($"         asc: {Ascii(pt[..show])}");
        if (strings.Count > 0)
            Console.WriteLine($"         str: {string.Join(" | ", strings)}");
    }
}

static byte[] XorCycle(byte[] data, byte[] key)
{
    var r = new byte[data.Length];
    for (int i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ key[i % key.Length]);
    return r;
}

static byte[] XorConst(byte[] data, byte c)
{
    var r = new byte[data.Length];
    for (int i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ c);
    return r;
}

static byte[] RC4(byte[] data, byte[] key)
{
    var s = new byte[256];
    for (int i = 0; i < 256; i++) s[i] = (byte)i;
    int j = 0;
    for (int i = 0; i < 256; i++)
    {
        j = (j + s[i] + key[i % key.Length]) & 0xFF;
        (s[i], s[j]) = (s[j], s[i]);
    }
    var r = new byte[data.Length];
    int x = 0, y = 0;
    for (int i = 0; i < data.Length; i++)
    {
        x = (x + 1) & 0xFF;
        y = (y + s[x]) & 0xFF;
        (s[x], s[y]) = (s[y], s[x]);
        r[i] = (byte)(data[i] ^ s[(s[x] + s[y]) & 0xFF]);
    }
    return r;
}

static byte[] LCGDecrypt(byte[] data, uint seed, int kind) // 0=Borland, 1=MinStd
{
    var r = new byte[data.Length];
    uint state = seed;
    for (int i = 0; i < data.Length; i++)
    {
        switch (kind)
        {
            case 0: // Borland
                state = state * 0x015A4E35u + 1u;
                r[i] = (byte)(data[i] ^ (state >> 16));
                break;
            case 1: // MinStd
                state = (uint)((ulong)state * 16807 % 2147483647);
                r[i] = (byte)(data[i] ^ (byte)state);
                break;
        }
    }
    return r;
}

static double ReadableScore(byte[] data)
{
    int printable = data.Count(b => b is >= 0x20 and < 0x7F);
    double density = (double)printable / data.Length;
    int maxRun = 0, run = 0;
    foreach (byte b in data)
    {
        if (b is >= 0x20 and < 0x7F) run++;
        else { maxRun = Math.Max(maxRun, run); run = 0; }
    }
    maxRun = Math.Max(maxRun, run);
    return density * Math.Log(maxRun + 1);
}

static IEnumerable<string> ExtractAscii(byte[] data, int minLen = 4)
{
    var sb = new StringBuilder();
    foreach (byte b in data)
    {
        if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
        else
        {
            if (sb.Length >= minLen) yield return sb.ToString();
            sb.Clear();
        }
    }
    if (sb.Length >= minLen) yield return sb.ToString();
}

static byte[] Reverse(byte[] b) { var r = (byte[])b.Clone(); Array.Reverse(r); return r; }
static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));
static string Ascii(byte[] b) =>
    new(b.Select(x => x is >= 0x20 and < 0x7F ? (char)x : '.').ToArray());
