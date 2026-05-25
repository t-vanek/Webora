using Microsoft.EntityFrameworkCore;
using Webora.Infrastructure.Identity;
using Webora.Infrastructure.Persistence;

namespace Webora.Web;

public static class IdentitySeedingExtensions
{
    public static async Task SeedIdentityAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        // In development apply pending migrations automatically; in production migrations are
        // expected to be applied as a deliberate deployment step.
        if (app.Environment.IsDevelopment())
        {
            var dbContext = services.GetRequiredService<WeboraDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        var seeder = services.GetRequiredService<IdentitySeeder>();
        await seeder.SeedAsync();
    }
}
