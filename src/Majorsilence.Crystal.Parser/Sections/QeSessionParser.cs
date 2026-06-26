using System.Buffers.Binary;
using System.Text;

namespace Majorsilence.Crystal.Parser.Sections;

/// <summary>
/// Parses the QESession OLE stream.
///
/// Header format (cleartext):
///   4 bytes  "QENG" ASCII magic
///   4 bytes  version (uint32 LE, typically 1)
///   4 bytes  flags / size hint
///   4 bytes  additional flags
///
/// After the 16-byte header, the payload appears encrypted with the same
/// scheme as the Contents stream. The larger QESession (from reports with
/// dataset connections) contains embedded SQL and join definitions.
/// </summary>
public sealed class QeSessionParser
{
    private static readonly byte[] QengMagic = "QENG"u8.ToArray();

    public QeSessionRecord Parse(byte[] data)
    {
        var record = new QeSessionRecord();

        if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual(QengMagic))
            return record;

        record.IsValid = true;

        if (data.Length >= 8)
            record.Version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));

        if (data.Length >= 12)
            record.Flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));

        // The rest of the stream is encrypted. For small reports (e.g. thereport.rpt, 64 bytes)
        // there is essentially no query body. Larger files carry the full SQL engine session.
        record.PayloadLength = data.Length - 16;
        record.RawPayload = data.Length > 16 ? data.AsSpan(16).ToArray() : [];

        // Attempt to find readable ASCII strings in the payload as a heuristic
        record.ExtractedStrings = ExtractAsciiStrings(record.RawPayload, minLength: 4);

        return record;
    }

    private static List<string> ExtractAsciiStrings(byte[] data, int minLength)
    {
        var results = new List<string>();
        var sb = new StringBuilder();

        foreach (byte b in data)
        {
            if (b >= 0x20 && b < 0x7F)
            {
                sb.Append((char)b);
            }
            else
            {
                if (sb.Length >= minLength)
                    results.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length >= minLength)
            results.Add(sb.ToString());

        return results;
    }
}

public sealed class QeSessionRecord
{
    public bool IsValid { get; set; }
    public uint Version { get; set; }
    public uint Flags { get; set; }
    public int PayloadLength { get; set; }
    public byte[] RawPayload { get; set; } = [];
    public List<string> ExtractedStrings { get; set; } = [];
}
