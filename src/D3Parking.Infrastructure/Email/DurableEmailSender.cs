using System.Text.Json;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Domain.Email;
using D3Parking.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace D3Parking.Infrastructure.Email;

public sealed class DurableEmailSender(
    IDbContextFactory<D3ParkingDbContext> factory,
    IDataProtectionProvider protection,
    TimeProvider clock) : IEmailSender
{
    internal const string ProtectionPurpose = "D3Parking.EmailOutbox.v1";

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var envelope = message with { MessageId = $"{Guid.NewGuid():N}@d3parking.local" };
        var payload = protection.CreateProtector(ProtectionPurpose).Protect(JsonSerializer.Serialize(envelope));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.EmailDeliveries.Add(new EmailDelivery(payload, clock.GetUtcNow()));
        // Do not acknowledge volatile work: failure to commit is surfaced to the caller.
        await db.SaveChangesAsync(cancellationToken);
    }
}
