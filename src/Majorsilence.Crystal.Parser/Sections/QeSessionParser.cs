using System.Buffers.Binary;
using System.Text;
using Majorsilence.Crystal.Parser.Decryption;

namespace Majorsilence.Crystal.Parser.Sections;

/// <summary>
/// The parsed <c>QESession</c> stream: the QueryEngine's saved session. When
/// <see cref="DecryptionSucceeded"/> is true, <see cref="DecryptedPayload"/> is the inflated
/// dependency graph and <see cref="DependencyGraph"/> is what was read from it: the data
/// source, its tables and fields, and the links between the tables.
/// </summary>
public class QeSessionRecord
{
    public bool IsValid { get; init; }
    public bool IsEncrypted { get; init; }
    public bool DecryptionSucceeded { get; init; }
    public byte Version { get; init; }
    public uint Flags { get; init; }
    public int HeaderLength { get; init; }
    public int PayloadLength { get; init; }

    /// <summary>
    /// The stream from the ciphertext offset onward: the inflated dependency graph when
    /// decryption succeeded, otherwise the (still encrypted) raw tail for diagnostics.
    /// </summary>
    public byte[]? RawPayload { get; init; }

    /// <summary>
    /// The decrypted and inflated QESession payload, a TSLV stream carrying the data source,
    /// tables and links, or null when the stream is absent or did not decrypt.
    /// </summary>
    public byte[]? DecryptedPayload { get; init; }

    /// <summary>The dependency graph read from <see cref="DecryptedPayload"/>, or null when there is none.</summary>
    public QeDependencyGraph? DependencyGraph { get; init; }

    /// <summary>
    /// The names the graph carries (driver, database, tables, fields) when it decoded; else
    /// the readable runs of the raw tail, for diagnostics.
    /// </summary>
    public List<string> ExtractedStrings { get; init; } = new();
}

/// <summary>
/// Parses the <c>QESession</c> stream. The 22-byte cleartext header carries the QENG magic,
/// version and flags; a per-file IV follows, and the remainder is AES-128-CFB128 ciphertext
/// (Crystal "Rijndael variant") that inflates to a TSLV dependency graph, which
/// <see cref="QeDependencyGraphReader"/> then decodes.
/// </summary>
/// <remarks>
/// Historically this parser only read ASCII strings out of the <em>encrypted</em> tail, so it
/// never surfaced the dependency graph. It now decrypts the stream with
/// <see cref="QeSessionDecryptor"/> (a fixed key distinct from the Contents stream) and
/// reads the tables and links out of the result.
/// </remarks>
public class QeSessionParser
{
    public QeSessionRecord Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 ||
            data[0] != (byte)'Q' || data[1] != (byte)'E' ||
            data[2] != (byte)'N' || data[3] != (byte)'G')
        {
            return new QeSessionRecord { IsValid = false, HeaderLength = 16 };
        }

        var version = data[4];
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);

        bool encrypted = QeSessionDecryptor.IsEncrypted(data);
        byte[]? decrypted = null;
        bool decryptionSucceeded = false;
        if (encrypted)
        {
            try
            {
                decrypted = QeSessionDecryptor.Decrypt(data);
                decryptionSucceeded = true;
            }
            catch
            {
                decryptionSucceeded = false;
            }
        }

        QeDependencyGraph? graph = null;
        if (decrypted is not null)
        {
            try
            {
                graph = QeDependencyGraphReader.Read(decrypted);
            }
            catch
            {
                graph = null;
            }
        }

        // The meaningful payload is the graph when decryption worked, else the raw tail.
        var source = decrypted
            ?? (data.Length > QeSessionDecryptor.CiphertextOffset
                ? data[QeSessionDecryptor.CiphertextOffset..].ToArray()
                : data[16..].ToArray());

        return new QeSessionRecord
        {
            IsValid = true,
            IsEncrypted = encrypted,
            DecryptionSucceeded = decryptionSucceeded,
            Version = version,
            Flags = flags,
            HeaderLength = QeSessionDecryptor.HeaderLength,
            PayloadLength = source.Length,
            RawPayload = source,
            DecryptedPayload = decrypted,
            DependencyGraph = graph,
            ExtractedStrings = graph is not null ? GraphStrings(graph) : ExtractStrings(source)
        };
    }

    private static List<string> GraphStrings(QeDependencyGraph graph)
    {
        var result = new List<string>();
        if (!string.IsNullOrEmpty(graph.DriverName)) result.Add(graph.DriverName);
        if (!string.IsNullOrEmpty(graph.DatabaseType)) result.Add(graph.DatabaseType);
        if (!string.IsNullOrEmpty(graph.DatabaseName)) result.Add(graph.DatabaseName);
        foreach (var table in graph.Tables)
        {
            result.Add(table.Alias == table.Name ? table.QualifiedName : $"{table.QualifiedName} {table.Alias}");
            foreach (var field in table.Fields)
                result.Add($"{table.Alias}.{field.Name}");
        }
        return result;
    }

    private static List<string> ExtractStrings(ReadOnlySpan<byte> data)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var b in data)
        {
            if (b >= 32 && b < 127) current.Append((char)b);
            else
            {
                if (current.Length >= 4) result.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length >= 4) result.Add(current.ToString());
        return result;
    }
}
