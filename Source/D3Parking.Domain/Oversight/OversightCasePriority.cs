namespace D3Parking.Domain.Oversight;

/// <summary>
/// How urgent the case is. Set by hand for now; the ingest opens everything at
/// <see cref="Normal"/> because deriving urgency from the signal (an outsider's car blocking a
/// spot today, a pair at 90 % concentration) needs rules that belong with the SLA clock, not
/// before it.
/// </summary>
public enum OversightCasePriority
{
    Low,
    Normal,
    High,
    Critical,
}
