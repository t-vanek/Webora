using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace D3Parking.Infrastructure.Persistence;

/// <summary>
/// Lets the EF Core CLI build the context at design time (e.g. for `dotnet ef migrations`)
/// without booting the web host. The connection string is only used to pick the provider —
/// no database connection is opened when scaffolding migrations.
/// </summary>
public class D3ParkingDbContextFactory : IDesignTimeDbContextFactory<D3ParkingDbContext>
{
    public D3ParkingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("D3PARKING_DESIGN_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=D3Parking;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new D3ParkingDbContext(options);
    }
}
