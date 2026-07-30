using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// The reporter's photo of the blocked spot — the proof a spot manager judges before approving
/// the apology voucher. The SHA-256 fingerprint of the bytes is stored under a unique index, so
/// one photo can back exactly one report ever: re-submitting the same file (today or months
/// later, by anyone) is rejected instead of minting another voucher.
/// </summary>
public class MismatchPhoto : Entity
{
    public Guid MismatchId { get; private set; }

    public string ContentType { get; private set; } = string.Empty;

    public byte[] Content { get; private set; } = [];

    /// <summary>SHA-256 of <see cref="Content"/>; unique — the anti-reuse fingerprint.</summary>
    public byte[] ContentHash { get; private set; } = [];

    public int SizeBytes { get; private set; }

    public DateTimeOffset UploadedAtUtc { get; private set; }

    private MismatchPhoto() { }

    public MismatchPhoto(Guid mismatchId, string contentType, byte[] content, byte[] contentHash, DateTimeOffset uploadedAtUtc)
    {
        MismatchId = mismatchId;
        ContentType = contentType;
        Content = content;
        ContentHash = contentHash;
        SizeBytes = content.Length;
        UploadedAtUtc = uploadedAtUtc;
    }
}
