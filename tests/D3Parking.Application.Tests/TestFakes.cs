using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Application.Accounts;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Contracts.Notifications;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Application.Tests;

/// <summary>Shared minimal fakes for the SQL Server-backed service tests.</summary>
internal sealed class TestDbContextFactory(DbContextOptions<D3ParkingDbContext> options) : IDbContextFactory<D3ParkingDbContext>
{
    public D3ParkingDbContext CreateDbContext() => new(options);
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Returns the resource key itself; the tests assert behavior, not message text.</summary>
internal sealed class PassthroughLocalizer<T> : IStringLocalizer<T>
{
    public LocalizedString this[string name] => new(name, name);

    public LocalizedString this[string name, params object[] arguments] => new(name, name);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}

/// <summary>The members the exercised services actually call; everything else throws.</summary>
internal sealed class FakeSiteSettings : ISiteSettingsService
{
    public Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken cancellationToken = default) => Task.FromResult(TimeZoneInfo.Utc);

    public Task<string?> GetCanonicalBaseUrlAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<string?> GetDefaultRoleAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<SiteSettingsDto> GetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AccountResult> UpdateDomainAsync(DomainSettingsDto domain, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AccountResult> UpdateRegionalAsync(RegionalSettingsDto regional, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AccountResult> UpdateEncodingAsync(EncodingSettingsDto encoding, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AccountResult> UpdateAccountsAsync(AccountsSettingsDto accounts, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AccountResult> UpdateGeneralAsync(GeneralSettingsDto general, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<DomainPolicy> GetDomainPolicyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<string?> GetDefaultLanguageAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<string> GetPageCharsetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<string> GetEmailCharsetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SiteIdentityDto> GetIdentityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class NullEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>No fleet involvement: every status is NoPlate and every mutation trivially succeeds.</summary>
internal sealed class NullFleetService : IFleetService
{
    public Task<IReadOnlyList<CompanyVehicleDto>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CompanyVehicleDto>>([]);

    public Task<ParkingResult> CreateAsync(string plate, VehicleType type, string? name, string? driverEmail, Guid? spotId, string? notes, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingResult.Success);

    public Task<ParkingResult> UpdateAsync(Guid id, string plate, VehicleType type, string? name, string? driverEmail, Guid? spotId, string? notes, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingResult.Success);

    public Task<ParkingResult> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingResult.Success);

    public Task<ParkingResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingResult.Success);

    public Task<ParkingResult> PairManuallyAsync(Guid id, Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingResult.Success);

    public Task<ParkingResult> UnpairAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingResult.Success);

    public Task<PairingStatusDto> GetMyPairingStatusAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PairingStatusDto(VehiclePairingState.NoPlate, null, null, null, null, null));

    public Task<ParkingResult> RequestPairingCodeAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingResult.Success);

    public Task<ParkingResult> ConfirmPairingAsync(Guid userId, string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingResult.Success);

    public Task SyncUserPlateAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyPairableAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Captures what would have been notified; the fleet tests assert the nudge loop closes.</summary>
internal sealed class RecordingNotificationService : INotificationService
{
    public List<(Guid UserId, string Title)> Sent { get; } = [];

    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default)
    {
        Sent.Add((userId, title));
        return Task.CompletedTask;
    }

    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, CancellationToken cancellationToken = default)
    {
        Sent.Add((userId, title));
        return Task.CompletedTask;
    }

    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, NotificationEmailOptions? emailOptions, CancellationToken cancellationToken = default)
    {
        Sent.Add((userId, title));
        return Task.CompletedTask;
    }

    // The read side mirrors NullNotificationService: nothing the exercised services rely on.
    public Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<NotificationDto>>([]);

    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<NotificationPreferencesDto> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task MuteAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task MuteUntilAsync(Guid userId, DateTimeOffset untilUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UnmuteAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetScopeAsync(Guid userId, NotificationScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetAvailabilityOptInAsync(Guid userId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SubscribeToPushAsync(Guid userId, PushSubscriptionDto subscription, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UnsubscribeFromPushAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class NullNotificationService : INotificationService
{
    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, NotificationEmailOptions? emailOptions, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<NotificationDto>>([]);

    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<NotificationPreferencesDto> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task MuteAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task MuteUntilAsync(Guid userId, DateTimeOffset untilUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UnmuteAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetScopeAsync(Guid userId, NotificationScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetAvailabilityOptInAsync(Guid userId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SubscribeToPushAsync(Guid userId, PushSubscriptionDto subscription, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UnsubscribeFromPushAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
