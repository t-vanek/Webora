using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

public sealed record ParkingSpotDto(
    Guid Id,
    string Code,
    ParkingSpotType Type,
    bool IsActive,
    string? Notes,
    Guid? OwnerId,
    string? OwnerName,
    int ResidentCapacity = 1,
    IReadOnlyList<ParkingSpotResidentDto>? Residents = null)
{
    public IReadOnlyList<ParkingSpotResidentDto> ResidentList => Residents ?? [];

    public int ResidentCount => ResidentList.Count > 0 ? ResidentList.Count : OwnerId is null ? 0 : 1;
}

public sealed record ParkingSpotResidentDto(Guid MembershipId, Guid UserId, string Name, bool IsPrimary);

/// <summary>An active account that does not currently hold another resident spot.</summary>
public sealed record ResidentCandidateDto(Guid UserId, string Email, string? DisplayName)
{
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Email : DisplayName;
}

public enum ParkingSpotStateFilter
{
    All,
    Active,
    Inactive,
}

public enum ParkingSpotOwnershipFilter
{
    All,
    Resident,
    Shared,
}

public sealed record ParkingSpotListQuery(
    string? Search = null,
    ParkingSpotStateFilter State = ParkingSpotStateFilter.All,
    ParkingSpotType? Type = null,
    ParkingSpotOwnershipFilter Ownership = ParkingSpotOwnershipFilter.All);

public sealed record ParkingSpotDirectorySummary(
    int Total,
    int Active,
    int Resident,
    int Shared,
    int Visitor);
