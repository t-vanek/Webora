using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Webora.Domain.Accounts;
using Webora.Infrastructure.Identity;

namespace Webora.Web.Identity;

/// <summary>
/// Only accounts in the <see cref="AccountStatus.Active"/> state may sign in. This is what gives
/// activation, deactivation, suspension and blocking their effect on authentication.
/// </summary>
public sealed class WeboraSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        if (user.Status != AccountStatus.Active)
        {
            Logger.LogWarning("Sign-in blocked for {UserId}: account status is {Status}", user.Id, user.Status);
            return false;
        }

        return await base.CanSignInAsync(user);
    }
}
