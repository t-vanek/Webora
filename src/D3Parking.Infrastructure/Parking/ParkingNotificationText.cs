using Microsoft.Extensions.Localization;
using D3Parking.Domain.Parking.Incentives;

namespace D3Parking.Infrastructure.Parking;

/// <summary>
/// Selects notification copy for installations with and without the credit economy. Every key
/// passed here must have a matching <c>_NoCredits</c> resource so disabling credits cannot leave
/// price, wallet or refund wording in a user's notification.
/// </summary>
internal static class ParkingNotificationText
{
    internal static LocalizedString ForEconomy(
        this IStringLocalizer<ParkingMessages> messages,
        IncentivePolicy policy,
        string key,
        params object[] arguments) =>
        messages[policy.CreditsEnabled ? key : $"{key}_NoCredits", arguments];
}
