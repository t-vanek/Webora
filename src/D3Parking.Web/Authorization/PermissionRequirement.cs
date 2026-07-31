using Microsoft.AspNetCore.Authorization;

namespace D3Parking.Web.Authorization;

/// <summary>
/// Satisfied when the user holds any one of <see cref="Permissions"/>. One permission is the
/// ordinary case; the any-of form exists for screens that merge several review queues, where
/// holding either queue's permission is enough to open the page (the queues themselves stay
/// gated individually inside it).
/// </summary>
public sealed class PermissionRequirement(params string[] permissions) : IAuthorizationRequirement
{
    public IReadOnlyList<string> Permissions { get; } = permissions;
}
