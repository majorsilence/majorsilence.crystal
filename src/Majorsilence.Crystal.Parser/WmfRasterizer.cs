using System.Runtime.Versioning;

namespace Majorsilence.Crystal.Parser;

/// <summary>
/// Rasterizes Windows Metafile (WMF) images to PNG so they can be embedded in
/// RDL, which has no WMF MIME type. Windows-only (GDI+ via System.Drawing);
/// on other platforms <see cref="TryRasterizeToPng"/> always returns null.
/// </summary>
public static class WmfRasterizer
{
    /// <summary>Placeable (Aldus) WMF header or standard WMF header.</summary>
    public static bool IsWmf(byte[] data) => data is
        [0xD7, 0xCD, 0xC6, 0x9A, ..] or [0x01, 0x00, 0x09, 0x00, ..];

    /// <summary>
    /// Locate a WMF or EMF payload at or near the start of <paramref name="data"/> —
    /// OLE presentation streams (\x02OlePres000) and some CONTENTS streams prefix the
    /// metafile with a small header. Returns the metafile offset within the first
    /// 64 bytes, or -1.
    /// </summary>
    public static int FindMetafileOffset(byte[] data)
    {
        int limit = Math.Min(64, data.Length - 4);
        for (int i = 0; i <= limit; i++)
        {
            // WMF: placeable (Aldus) or standard header
            if (data[i] == 0xD7 && data[i + 1] == 0xCD && data[i + 2] == 0xC6 && data[i + 3] == 0x9A)
                return i;
            if (data[i] == 0x01 && data[i + 1] == 0x00 && data[i + 2] == 0x09 && data[i + 3] == 0x00)
                return i;
            // EMF: EMR_HEADER (iType=1) with " EMF" signature at header offset 40
            if (data[i] == 0x01 && data[i + 1] == 0x00 && data[i + 2] == 0x00 && data[i + 3] == 0x00 &&
                i + 44 <= data.Length - 4 &&
                data[i + 40] == 0x20 && data[i + 41] == 0x45 && data[i + 42] == 0x4D && data[i + 43] == 0x46)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Render the WMF starting at <paramref name="offset"/> onto a white bitmap and
    /// encode as PNG. Returns null off-Windows or when GDI+ cannot load the metafile.
    /// </summary>
    public static byte[]? TryRasterizeToPng(byte[] data, int offset = 0)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return RasterizeWindows(data, offset);
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] RasterizeWindows(byte[] data, int offset)
    {
        using var input = new MemoryStream(data, offset, data.Length - offset);
        using var metafile = new System.Drawing.Imaging.Metafile(input);

        // Cap the raster size — metafile logical bounds can be in far-out units
        var unit = System.Drawing.GraphicsUnit.Pixel;
        var bounds = metafile.GetBounds(ref unit);
        int width = Math.Clamp((int)Math.Ceiling(bounds.Width), 1, 2000);
        int height = Math.Clamp((int)Math.Ceiling(bounds.Height), 1, 2000);

        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.White);
            g.DrawImage(metafile, 0, 0, width, height);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return output.ToArray();
    }
}
