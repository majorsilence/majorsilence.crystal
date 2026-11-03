using System.Runtime.InteropServices;
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
    /// <paramref name="presentation"/> is true for an OLE object's cached presentation
    /// (its \x02OlePres000 stream) rather than a picture's own CONTENTS.
    /// </summary>
    public static byte[]? TryRasterizeToPng(byte[] data, int offset = 0, bool presentation = false)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return RasterizeWindows(data, offset, presentation);
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] RasterizeWindows(byte[] data, int offset, bool presentation)
    {
        // A WMF may carry an enhanced-metafile copy of the same picture in comment records.
        // GDI+ draws that copy in its own way, leaving out the raster operations a picture
        // masks a bitmap with, so a logo laid over the page in two passes came out black.
        // Crystal draws a picture's copy with GDI, and an OLE object's presentation from its
        // WMF records, which GDI plays and the copy's comments do not reach.
        var wmf = WmfRecords.Read(data, offset);
        if (presentation && wmf is { WindowWidth: > 0, WindowHeight: > 0 })
            return PlayPresentation(data, offset, wmf);
        if (!presentation && wmf?.EnhancedCopy is { } copy)
            return PlayEnhancedCopy(copy);

        using var input = new MemoryStream(data, offset, data.Length - offset);
        using var metafile = new System.Drawing.Imaging.Metafile(input);
        var (width, height) = RasterSize(metafile);

        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.White);
            g.DrawImage(metafile, 0, 0, width, height);
        }
        return EncodePng(bitmap);
    }

    // Cap the raster size — metafile logical bounds can be in far-out units. Both sides
    // shrink by the same factor: a paragraph kept as a picture is far wider than it is
    // tall, and capping its width alone squeezed every line into a quarter of its width.
    [SupportedOSPlatform("windows")]
    private static (int Width, int Height) RasterSize(System.Drawing.Imaging.Metafile metafile)
    {
        var unit = System.Drawing.GraphicsUnit.Pixel;
        var bounds = metafile.GetBounds(ref unit);
        double scale = Math.Min(1.0, RasterCap / Math.Max(bounds.Width, bounds.Height));
        return (Math.Clamp((int)Math.Ceiling(bounds.Width * scale), 1, RasterCap),
            Math.Clamp((int)Math.Ceiling(bounds.Height * scale), 1, RasterCap));
    }

    private const int RasterCap = 2000;

    // An OLE presentation is played as a metafile picture: its records' window is mapped onto
    // the whole picture, as GDI maps it, and a pen one unit wide is one unit of the picture
    // wide. The window is in coarse units (often points), so the raster is drawn at the cap
    // on its longer side rather than one pixel a unit.
    [SupportedOSPlatform("windows")]
    private static byte[] PlayPresentation(byte[] data, int offset, WmfRecords wmf)
    {
        double scale = (double)RasterCap / Math.Max(wmf.WindowWidth, wmf.WindowHeight);
        int width = Math.Clamp((int)Math.Round(wmf.WindowWidth * scale), 1, RasterCap);
        int height = Math.Clamp((int)Math.Round(wmf.WindowHeight * scale), 1, RasterCap);
        byte[] records = data[(offset + wmf.HeaderLength)..];
        return PlayWithGdi(width, height, hdc =>
        {
            nint handle = Gdi.SetMetaFileBitsEx((uint)records.Length, records);
            if (handle == 0) throw new InvalidDataException("GDI could not load the metafile.");
            try
            {
                Gdi.SetMapMode(hdc, Gdi.MmAnisotropic);
                Gdi.SetWindowOrgEx(hdc, wmf.WindowX, wmf.WindowY, 0);
                Gdi.SetWindowExtEx(hdc, wmf.WindowWidth, wmf.WindowHeight, 0);
                Gdi.SetViewportExtEx(hdc, width, height, 0);
                Gdi.PlayMetaFile(hdc, handle);
            }
            finally
            {
                Gdi.DeleteMetaFile(handle);
            }
        });
    }

    [SupportedOSPlatform("windows")]
    private static byte[] PlayEnhancedCopy(byte[] emf)
    {
        int width, height;
        using (var metafile = new System.Drawing.Imaging.Metafile(new MemoryStream(emf)))
            (width, height) = RasterSize(metafile);
        return PlayWithGdi(width, height, hdc =>
        {
            nint handle = Gdi.SetEnhMetaFileBits((uint)emf.Length, emf);
            if (handle == 0) throw new InvalidDataException("GDI could not load the metafile.");
            try
            {
                var frame = new Gdi.Rect { Right = width, Bottom = height };
                Gdi.PlayEnhMetaFile(hdc, handle, ref frame);
            }
            finally
            {
                Gdi.DeleteEnhMetaFile(handle);
            }
        });
    }

    [SupportedOSPlatform("windows")]
    private static byte[] PlayWithGdi(int width, int height, Action<nint> play)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.White);
            nint hdc = g.GetHdc();
            try
            {
                play(hdc);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        return EncodePng(bitmap);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] EncodePng(System.Drawing.Bitmap bitmap)
    {
        using var output = new MemoryStream();
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return output.ToArray();
    }

    /// <summary>
    /// The parts of a WMF the rasterizer chooses between: the window its records set, and the
    /// enhanced-metafile copy its "WMFC" comment records carry, joined from their chunks.
    /// </summary>
    internal sealed class WmfRecords
    {
        /// <summary>The placeable header's length (22) or 0, before the standard header.</summary>
        public int HeaderLength { get; private init; }

        public short WindowX { get; private set; }
        public short WindowY { get; private set; }
        public short WindowWidth { get; private set; }
        public short WindowHeight { get; private set; }

        /// <summary>The enhanced-metafile copy, or null when there is none or it is incomplete.</summary>
        public byte[]? EnhancedCopy { get; private set; }

        /// <summary>The WMF at <paramref name="offset"/>, or null for an EMF or a malformed WMF.</summary>
        public static WmfRecords? Read(byte[] data, int offset)
        {
            int header = data.AsSpan(offset) is [0xD7, 0xCD, 0xC6, 0x9A, ..] ? 22 : 0;
            int p = offset + header;
            if (data.Length < p + 18 || BitConverter.ToUInt16(data, p) is not (1 or 2)
                || BitConverter.ToUInt16(data, p + 2) != 9)
                return null;

            var wmf = new WmfRecords { HeaderLength = header };
            using var copy = new MemoryStream();
            long copySize = -1;
            bool windowSet = false, extentSet = false;
            for (p += 18; p + 6 <= data.Length;)
            {
                long words = BitConverter.ToUInt32(data, p);
                if (words < 3 || p + words * 2 > data.Length) return null;
                int function = BitConverter.ToUInt16(data, p + 4);
                switch (function)
                {
                    // META_ESCAPE MFCOMMENT "WMFC": one chunk of the copy, its size at +32 and
                    // the whole copy's at +40, the chunk's bytes from +44.
                    case 0x0626 when words >= 22 && BitConverter.ToUInt16(data, p + 6) == 15
                                     && BitConverter.ToUInt32(data, p + 10) == 0x43464D57:
                        int chunk = BitConverter.ToInt32(data, p + 32);
                        if (chunk < 0 || 44 + chunk > words * 2) return null;
                        if (copySize < 0) copySize = BitConverter.ToUInt32(data, p + 40);
                        copy.Write(data, p + 44, chunk);
                        break;
                    case 0x020B when words >= 5 && !windowSet:   // META_SETWINDOWORG y, x
                        wmf.WindowY = BitConverter.ToInt16(data, p + 6);
                        wmf.WindowX = BitConverter.ToInt16(data, p + 8);
                        windowSet = true;
                        break;
                    case 0x020C when words >= 5 && !extentSet:   // META_SETWINDOWEXT y, x
                        wmf.WindowHeight = BitConverter.ToInt16(data, p + 6);
                        wmf.WindowWidth = BitConverter.ToInt16(data, p + 8);
                        extentSet = true;
                        break;
                }
                p += (int)words * 2;
                if (function == 0) break;
            }
            if (copySize > 0 && copy.Length == copySize) wmf.EnhancedCopy = copy.ToArray();
            return wmf;
        }
    }

    private static class Gdi
    {
        public const int MmAnisotropic = 8;

        public struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("gdi32.dll")] public static extern nint SetMetaFileBitsEx(uint size, byte[] data);
        [DllImport("gdi32.dll")] public static extern bool PlayMetaFile(nint hdc, nint metafile);
        [DllImport("gdi32.dll")] public static extern bool DeleteMetaFile(nint metafile);
        [DllImport("gdi32.dll")] public static extern nint SetEnhMetaFileBits(uint size, byte[] data);
        [DllImport("gdi32.dll")] public static extern bool PlayEnhMetaFile(nint hdc, nint metafile, ref Rect frame);
        [DllImport("gdi32.dll")] public static extern bool DeleteEnhMetaFile(nint metafile);
        [DllImport("gdi32.dll")] public static extern int SetMapMode(nint hdc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetWindowOrgEx(nint hdc, int x, int y, nint previous);
        [DllImport("gdi32.dll")] public static extern bool SetWindowExtEx(nint hdc, int x, int y, nint previous);
        [DllImport("gdi32.dll")] public static extern bool SetViewportExtEx(nint hdc, int x, int y, nint previous);
    }
}
