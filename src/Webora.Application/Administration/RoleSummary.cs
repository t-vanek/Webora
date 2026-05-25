namespace Webora.Application.Administration;

/// <summary>A single row in the role administration list.</summary>
public sealed record RoleSummary(
    Guid Id,
    string Name,
    int PermissionCount,
    int UserCount,
    bool IsDefault);

/// <summary>Full detail of a role: its name and the permissions granted to it.</summary>
public sealed record RoleDetail(
    Guid Id,
    string Name,
    bool IsDefault,
    IReadOnlyList<string> Permissions);
