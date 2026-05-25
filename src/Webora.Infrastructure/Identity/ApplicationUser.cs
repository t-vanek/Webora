using Microsoft.AspNetCore.Identity;

namespace Webora.Infrastructure.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser() => Id = Guid.CreateVersion7();

    public string? DisplayName { get; set; }
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() => Id = Guid.CreateVersion7();

    public ApplicationRole(string roleName) : this() => Name = roleName;
}
