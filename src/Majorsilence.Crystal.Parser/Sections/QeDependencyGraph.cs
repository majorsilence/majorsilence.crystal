using System.Buffers.Binary;
using System.Text;

namespace Majorsilence.Crystal.Parser.Sections;

/// <summary>
/// The QueryEngine's dependency graph, read from the decrypted <c>QESession</c> stream: the
/// data source, every table the report reads with its fields, and the links between the
/// tables. Field ids are what the links refer to.
/// </summary>
public sealed class QeDependencyGraph
{
    /// <summary>The driver the report connects through, as its library name (for example <c>crdb_odbc.dll</c>).</summary>
    public string? DriverName { get; init; }

    /// <summary>The driver's own name for the kind of database (for example <c>ODBC (RDO)</c>).</summary>
    public string? DatabaseType { get; init; }

    /// <summary>The database, as the driver names it: a file path, a DSN, or a server name.</summary>
    public string? DatabaseName { get; init; }

    public List<QeTable> Tables { get; init; } = [];

    /// <summary>The links in the order the Crystal runtime applies them (ascending id).</summary>
    public List<QeLink> Links { get; init; } = [];
}

public sealed class QeTable
{
    public int Id { get; init; }

    /// <summary>The table's name in the database.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The name with its qualifier, as the driver writes it (for example <c>dbo.Orders</c>).</summary>
    public string QualifiedName { get; init; } = string.Empty;

    /// <summary>The qualifier's parts, outermost first: the catalog and schema a driver records.</summary>
    public IReadOnlyList<string> QualifierParts { get; init; } = [];

    /// <summary>The name the report uses for the table; the name itself unless the designer renamed it.</summary>
    public string Alias { get; init; } = string.Empty;

    public List<QeField> Fields { get; init; } = [];
}

public sealed class QeField
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>Crystal's field value type code, the numbering <c>RptParser</c> maps for the field dictionary.</summary>
    public int ValueType { get; init; }

    /// <summary>The field's length in bytes as the driver reports it, or -1 for unbounded.</summary>
    public int Length { get; init; }
}

public sealed class QeLink
{
    public int Id { get; init; }
    public int SourceFieldId { get; init; }
    public int TargetFieldId { get; init; }

    /// <summary>The comparison code; 4 is equality, the only one the corpora use.</summary>
    public int OperatorCode { get; init; }

    /// <summary>The join type code: 1 inner (Crystal's equal join), 2 left outer.</summary>
    public int JoinTypeCode { get; init; }

    /// <summary>A trailing flag, 1 in every corpus file; its meaning is not established.</summary>
    public int Flag { get; init; }
}

/// <summary>
/// Reads a <see cref="QeDependencyGraph"/> out of the decrypted, inflated <c>QESession</c>
/// payload.
/// </summary>
/// <remarks>
/// The payload is a tree of records in the same tag-schema-length-value framing as the
/// Contents stream, each record's bytes masked with its own tag on top of its parents'
/// masks. A record's body is not a plain list of child records though: integers, strings
/// and child records are interleaved in an order fixed by the record's type. So rather than
/// decode every type, this reader scans each decoded body for record headers whose declared
/// length fits, and decodes only the records it needs:
/// <list type="bullet">
///   <item>tag 2, schema 0x0902: the data source, opening with the driver, type and database;</item>
///   <item>tag 3, schema 0x0905: a table: id, name, qualified name, the qualifier's parts as a
///     counted list, the alias, then its fields;</item>
///   <item>tag 4, schema 0x0905 or 0x0906: a field: id, name, value type, length;</item>
///   <item>tag 10, schema 0x0901: a link: id, source field id, target field id, operator,
///     join type, flag.</item>
/// </list>
/// Strings are a big-endian length that counts the terminating null, then the bytes.
/// Checked against the Crystal runtime's own object model for every table, alias, field
/// count and link in the public and third-party corpora (BACKLOG has the counts).
/// </remarks>
public static class QeDependencyGraphReader
{
    private const int TagDataSource = 2, TagTable = 3, TagField = 4, TagLink = 10;
    private const int SchemaDataSource = 0x0902, SchemaTable = 0x0905, SchemaLink = 0x0901;
    private const int MaxDepth = 6;

    public static QeDependencyGraph Read(byte[] payload)
    {
        var all = Flatten(Scan(payload, 0)).ToList();

        string? driver = null, dbType = null, dbName = null;
        var source = all.FirstOrDefault(r => r.Tag == TagDataSource && r.Schema == SchemaDataSource);
        if (source is not null)
        {
            // An int32, then the driver, its database type, and the database.
            int p = 4;
            driver = ReadString(source.Data, ref p);
            dbType = ReadString(source.Data, ref p);
            dbName = ReadString(source.Data, ref p);
        }

        var tables = new List<QeTable>();
        foreach (var rec in all.Where(r => r.Tag == TagTable && r.Schema == SchemaTable))
        {
            var table = ReadTable(rec);
            if (table is not null) tables.Add(table);
        }

        var links = new List<QeLink>();
        foreach (var rec in all.Where(r => r.Tag == TagLink && r.Schema == SchemaLink && r.Data.Length >= 12))
        {
            var d = rec.Data;
            int Int(int i) => d.Length >= (i + 1) * 4 ? BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(i * 4)) : 0;
            links.Add(new QeLink
            {
                Id = Int(0),
                SourceFieldId = Int(1),
                TargetFieldId = Int(2),
                OperatorCode = Int(3),
                JoinTypeCode = Int(4),
                Flag = Int(5)
            });
        }
        // The runtime lists links by id; the stream does not keep them in that order.
        links.Sort((a, b) => a.Id.CompareTo(b.Id));

        return new QeDependencyGraph
        {
            DriverName = driver,
            DatabaseType = dbType,
            DatabaseName = dbName,
            Tables = tables,
            Links = links
        };
    }

    // id, name, int32, byte, qualified name, int32 count, that many qualifier parts, int32,
    // alias, int32, field count, then the field records.
    private static QeTable? ReadTable(Record rec)
    {
        var d = rec.Data;
        if (d.Length < 8) return null;
        int p = 0;
        int id = ReadInt(d, ref p);
        string? name = ReadString(d, ref p);
        if (name is null) return null;
        p += 5;
        string? qualified = ReadString(d, ref p);
        if (qualified is null) return null;
        int partCount = ReadInt(d, ref p);
        var parts = new List<string>();
        for (int i = 0; i < partCount && i < 8; i++)
        {
            string? part = ReadString(d, ref p);
            if (part is null) break;
            parts.Add(part);
        }
        p += 4;
        string? alias = ReadString(d, ref p);

        var fields = new List<QeField>();
        // Fields carry schema 0x0905 or 0x0906 by the version that saved them; the layout read
        // here is the same in both.
        foreach (var child in rec.Children.Where(c => c.Tag == TagField && (c.Schema & 0xFF00) == 0x0900))
        {
            int q = 0;
            int fieldId = ReadInt(child.Data, ref q);
            string? fieldName = ReadString(child.Data, ref q);
            if (fieldName is null) continue;
            q += 5;
            int valueType = ReadInt(child.Data, ref q);
            int length = ReadInt(child.Data, ref q);
            fields.Add(new QeField { Id = fieldId, Name = fieldName, ValueType = valueType, Length = length });
        }

        return new QeTable
        {
            Id = id,
            Name = name,
            QualifiedName = qualified,
            QualifierParts = parts,
            Alias = string.IsNullOrEmpty(alias) ? name : alias,
            Fields = fields
        };
    }

    private static int ReadInt(byte[] d, ref int p)
    {
        if (p + 4 > d.Length) { p = d.Length; return 0; }
        int v = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p));
        p += 4;
        return v;
    }

    // A big-endian length that counts the null terminator, then the bytes. A zero length is
    // an absent string.
    private static string? ReadString(byte[] d, ref int p)
    {
        if (p + 4 > d.Length) return null;
        int n = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p));
        if (n < 0 || n > 65536 || p + 4 + n > d.Length) return null;
        p += 4;
        string s = n <= 1 ? string.Empty : Encoding.Latin1.GetString(d, p, n - 1);
        p += n;
        return s;
    }

    private sealed class Record
    {
        public int Tag;
        public int Schema;
        public byte[] Data = [];
        public List<Record> Children = [];
    }

    private static IEnumerable<Record> Flatten(IEnumerable<Record> records) =>
        records.SelectMany(r => new[] { r }.Concat(Flatten(r.Children)));

    // Finds the records in a decoded body by their headers, taking a header only when its
    // declared length fits in what is left; anything else is a field of the enclosing record
    // and is stepped over.
    private static List<Record> Scan(byte[] b, int depth)
    {
        var records = new List<Record>();
        int pos = 0;
        while (pos < b.Length)
        {
            if (TryHeader(b, pos, out int tag, out int schema, out int len, out int headerLen))
            {
                var data = new byte[len];
                for (int i = 0; i < len; i++)
                    data[i] = (byte)(b[pos + headerLen + i] ^ tag);
                var rec = new Record { Tag = tag, Schema = schema, Data = data };
                if (depth < MaxDepth && len >= 8)
                    rec.Children = Scan(data, depth + 1);
                records.Add(rec);
                pos += headerLen + len;
            }
            else
            {
                pos++;
            }
        }
        return records;
    }

    // The graph's records all use a four-byte length, the simple-encryption bit, and a
    // one-byte tag: control byte 0xF8 with a schema, 0xD8 without.
    private static bool TryHeader(byte[] b, int pos, out int tag, out int schema, out int len, out int headerLen)
    {
        tag = schema = len = headerLen = 0;
        if (pos + 2 > b.Length) return false;
        byte control = b[pos];
        if ((control & 0xC0) != 0xC0 || (control & 0x08) == 0 || (control & 0x07) != 0) return false;
        bool hasSchema = (control & 0x20) != 0;
        int cur = pos + 1;
        tag = b[cur++];
        if (hasSchema)
        {
            if (cur + 2 > b.Length) return false;
            schema = (b[cur] << 8) | b[cur + 1];
            cur += 2;
        }
        if (cur + 4 > b.Length) return false;
        len = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(cur));
        cur += 4;
        headerLen = cur - pos;
        return len >= 0 && pos + headerLen + len <= b.Length;
    }
}
