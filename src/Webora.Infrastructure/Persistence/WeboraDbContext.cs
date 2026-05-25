using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Webora.Infrastructure.Identity;

namespace Webora.Infrastructure.Persistence;

public class WeboraDbContext(DbContextOptions<WeboraDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Registers the OpenIddict entity sets (applications, authorizations, scopes, tokens).
        builder.UseOpenIddict();
    }
}
