using Microsoft.Extensions.Localization;

namespace D3Parking.Web.Resources;

/// <summary>
/// Picks the resource variant that agrees with a count.
/// </summary>
/// <remarks>
/// Czech inflects in three classes — "1 uživatel / 2 uživatelé / 5 uživatelů" — so a template with
/// one fixed noun is wrong two thirds of the time. Resources carry the variants under the
/// <c>_1</c>, <c>_2</c> and <c>_5</c> suffixes; English simply repeats its plural for the last two.
/// </remarks>
public static class PluralText
{
    /// <summary>Czech plural class: 1, 2–4, 5+.</summary>
    public static int Form(int count) => count == 1 ? 1 : count is >= 2 and <= 4 ? 2 : 5;

    /// <summary>Resolves "{keyStem}_{form}" for the count and formats it with the count.</summary>
    public static string For(IStringLocalizer localizer, string keyStem, int count) =>
        localizer[$"{keyStem}_{Form(count)}", count];
}
