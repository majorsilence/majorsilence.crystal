using System.Buffers.Binary;

namespace Majorsilence.Crystal.Parser.Sections;

/// <summary>
/// Parses the ReportInfo OLE stream.
///
/// Format: series of TLV records where tag (2 bytes LE) + total_size (2 bytes LE, includes tag+size) + data.
/// This stream is NOT encrypted and is present in all CR versions.
/// </summary>
public sealed class ReportInfoParser
{
    public ReportInfoRecord Parse(byte[] data)
    {
        var record = new ReportInfoRecord();
        int pos = 0;

        while (pos + 4 <= data.Length)
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            ushort totalSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
            pos += 4;

            int dataSize = totalSize > 4 ? totalSize - 4 : 0;
            if (dataSize > data.Length - pos) break;

            var payload = data.AsSpan(pos, dataSize);

            switch (tag)
            {
                case 0x0030:
                    record.FormatVersion = dataSize >= 2
                        ? BinaryPrimitives.ReadInt16LittleEndian(payload)
                        : (short)0;
                    break;
                case 0x04F0:
                    record.Flag04F0 = dataSize >= 4
                        ? BinaryPrimitives.ReadInt32LittleEndian(payload)
                        : 0;
                    break;
                default:
                    record.UnknownTags[tag] = payload.ToArray();
                    break;
            }

            pos += dataSize;
        }

        return record;
    }
}

public sealed class ReportInfoRecord
{
    public short FormatVersion { get; set; }
    public int Flag04F0 { get; set; }
    public Dictionary<ushort, byte[]> UnknownTags { get; } = [];
}
