namespace D3Parking.Application.Parking;

/// <summary>Outcome of a parking command: success, or a set of human-readable error messages.</summary>
public sealed record ParkingResult
{
    public bool Succeeded { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>The booking succeeded with the caller's approved compensation instead of credits.</summary>
    public bool AutomaticCompensationApplied { get; init; }

    /// <summary>The booking atomically shared the caller's assigned resident spot.</summary>
    public bool ResidentSpotAutomaticallyReleased { get; init; }

    /// <summary>Cancelling or releasing an alternative booking restored a still-free resident spot.</summary>
    public bool ResidentSpotAutomaticallyReturned { get; init; }

    public static readonly ParkingResult Success = new() { Succeeded = true };

    public static ParkingResult Failure(params string[] errors) => new() { Succeeded = false, Errors = errors };
}
