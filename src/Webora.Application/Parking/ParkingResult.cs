namespace Webora.Application.Parking;

/// <summary>Outcome of a parking command: success, or a set of human-readable error messages.</summary>
public sealed record ParkingResult
{
    public bool Succeeded { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static readonly ParkingResult Success = new() { Succeeded = true };

    public static ParkingResult Failure(params string[] errors) => new() { Succeeded = false, Errors = errors };
}
