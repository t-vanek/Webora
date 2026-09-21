namespace D3Parking.Domain.Parking;

/// <summary>The physical category of a parking spot, used for matching and filtering.</summary>
public enum ParkingSpotType
{
    Standard,
    Disabled,
    ElectricCharging,
    Visitor,
    Motorcycle,
}
