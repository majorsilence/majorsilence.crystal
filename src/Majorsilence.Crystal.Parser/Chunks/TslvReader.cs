namespace Majorsilence.Crystal.Parser.Chunks;

/// <summary>
/// Reads Crystal Reports TSLV (Tag-Schema-Length-Value) records from the inflated Contents payload.
///
/// TSLV record header format (all records in the inflated payload use n2=0xF8 pattern):
///   n2 (byte 0): control byte
///     bits 7,6: size field length (11→4 bytes, 10→2 bytes, 01→1 byte, 00→0 bytes)
///     bit 5:    schema present (2 bytes follow)
///     bit 3:    useSimpleEncryption — record data XOR'd with (tag &amp; 0xFF)
///     bit 2:    tag in separate 2-byte field; else embedded in n2[1:0] and n3
///   n3 (byte 1): low 8 bits of tag (when bit2=0)
///   [2 bytes]: schema if bit5=1 (big-endian)
///   [0/1/2/4 bytes]: data size (big-endian)
///
/// XOR rule: when useSimpleEncryption (bit3=1), data[i] = raw[i] ^ (tag &amp; 0xFF).
/// This is invariant across nesting levels:
///   nested record's data = parent_decoded_payload[headerLen+i] ^ (nested_tag &amp; 0xFF)
/// so ParseDecoded() can be applied recursively.
/// </summary>
public static class TslvReader
{
    /// <summary>Parse flat records from the raw inflated TSLV stream (j=0 initially).</summary>
    public static List<TslvRecord> ReadAll(byte[] stream) =>
        ParseStream(stream, 0, stream.Length, xorKey: 0);

    /// <summary>
    /// Parse nested records from a parent record's already-decoded Data payload.
    /// The parent's XOR has already been applied; nested records apply their own XOR.
    /// </summary>
    public static List<TslvRecord> ParseDecoded(byte[] decodedData) =>
        ParseStream(decodedData, 0, decodedData.Length, xorKey: 0);

    private static List<TslvRecord> ParseStream(byte[] bytes, int start, int length, int xorKey)
    {
        var records = new List<TslvRecord>();
        int pos = start;
        int end = start + length;

        while (pos + 2 <= end)
        {
            if (!TryReadHeader(bytes, pos, end, xorKey, out int tag, out int schema, out int dataSize, out int headerLen))
                break;

            int dataStart = pos + headerLen;
            int available = Math.Min(dataSize, end - dataStart);
            byte tagLow = (byte)(tag & 0xFF);

            // Decode: data[i] = raw[i] ^ tagLow
            byte[] decoded = new byte[available];
            for (int i = 0; i < available; i++)
                decoded[i] = (byte)(bytes[dataStart + i] ^ tagLow);

            records.Add(new TslvRecord
            {
                Tag = tag,
                Schema = schema,
                Data = decoded,
                StreamOffset = pos
            });

            pos = dataStart + dataSize;
            if (pos > end) break;
        }

        return records;
    }

    private static bool TryReadHeader(byte[] bytes, int pos, int end, int outerXor,
        out int tag, out int schema, out int dataSize, out int headerLen)
    {
        tag = schema = dataSize = headerLen = 0;
        if (pos + 2 > end) return false;

        // Header bytes are part of the outer stream — if caller has already decoded them
        // (via ParseDecoded), they come through unchanged (outerXor=0 at every level).
        byte n2 = (byte)(bytes[pos] ^ outerXor);
        byte n3 = (byte)(bytes[pos + 1] ^ outerXor);
        int cur = pos + 2;

        bool hasSeparateTag = (n2 & 0x04) != 0;
        bool hasSchema = (n2 & 0x20) != 0;
        int sizeLen = GetSizeFieldLen(n2);

        if (hasSeparateTag)
        {
            if (cur + 2 > end) return false;
            tag = ((bytes[cur] ^ outerXor) << 8) | (bytes[cur + 1] ^ outerXor);
            cur += 2;
        }
        else
        {
            tag = ((n2 & 3) << 8) | n3;
        }

        if (hasSchema)
        {
            if (cur + 2 > end) return false;
            schema = ((bytes[cur] ^ outerXor) << 8) | (bytes[cur + 1] ^ outerXor);
            cur += 2;
        }

        if (sizeLen > 0)
        {
            if (cur + sizeLen > end) return false;
            dataSize = 0;
            for (int i = 0; i < sizeLen; i++)
                dataSize = (dataSize << 8) | (bytes[cur + i] ^ outerXor);
            cur += sizeLen;
        }

        headerLen = cur - pos;
        return tag >= 0 && dataSize >= 0;
    }

    private static int GetSizeFieldLen(byte n2)
    {
        int bits = (n2 >> 6) & 3;
        return bits switch { 3 => 4, 2 => 2, 1 => 1, _ => 0 };
    }
}
