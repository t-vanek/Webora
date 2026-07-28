namespace D3Parking.Domain.Authorization;

public static class Roles
{
    public const string Administrator = "Administrator";
    public const string Editor = "Editor";
    public const string Viewer = "Viewer";

    /// <summary>The built-in roles created by the seeder. They cannot be renamed or deleted.</summary>
    public static IReadOnlyList<string> Defaults { get; } = [Administrator, Editor, Viewer];

    public static bool IsDefault(string roleName) =>
        Defaults.Contains(roleName, StringComparer.Ordinal);
}
