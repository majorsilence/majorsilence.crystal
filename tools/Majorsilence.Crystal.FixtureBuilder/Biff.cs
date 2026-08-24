using System.Text;
using Majorsilence.Crystal.Parser.OleStorage;

namespace Majorsilence.Crystal.FixtureBuilder;

/// <summary>
/// Just enough BIFF8 to read a cell grid out of Crystal's data-only Excel export: the
/// shared-string table and the handful of cell records Crystal actually emits. Not a
/// spreadsheet reader - it ignores formatting, formulas, and everything about the workbook
/// beyond its cell values, because a data fixture needs nothing else.
/// </summary>
internal static class Biff
{
    internal readonly record struct CellRef(int Row, int Col);

    // BIFF record types used here.
    private const int SST = 0x00FC;
    private const int CONTINUE = 0x003C;
    private const int LABELSST = 0x00FD;
    private const int LABEL = 0x0204;
    private const int NUMBER = 0x0203;
    private const int RK = 0x027E;
    private const int MULRK = 0x00BD;

    internal static Dictionary<CellRef, object> ReadGrid(string xlsPath)
    {
        byte[] workbook = ReadWorkbookStream(xlsPath);
        var records = ReadRecords(workbook);
        var sst = ReadSharedStrings(records);

        var cells = new Dictionary<CellRef, object>();
        foreach (var (id, data) in records)
        {
            switch (id)
            {
                case LABELSST when data.Length >= 10:
                {
                    int idx = ReadInt32LE(data, 6);
                    if (idx < 0 || idx >= sst.Count)
                        throw new InvalidDataException(
                            $"Shared-string index {idx} is outside the {sst.Count}-entry table. " +
                            "The table was read short, which would silently blank every text " +
                            "cell past that point rather than fail.");
                    cells[new CellRef(ReadUInt16LE(data, 0), ReadUInt16LE(data, 2))] = sst[idx];
                    break;
                }
                case LABEL when data.Length >= 8:
                    cells[new CellRef(ReadUInt16LE(data, 0), ReadUInt16LE(data, 2))] =
                        new Cursor(new List<byte[]> { data }, 6).ReadString();
                    break;

                case NUMBER when data.Length >= 14:
                    cells[new CellRef(ReadUInt16LE(data, 0), ReadUInt16LE(data, 2))] =
                        BitConverter.ToDouble(data, 6);
                    break;

                case RK when data.Length >= 10:
                    cells[new CellRef(ReadUInt16LE(data, 0), ReadUInt16LE(data, 2))] =
                        DecodeRk(ReadUInt32LE(data, 6));
                    break;

                case MULRK when data.Length >= 6:
                {
                    int row = ReadUInt16LE(data, 0), first = ReadUInt16LE(data, 2);
                    int count = (data.Length - 6) / 6;
                    for (int k = 0; k < count; k++)
                        cells[new CellRef(row, first + k)] = DecodeRk(ReadUInt32LE(data, 4 + k * 6 + 2));
                    break;
                }
            }
        }
        return cells;
    }

    private static byte[] ReadWorkbookStream(string xlsPath)
    {
        using var ole = OleReader.Open(xlsPath);
        foreach (var name in new[] { "Workbook", "Book" })
            if (ole.HasStream(name))
                return ole.ReadStream(name);
        throw new InvalidDataException(
            "No Workbook stream. Streams present: " + string.Join(", ", ole.ListStreamNames()));
    }

    private static List<(int Id, byte[] Data)> ReadRecords(byte[] workbook)
    {
        var records = new List<(int, byte[])>();
        int pos = 0;
        while (pos + 4 <= workbook.Length)
        {
            int id = ReadUInt16LE(workbook, pos);
            int len = ReadUInt16LE(workbook, pos + 2);
            pos += 4;
            if (pos + len > workbook.Length) break;
            records.Add((id, workbook.AsSpan(pos, len).ToArray()));
            pos += len;
        }
        return records;
    }

    private static List<string> ReadSharedStrings(List<(int Id, byte[] Data)> records)
    {
        int at = records.FindIndex(r => r.Id == SST);
        if (at < 0) return [];

        var payloads = new List<byte[]> { records[at].Data };
        for (int i = at + 1; i < records.Count && records[i].Id == CONTINUE; i++)
            payloads.Add(records[i].Data);

        var cursor = new Cursor(payloads, 0);
        cursor.Skip(4);                        // total occurrences, not needed
        int unique = cursor.ReadInt32();

        var strings = new List<string>(Math.Max(0, unique));
        for (int i = 0; i < unique; i++)
            strings.Add(cursor.ReadString());
        return strings;
    }

    /// <summary>
    /// A position in the SST's records, read as one logical stream.
    ///
    /// The table spills into CONTINUE records, and a string may be cut in half by that
    /// boundary. What makes it more than concatenation is that the second half restarts
    /// with its own compression flag: the tail of a byte-per-character string can continue
    /// as two bytes per character, and vice versa. Concatenating the payloads and reading
    /// straight through therefore goes out of step at the first split string and stays
    /// that way - which is what silently blanked every text cell past the first boundary
    /// when this was first written.
    /// </summary>
    private sealed class Cursor(List<byte[]> payloads, int offset)
    {
        private int _rec;
        private int _pos = offset;

        private byte[] Current => payloads[_rec];

        private bool AtRecordEnd => _pos >= Current.Length;

        /// <summary>Moves to the next record. Returns false at the end of the table.</summary>
        private bool NextRecord()
        {
            if (_rec + 1 >= payloads.Count) return false;
            _rec++;
            _pos = 0;
            return true;
        }

        private byte ReadByte()
        {
            while (AtRecordEnd)
                if (!NextRecord())
                    throw new InvalidDataException("shared-string table ended mid-value");
            return Current[_pos++];
        }

        internal void Skip(int count)
        {
            for (int i = 0; i < count; i++) ReadByte();
        }

        private int ReadUInt16() => ReadByte() | (ReadByte() << 8);

        internal int ReadInt32() =>
            ReadByte() | (ReadByte() << 8) | (ReadByte() << 16) | (ReadByte() << 24);

        /// <summary>XLUnicodeRichExtendedString: length, flags, then the characters.</summary>
        internal string ReadString()
        {
            int charCount = ReadUInt16();
            byte flags = ReadByte();

            bool wide = (flags & 0x01) != 0;
            bool hasFarEast = (flags & 0x04) != 0;
            bool rich = (flags & 0x08) != 0;

            int runs = rich ? ReadUInt16() : 0;
            int farEastLength = hasFarEast ? ReadInt32() : 0;

            var text = new StringBuilder(charCount);
            int read = 0;
            while (read < charCount)
            {
                // A record boundary inside the characters restarts the encoding, so the
                // flag byte has to be re-read before the remainder.
                if (AtRecordEnd)
                {
                    if (!NextRecord())
                        throw new InvalidDataException("shared string ended mid-text");
                    wide = (ReadByte() & 0x01) != 0;
                }

                int available = Current.Length - _pos;
                int wanted = charCount - read;
                int take = wide ? Math.Min(wanted, available / 2) : Math.Min(wanted, available);

                // A wide character split down the middle by the boundary: no whole
                // character fits, so read it a byte at a time to cross the seam.
                if (take == 0)
                {
                    int lo = ReadByte(), hi = ReadByte();
                    text.Append((char)(lo | (hi << 8)));
                    read++;
                    continue;
                }

                text.Append(wide
                    ? Encoding.Unicode.GetString(Current, _pos, take * 2)
                    : Encoding.Latin1.GetString(Current, _pos, take));
                _pos += wide ? take * 2 : take;
                read += take;
            }

            Skip(runs * 4 + farEastLength);
            return text.ToString();
        }
    }

    /// <summary>
    /// RK packs a number into 32 bits: the low two bits say whether the rest is a
    /// truncated IEEE double or a 30-bit integer, and whether to divide by 100.
    /// </summary>
    private static double DecodeRk(uint rk)
    {
        bool hundredths = (rk & 0x01) != 0;
        bool integer = (rk & 0x02) != 0;
        double value = integer
            ? (int)rk >> 2
            : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
        return hundredths ? value / 100.0 : value;
    }

    private static int ReadUInt16LE(byte[] b, int o) => b[o] | (b[o + 1] << 8);
    private static uint ReadUInt32LE(byte[] b, int o) =>
        (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static int ReadInt32LE(byte[] b, int o) => (int)ReadUInt32LE(b, o);
}
