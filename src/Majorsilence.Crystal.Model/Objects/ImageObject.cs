namespace Majorsilence.Crystal.Model.Objects;

public enum ImageSourceKind
{
    /// <summary>Static picture embedded in the report file (OLE "Embedding N" storage).</summary>
    Embedded,

    /// <summary>Database blob field rendered as an image at runtime (barcodes, photos).</summary>
    Database,
}

public sealed class ImageObject : ReportObject
{
    public ImageSourceKind Source { get; init; }

    /// <summary>Column name of the blob field, for <see cref="ImageSourceKind.Database"/>.</summary>
    public string FieldName { get; init; } = string.Empty;

    /// <summary>Index N of the "Embedding N" OLE storage, for <see cref="ImageSourceKind.Embedded"/>.</summary>
    public int EmbeddingIndex { get; init; }

    /// <summary>Raw image bytes resolved from the OLE storage; null if unresolved.</summary>
    public byte[]? ImageData { get; set; }

    /// <summary>MIME type sniffed from <see cref="ImageData"/> (e.g. "image/bmp").</summary>
    public string? MimeType { get; set; }
}
