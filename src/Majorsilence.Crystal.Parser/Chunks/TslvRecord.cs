using System.Buffers.Binary;
using System.Text;

namespace Majorsilence.Crystal.Parser.Chunks;

/// <summary>
/// A decoded TSLV (Tag-Schema-Length-Value) record from the Crystal Reports Contents stream.
/// Data is already XOR-decoded (data[i] = raw[i] ^ (tag &amp; 0xFF)).
/// All multi-byte integers in Data use big-endian byte order (DataInputStream convention).
/// </summary>
public sealed class TslvRecord
{
    public int Tag { get; init; }
    public int Schema { get; init; }
    public byte[] Data { get; init; } = [];
    public int StreamOffset { get; init; }

    // Section wrapper start tags (contain nested tag-140 Section header in payload)
    public static bool IsSectionStart(int tag) => tag is 141 or 143 or 145 or 147 or 149 or 151 or 153;
    public static bool IsSectionEnd(int tag) => tag is 142 or 144 or 146 or 148 or 150 or 152 or 154;

    public static SectionKind SectionKindFromTag(int tag) => tag switch
    {
        141 or 142 => SectionKind.ReportHeader,
        143 or 144 => SectionKind.ReportFooter,
        145 or 146 => SectionKind.PageHeader,
        147 or 148 => SectionKind.PageFooter,
        149 or 150 => SectionKind.Detail,
        151 or 152 => SectionKind.GroupHeader,
        153 or 154 => SectionKind.GroupFooter,
        _ => SectionKind.Unknown
    };

    // Parse nested sub-records from this record's decoded Data payload
    public List<TslvRecord> ParseChildren() => TslvReader.ParseDecoded(Data);

    // --- Big-endian readers (DataInputStream convention) ---
    public int ReadInt32BE(int offset) =>
        offset + 4 <= Data.Length ? BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan(offset)) : 0;

    public int ReadInt16BE(int offset) =>
        offset + 2 <= Data.Length ? BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(offset)) : 0;

    // --- Little-endian readers (field type codes and some metadata) ---
    public int ReadInt16LE(int offset) =>
        offset + 2 <= Data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(offset)) : 0;

    // Crystal MUTF-8 string: int32 length (incl null terminator), then bytes
    // Returns null if n==0, empty if n==1, else n-1 bytes decoded as MUTF-8
    public string? ReadMutf8String(int offset, out int bytesConsumed)
    {
        bytesConsumed = 0;
        if (offset + 4 > Data.Length) return null;
        int n = ReadInt32BE(offset);
        bytesConsumed = 4;
        if (n <= 0) return null;
        if (n == 1)
        {
            bytesConsumed += 1;  // one zero byte
            return string.Empty;
        }
        if (offset + 4 + n > Data.Length) return null;
        bytesConsumed += n;
        // Decode Java MUTF-8: like UTF-8 but null is 0xC0 0x80 and no 4-byte sequences.
        int strLen = n - 1;  // last byte is null terminator
        var sb = new StringBuilder(strLen);
        int end = offset + 4 + strLen;
        for (int i = offset + 4; i < end; )
        {
            byte b = Data[i];
            if (b == 0) { i++; continue; }  // embedded null (shouldn't appear in names)
            if (b < 0x80) { sb.Append((char)b); i++; }
            else if ((b & 0xE0) == 0xC0 && i + 1 < end)
            {
                // 2-byte sequence: U+0080..U+07FF
                int cp = ((b & 0x1F) << 6) | (Data[i + 1] & 0x3F);
                sb.Append((char)cp);
                i += 2;
            }
            else if ((b & 0xF0) == 0xE0 && i + 2 < end)
            {
                // 3-byte sequence: U+0800..U+FFFF
                int cp = ((b & 0x0F) << 12) | ((Data[i + 1] & 0x3F) << 6) | (Data[i + 2] & 0x3F);
                sb.Append((char)cp);
                i += 3;
            }
            else { i++; }  // invalid/truncated sequence — skip byte
        }
        return sb.ToString();
    }
}

public enum SectionKind
{
    Unknown,
    ReportHeader,
    ReportFooter,
    PageHeader,
    PageFooter,
    Detail,
    GroupHeader,
    GroupFooter,
}
