namespace D3Parking.Domain.Accounts;

/// <summary>The account lifecycle state machine: which status transitions are permitted.</summary>
public static class AccountStatusTransitions
{
    private static readonly IReadOnlyDictionary<AccountStatus, IReadOnlySet<AccountStatus>> Allowed =
        new Dictionary<AccountStatus, IReadOnlySet<AccountStatus>>
        {
            [AccountStatus.PendingActivation] = new HashSet<AccountStatus> { AccountStatus.Active, AccountStatus.Blocked },
            [AccountStatus.Active] = new HashSet<AccountStatus> { AccountStatus.Deactivated, AccountStatus.Suspended, AccountStatus.Blocked },
            [AccountStatus.Deactivated] = new HashSet<AccountStatus> { AccountStatus.Active, AccountStatus.Blocked },
            [AccountStatus.Suspended] = new HashSet<AccountStatus> { AccountStatus.Active, AccountStatus.Blocked },
            [AccountStatus.Blocked] = new HashSet<AccountStatus> { AccountStatus.Active },
        };

    public static bool IsAllowed(AccountStatus from, AccountStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
}
