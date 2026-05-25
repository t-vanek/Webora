using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Webora.Infrastructure.Persistence;

/// <summary>
/// Lets the EF Core CLI build the context at design time (e.g. for `dotnet ef migrations`)
/// without booting the web host. The connection string is only used to pick the provider —
/// no database connection is opened when scaffolding migrations.
/// </summary>
public class WeboraDbContextFactory : IDesignTimeDbContextFactory<WeboraDbContext>
{
    public WeboraDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("WEBORA_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=webora;Username=webora;Password=webora";

        var options = new DbContextOptionsBuilder<WeboraDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new WeboraDbContext(options);
    }
}
