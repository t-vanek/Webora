using Microsoft.AspNetCore.Identity;
using Webora.Domain.Accounts;

namespace Webora.Infrastructure.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser() => Id = Guid.CreateVersion7();

    public string? DisplayName { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.PendingActivation;

    public DateTimeOffset? StatusChangedAtUtc { get; set; }

    public string? StatusReason { get; set; }
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() => Id = Guid.CreateVersion7();

    public ApplicationRole(string roleName) : this() => Name = roleName;
}
