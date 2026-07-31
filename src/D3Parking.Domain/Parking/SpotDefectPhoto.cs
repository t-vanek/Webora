using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// An optional picture of what is wrong with the lot.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="MismatchPhoto"/>. That one is evidence: it decides whether a
/// voucher is worth real credits, which is why its bytes are fingerprinted under a unique index so
/// one picture can back exactly one report ever. This one proves nothing and unlocks nothing — it
/// only saves somebody a walk to see which bay the pothole is in. Reusing the fingerprinted table
/// would either weaken the rule that makes it worth having, or refuse a second photo of the same
/// unrepaired light for no reason anyone could explain.
/// </remarks>
public class SpotDefectPhoto : Entity
{
    public Guid DefectId { get; private set; }

    public string ContentType { get; private set; } = string.Empty;

    public byte[] Content { get; private set; } = [];

    public int SizeBytes { get; private set; }

    public DateTimeOffset UploadedAtUtc { get; private set; }

    private SpotDefectPhoto() { }

    public SpotDefectPhoto(Guid defectId, string contentType, byte[] content, DateTimeOffset uploadedAtUtc)
    {
        DefectId = defectId;
        ContentType = contentType;
        Content = content;
        SizeBytes = content.Length;
        UploadedAtUtc = uploadedAtUtc;
    }
}
