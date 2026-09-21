namespace D3Parking.Application.Parking.Maps;

/// <summary>
/// Decides an uploaded image's type from its own bytes rather than from what the browser claimed.
/// </summary>
/// <remarks>
/// The declared content type is attacker-controlled — it is whatever the multipart part says — and
/// whatever is stored here is echoed back by the endpoint that streams the underlay. Trusting it
/// would let an upload of HTML come back as HTML from the application's own origin, which is a
/// stored cross-site scripting hole rather than a bad picture.
///
/// SVG is deliberately not on the list even though it is an image and would be the nicest possible
/// underlay: it is a document format that carries script, and serving one same-origin is the same
/// hole by a more respectable name. Raster only.
/// </remarks>
public static class ImageContentType
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Webp = "image/webp";

    /// <summary>What the file picker should offer. Mirrors what <see cref="Detect"/> will accept.</summary>
    public const string AcceptAttribute = $"{Png},{Jpeg},{Webp}";

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The content type the bytes actually are, or null when they are not one of the raster formats
    /// this accepts. Never returns a type the caller supplied.
    /// </summary>
    public static string? Detect(ReadOnlySpan<byte> content)
    {
        if (content.Length >= PngMagic.Length && content[..PngMagic.Length].SequenceEqual(PngMagic))
        {
            return Png;
        }

        // JPEG: SOI marker, then any of the several frame markers that follow it.
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return Jpeg;
        }

        // WebP is a RIFF container: "RIFF" <4-byte size> "WEBP".
        if (content.Length >= 12
            && content[..4].SequenceEqual("RIFF"u8)
            && content[8..12].SequenceEqual("WEBP"u8))
        {
            return Webp;
        }

        return null;
    }
}
