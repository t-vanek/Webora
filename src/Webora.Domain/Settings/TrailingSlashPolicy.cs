namespace Webora.Domain.Settings;

/// <summary>How a trailing slash on the URL path is canonicalized.</summary>
public enum TrailingSlashPolicy
{
    /// <summary>Leave the path as-is.</summary>
    NoPreference,

    /// <summary>Canonical form ends with a slash (e.g. /about → /about/).</summary>
    Add,

    /// <summary>Canonical form has no trailing slash (e.g. /about/ → /about).</summary>
    Remove,
}
