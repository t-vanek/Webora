namespace Webora.Infrastructure.Accounts;

public sealed class AccountOptions
{
    public const string SectionName = "Account";

    /// <summary>Base URL used to build links in account emails (no trailing slash).</summary>
    public string BaseUrl { get; set; } = "https://localhost";
}
