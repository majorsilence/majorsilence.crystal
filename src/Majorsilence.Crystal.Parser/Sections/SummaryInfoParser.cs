using System.Buffers.Binary;
using System.Text;

namespace Majorsilence.Crystal.Parser.Sections;

/// <summary>
/// Reads the \x05SummaryInformation OLE stream (standard property set).
/// Extracts PIDSI_TITLE(2), PIDSI_AUTHOR(4), and PIDSI_COMMENTS(6) for the report metadata.
/// </summary>
public static class SummaryInfoParser
{
    public static (string Title, string Author, string Comments) Parse(byte[] data)
    {
        string title = string.Empty, author = string.Empty, comments = string.Empty;
        // Property set header: byte order(2), version(2), OS(4), clsid(16), count(4) = 28 bytes
        // Then: FMTID(16) + offset(4) = 20 bytes → property set data at offset 48
        const int setStart = 0x30; // 48
        if (data.Length < setStart + 8) return (title, author, comments);
        uint propCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(setStart + 4));
        for (int i = 0; i < propCount && i < 64; i++)
        {
            int entryOff = setStart + 8 + i * 8;
            if (entryOff + 8 > data.Length) break;
            uint propId  = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entryOff));
            uint propOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entryOff + 4));
            int valOff = setStart + (int)propOff;
            if (valOff + 8 > data.Length) continue;
            uint valType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(valOff));
            string? s = valType switch
            {
                0x001E => ReadLpStr(data, valOff + 4),   // VT_LPSTR
                0x001F => ReadLpWStr(data, valOff + 4),  // VT_LPWSTR
                _ => null
            };
            if (s is null) continue;
            if (propId == 2) title = s;
            else if (propId == 4) author = s;
            else if (propId == 6) comments = s;
        }
        return (title, author, comments);
    }

    private static string? ReadLpStr(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return null;
        uint sz = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        if (sz == 0 || sz > 512 || offset + 4 + sz > data.Length) return null;
        return Encoding.Latin1.GetString(data, offset + 4, (int)sz).TrimEnd('\0');
    }

    private static string? ReadLpWStr(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return null;
        uint charCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        if (charCount == 0 || charCount > 512 || offset + 4 + charCount * 2 > data.Length) return null;
        return Encoding.Unicode.GetString(data, offset + 4, (int)charCount * 2).TrimEnd('\0');
    }
}
