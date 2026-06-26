using System.IO.Compression;
using System.Security.Cryptography;

namespace Majorsilence.Crystal.Parser.Decryption;

/// <summary>
/// Decrypts and decompresses the Crystal Reports Contents OLE stream.
///
/// Pipeline (from decompiled Crystal Reports Java SDK StreamBuilder.java):
///   1. TSLV stream header (10 bytes) + record data (24 bytes) = 34 bytes consumed
///      - Simple rolling XOR (j=0xFF) applied during header read to extract per-file AES IV
///   2. AES-128-CFB128 decryption using Crystal's Rijndael variant (little-endian word ordering)
///      - KEY = Rijndael.goto (hardcoded static constant in CrystalReportsRuntime.jar)
///      - IV  = bytes 16..31 of Contents stream XOR'd with 0xFF (from stream header record)
///   3. ZLib inflate (standard zlib format with header)
///   Output is TSLV record stream containing the report definition.
/// </summary>
public static class ContentDecryptor
{
    // Rijndael.goto from CrystalReportsRuntime.jar — hardcoded static AES key for all CR files
    private static readonly byte[] CrystalAesKey =
    [
        0x11, 0xDD, 0x18, 0x96, 0xBD, 0x4A, 0x15, 0xCD,
        0xBF, 0xF2, 0x54, 0x35, 0x03, 0xE6, 0x76, 0x0F
    ];

    private const int TslvHeaderLength = 10;   // TSLV stream header record header
    private const int TslvDataLength = 24;     // TSLV stream header record data
    private const int CiphertextOffset = TslvHeaderLength + TslvDataLength;  // = 34
    private const int PerFileKeyOffset = 16;   // byte offset within Contents stream
    private const int PerFileKeyLength = 16;   // AES IV length

    public static bool IsEncrypted(ReadOnlySpan<byte> contents)
    {
        // TSLV stream header tag = 0xFFFF indicates encryption may be present;
        // bytes 10-11 XOR 0xFF = 0x00 0x01 means isEncrypted=true in the record data.
        return contents.Length > 11
            && contents[0] == 0xFC
            && contents[2] == 0xFF && contents[3] == 0xFF   // tag = 0xFFFF
            && (contents[10] ^ 0xFF) == 0x00                // isEncrypted high byte
            && (contents[11] ^ 0xFF) == 0x01;               // isEncrypted low byte (big-endian 1 = true)
    }

    /// <summary>
    /// Decrypts and decompresses the Contents stream.
    /// Returns the raw TSLV payload starting with the first TSLV record after the stream header.
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> contents)
    {
        if (!IsEncrypted(contents))
            return contents.ToArray();

        // Extract per-file AES IV: bytes 16..31 of the Contents stream, XOR'd with 0xFF
        // (the TSLV stream header data is read through the simple-XOR stream with j=0xFF)
        byte[] iv = new byte[PerFileKeyLength];
        for (int i = 0; i < PerFileKeyLength; i++)
            iv[i] = (byte)(contents[PerFileKeyOffset + i] ^ 0xFF);

        byte[] ciphertext = contents[CiphertextOffset..].ToArray();
        byte[] decrypted = CrystalCfb128Decrypt(CrystalAesKey, iv, ciphertext);

        return ZlibInflate(decrypted);
    }

    // Crystal Reports Rijndael is AES-128 with little-endian byte ordering within each 4-byte word.
    // b.a(input, output) = rev32(AES_Encrypt(rev32(key), rev32(input)))
    // where rev32 reverses bytes within each 4-byte group.
    // CFB-128: keystream = Crystal_b(feedback), feedback starts at IV then advances by ciphertext block.
    private static byte[] CrystalCfb128Decrypt(byte[] key, byte[] iv, byte[] ct)
    {
        byte[] crystalKey = Rev32(key);
        using var aes = Aes.Create();
        aes.Key = crystalKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        byte[] result = new byte[ct.Length];
        byte[] feedback = (byte[])iv.Clone();
        byte[] ks = new byte[16];
        using var enc = aes.CreateEncryptor();

        for (int pos = 0; pos < ct.Length; pos += 16)
        {
            byte[] fbRev = Rev32(feedback);
            enc.TransformBlock(fbRev, 0, 16, ks, 0);
            ks = Rev32(ks);

            int blen = Math.Min(16, ct.Length - pos);
            for (int i = 0; i < blen; i++)
                result[pos + i] = (byte)(ct[pos + i] ^ ks[i]);

            Array.Copy(ct, pos, feedback, 0, blen);
        }
        return result;
    }

    private static byte[] Rev32(byte[] data)
    {
        var result = (byte[])data.Clone();
        for (int i = 0; i + 3 < result.Length; i += 4)
        {
            (result[i], result[i + 3]) = (result[i + 3], result[i]);
            (result[i + 1], result[i + 2]) = (result[i + 2], result[i + 1]);
        }
        return result;
    }

    private static byte[] ZlibInflate(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var result = new MemoryStream();
        zlib.CopyTo(result);
        return result.ToArray();
    }
}
