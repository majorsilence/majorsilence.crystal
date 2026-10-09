using System.Security.Cryptography;

namespace Majorsilence.Crystal.Parser.Decryption;

/// <summary>
/// Decrypts the <c>QESession</c> stream — the QueryEngine's saved session, which holds the
/// data source and its table links (the information RptEngine needs to join multi-table
/// reports, see issue #36). It is encrypted with AES-128 in CFB-128 mode using a fixed
/// key, but a <em>different</em> key from <see cref="ContentDecryptor"/> (the Contents
/// stream), with a per-file initialization vector stored in cleartext just after the
/// QENG header.
/// </summary>
/// <remarks>
/// Framing (verified against the full corpus — every <c>QESession</c> stream in the public
/// and third-party report sets decrypts and inflates to a valid TSLV dependency graph under
/// exactly this):
/// <list type="bullet">
///   <item>bytes 0..3   : <c>QENG</c> magic</item>
///   <item>bytes 4..21  : version / flags (cleartext)</item>
///   <item>bytes 22..37 : per-file IV (cleartext, raw — <em>not</em> XOR 0xFF like Contents)</item>
///   <item>bytes 38..end: AES-128-CFB128 ciphertext (Crystal "Rijndael variant"), which
///     inflates (zlib) to a TSLV stream</item>
/// </list>
/// The key is the same 16 bytes for every file.
/// </remarks>
public static class QeSessionDecryptor
{
    /// <summary>Cleartext header length: QENG + version + flags.</summary>
    public const int HeaderLength = 22;

    /// <summary>Offset and length of the per-file initialization vector.</summary>
    public const int IvOffset = HeaderLength;
    public const int IvLength = 16;

    /// <summary>Offset where the ciphertext begins.</summary>
    public const int CiphertextOffset = IvOffset + IvLength; // 38

    /// <summary>Fixed AES-128 key for the QESession stream (distinct from the Contents key).</summary>
    private static readonly byte[] QeSessionAesKey =
    {
        0x2A, 0xBC, 0xDF, 0x1F, 0xD6, 0xF8, 0xAC, 0x6C,
        0x0A, 0x50, 0x0C, 0x65, 0x20, 0x47, 0xBA, 0xDC
    };

    public static bool IsEncrypted(ReadOnlySpan<byte> data)
    {
        if (data.Length < CiphertextOffset) return false;
        return data[0] == (byte)'Q' && data[1] == (byte)'E'
            && data[2] == (byte)'N' && data[3] == (byte)'G';
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        if (!IsEncrypted(data))
            throw new CryptographicException("Not a QESession stream (missing QENG header or too short).");
        var iv = data.Slice(IvOffset, IvLength).ToArray();
        var ciphertext = data.Slice(CiphertextOffset).ToArray();
        var plain = ContentDecryptor.CrystalCfb128Decrypt(QeSessionAesKey, iv, ciphertext);
        return ContentDecryptor.ZlibInflate(plain);
    }
}
