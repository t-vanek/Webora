using System.Security.Claims;
using D3Parking.Domain.Authorization;
using D3Parking.Web.Identity;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// The two decisions the external sign-in makes before any account is touched: where it is willing
/// to send the browser back to, and what it is prepared to believe the directory said. Both are
/// pure, both are security guards, and neither needs a browser or a database to exercise.
/// </summary>
[TestFixture]
public class ExternalSignInTests
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string TenantIdClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    [TestCase("/reserve", "/reserve")]
    [TestCase("/", "/")]
    [TestCase("/lot/3?tab=history", "/lot/3?tab=history")]
    public void A_local_path_is_followed_back(string returnUrl, string expected)
    {
        Assert.That(ExternalSignInEndpoints.Safe(returnUrl), Is.EqualTo(expected));
    }

    // Every one of these arrives carrying a freshly minted session cookie if it is followed, which
    // is what makes an open redirect here worse than an open redirect anywhere else on the site.
    [TestCase("https://evil.example/steal", Description = "absolute URL")]
    [TestCase("//evil.example/steal", Description = "protocol-relative")]
    [TestCase(@"/\evil.example/steal", Description = "backslash, which browsers normalise to //")]
    [TestCase(@"\\evil.example/steal", Description = "UNC-looking")]
    [TestCase("reserve", Description = "no leading slash — could resolve anywhere")]
    [TestCase("", Description = "empty")]
    [TestCase(null, Description = "absent")]
    public void Anything_that_could_leave_the_site_falls_back_to_the_root(string? returnUrl)
    {
        Assert.That(ExternalSignInEndpoints.Safe(returnUrl), Is.EqualTo("/"),
            "A return URL that can leave the site must never be followed after a sign-in.");
    }

    [Test]
    public void An_identity_is_read_from_the_claims_entra_actually_sends()
    {
        var identity = Principal(
            (ObjectIdClaim, "obj-1"),
            (ClaimTypes.Email, "jan@contoso.com"),
            (TenantIdClaim, "tenant-a"),
            ("name", "Jan Novák")).ReadEntraIdentity();

        Assert.That(identity, Is.Not.Null);
        Assert.That(identity!.Provider, Is.EqualTo(ExternalProviders.EntraId));
        Assert.That(identity.ObjectId, Is.EqualTo("obj-1"));
        Assert.That(identity.Email, Is.EqualTo("jan@contoso.com"));
        Assert.That(identity.TenantId, Is.EqualTo("tenant-a"));
        Assert.That(identity.DisplayName, Is.EqualTo("Jan Novák"));
    }

    [Test]
    public void The_short_claim_names_are_read_too()
    {
        // Which of the two spellings arrives depends on the token version and on whether claim
        // mapping is left on; the reader must not care.
        var identity = Principal(
            ("oid", "obj-2"),
            ("email", "eva@contoso.com"),
            ("tid", "tenant-a")).ReadEntraIdentity();

        Assert.That(identity, Is.Not.Null);
        Assert.That(identity!.ObjectId, Is.EqualTo("obj-2"));
        Assert.That(identity.Email, Is.EqualTo("eva@contoso.com"));
        Assert.That(identity.TenantId, Is.EqualTo("tenant-a"));
    }

    [Test]
    public void An_address_is_taken_from_preferred_username_when_there_is_no_email_claim()
    {
        var identity = Principal(
            ("oid", "obj-3"),
            ("preferred_username", "petr@contoso.com")).ReadEntraIdentity();

        Assert.That(identity!.Email, Is.EqualTo("petr@contoso.com"));
    }

    [Test]
    public void A_token_without_an_object_id_is_not_an_identity()
    {
        // The object id is the only stable handle. Without it there is nothing to key an account
        // on, and falling back to the address would merge or split accounts on a rename.
        var identity = Principal((ClaimTypes.Email, "nobody@contoso.com")).ReadEntraIdentity();

        Assert.That(identity, Is.Null);
    }

    [Test]
    public void A_token_without_any_address_is_not_an_identity()
    {
        var identity = Principal((ObjectIdClaim, "obj-4")).ReadEntraIdentity();

        Assert.That(identity, Is.Null);
    }

    [Test]
    public void Roles_are_deduplicated_across_the_two_claim_spellings()
    {
        var identity = Principal(
            (ObjectIdClaim, "obj-5"),
            ("email", "role@contoso.com"),
            ("roles", "Parking.Employee"),
            (ClaimTypes.Role, "parking.employee"),
            ("roles", "Parking.LotManager")).ReadEntraIdentity();

        Assert.That(identity!.Roles, Is.EquivalentTo(new[] { "Parking.Employee", "Parking.LotManager" }));
    }

    [Test]
    public void A_sign_in_that_grants_no_role_says_so_rather_than_staying_silent()
    {
        var identity = Principal(
            (ObjectIdClaim, "obj-6"),
            ("email", "none@contoso.com")).ReadEntraIdentity();

        // Empty, never null: null means "nothing to say about roles" and leaves what the account
        // holds alone, so a revoked app role assignment would survive every sign-in.
        Assert.That(identity!.Roles, Is.Not.Null);
        Assert.That(identity.Roles, Is.Empty);
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "TestAuth"));
}
