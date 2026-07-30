using D3Parking.Domain.Common;

namespace D3Parking.Domain.Authorization;

/// <summary>
/// Maps a role coming from an external directory onto one of this application's roles.
/// </summary>
/// <remarks>
/// The directory decides who is a "Parking.LotManager"; this application decides what that means.
/// Keeping the translation in a table rather than in configuration means the mapping is
/// administered on the same screens as everything else, is audited, and is bounded by the same rule
/// as every other grant: nobody may map a directory role onto a local role stronger than their own.
/// </remarks>
public class ExternalRoleMapping : Entity, IAggregateRoot
{
    /// <summary>The directory the claim comes from. Only <see cref="ExternalProviders.EntraId"/> today.</summary>
    public string Provider { get; private set; } = ExternalProviders.EntraId;

    /// <summary>
    /// The app role's value as it appears in the token's <c>roles</c> claim — the string configured
    /// in the Entra app registration, not its display name or id.
    /// </summary>
    public string ExternalRole { get; private set; } = string.Empty;

    public Guid RoleId { get; private set; }

    private ExternalRoleMapping() { }

    public ExternalRoleMapping(string provider, string externalRole, Guid roleId)
    {
        Provider = provider;
        ExternalRole = externalRole.Trim();
        RoleId = roleId;
    }

    public void Retarget(Guid roleId) => RoleId = roleId;
}

/// <summary>
/// A role assignment this application made on the directory's behalf.
/// </summary>
/// <remarks>
/// Without this record a sync could not tell "the directory gave them this" from "an administrator
/// gave them this by hand", and would have to choose between never removing anything (stale access
/// outlives the person's Entra assignment) or removing everything (a local grant silently disappears
/// at the next sign-in). Tracking provenance lets a sync take back exactly what it granted.
/// </remarks>
public class ExternalRoleAssignment
{
    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public string Provider { get; private set; } = ExternalProviders.EntraId;

    private ExternalRoleAssignment() { }

    public ExternalRoleAssignment(Guid userId, Guid roleId, string provider)
    {
        UserId = userId;
        RoleId = roleId;
        Provider = provider;
    }
}

/// <summary>The external identity providers this application understands.</summary>
public static class ExternalProviders
{
    /// <summary>Microsoft Entra ID. Doubles as the ASP.NET Identity login provider key.</summary>
    public const string EntraId = "EntraId";
}
