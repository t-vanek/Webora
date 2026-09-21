namespace D3Parking.Domain.Parking;

/// <summary>How often the shared planning wallet is topped up to its configured limit.</summary>
public enum BudgetRenewalPeriod
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
    Yearly = 3,
}
