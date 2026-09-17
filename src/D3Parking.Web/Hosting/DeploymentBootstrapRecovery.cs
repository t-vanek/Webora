using System.Net.Mail;
using System.Text.Json;
using D3Parking.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace D3Parking.Web.Hosting;

/// <summary>Read-only admission for finishing the bootstrap of an interrupted first installation.</summary>
public static class DeploymentBootstrapRecovery
{
    public static async Task<bool> IsEligibleAsync(D3ParkingDbContext db, IConfiguration config,
        string root, string environment, CancellationToken ct)
    {
        // The caller must additionally limit this exception to preflight. Ordinary deployment
        // must not recreate an administrator whose access was intentionally removed.
        if (!bool.TryParse(config["deployment-recovery"], out var recovery) || !recovery
            || !Path.IsPathFullyQualified(root) || !HasValidBootstrapCredentials(config))
            return false;

        if (!HasFirstInstallationJournal(root, ReleaseInformation.Read(environment).Version))
            return false;

        // A journal is not proof that every migration completed. Verify the actual schema
        // before allowing the host's transactional identity seed to run again.
        await DeploymentDatabase.RequireCurrentSchemaAsync(db, ct);
        return !await db.Users.AnyAsync(ct);
    }

    private static bool HasValidBootstrapCredentials(IConfiguration config)
    {
        var email = config["IdentitySeed:AdminEmail"];
        var password = config["IdentitySeed:AdminPassword"];
        var allowedUserNameCharacters = new IdentityOptions().User.AllowedUserNameCharacters!;
        // The seed also uses the email as UserName. MailAddress alone accepts display names
        // and characters that Identity's default user validator rejects.
        return MailAddress.TryCreate(email, out var address)
            && string.Equals(address.Address, email, StringComparison.Ordinal)
            && email!.All(allowedUserNameCharacters.Contains)
            && password is { Length: >= 12 }
            && password.Any(char.IsAsciiLetterUpper) && password.Any(char.IsAsciiLetterLower)
            && password.Any(char.IsAsciiDigit) && password.Any(c => !char.IsAsciiLetterOrDigit(c));
    }

    private static bool HasFirstInstallationJournal(string root, string version)
    {
        var installation = Path.Combine(root, "state", "installation.json");
        try
        {
            // File.Exists treats inaccessible paths as absent. Fail closed instead, including
            // when a directory or an unreadable file occupies the installation-state path.
            try { File.GetAttributes(installation); return false; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            using var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "state", "in-progress.json")));
            var entry = journal.RootElement;
            if (entry.ValueKind != JsonValueKind.Object) return false;
            foreach (var name in new[] { "previous", "target", "phase" })
                if (entry.EnumerateObject().Count(p => p.NameEquals(name)) != 1) return false;
            return entry.GetProperty("previous").ValueKind == JsonValueKind.Null
                && entry.GetProperty("target").ValueKind == JsonValueKind.String
                && entry.GetProperty("target").GetString() == version
                && entry.GetProperty("phase").ValueKind == JsonValueKind.String
                && entry.GetProperty("phase").GetString() is "database upgrade" or "starting target" or "recovery start";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
