using System.Buffers.Binary;
using System.Text;

namespace Majorsilence.Crystal.Cli.Scanning;

/// <summary>
/// Scans a raw TSLV payload for Crystal MUTF-8 strings: BE-Int32 length
/// (including the null terminator) followed by UTF-8 bytes and a null byte.
/// </summary>
public static class Mutf8Scanner
{
    public static List<(int Start, int End, string Text)> Scan(byte[] data, int minChars = 2, int maxChars = 200)
    {
        var found = new List<(int, int, string)>();
        for (int i = 0; i + 6 <= data.Length; i++)
        {
            int n = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i));
            if (n < minChars || n > maxChars || i + 4 + n > data.Length) continue;
            bool ok = true;
            for (int j = i + 4; j < i + 4 + n - 1; j++)
                if (data[j] < 0x20) { ok = false; break; }
            if (!ok || data[i + 4 + n - 1] != 0) continue;
            string s = Encoding.UTF8.GetString(data, i + 4, n - 1);
            if (s.Length >= minChars && s.Any(char.IsLetter))
                found.Add((i, i + 4 + n, s));
        }
        return found;
    }
}
