namespace D3Parking.Domain.Parking.Incentives;

/// <summary>Recognitions earned for behaviour that improves how well the lot is used.</summary>
public enum ParkingBadge
{
    /// <summary>Repeatedly freed reserved spots for colleagues.</summary>
    ConsiderateColleague,

    /// <summary>Regularly parks outside the high-demand peak.</summary>
    OffPeakChampion,

    /// <summary>A long run of used reservations with no no-shows.</summary>
    ReliableParker,

    /// <summary>Reached a major points milestone.</summary>
    CenturyClub,

    /// <summary>Highly trusted in the sharing network (top trust-graph standing).</summary>
    Trusted,
}
