using Microsoft.AspNetCore.Identity;

namespace Webora.Infrastructure.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
}

public class ApplicationRole : IdentityRole<Guid>;
