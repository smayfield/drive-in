using System.Security.Claims;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DriveIn.Web.Authorization;

// Runs on every sign-in (local, Google, invite acceptance) and on cookie refresh.
// - Adds the employee-theater claim used by TheaterAuthorizationHandler.
// - Bootstraps the first admin: a confirmed account whose email matches Seed:AdminEmail
//   is put in the Admin role, so the admin only has to register.
public sealed class AppClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options,
    IConfiguration configuration)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var adminEmail = configuration["Seed:AdminEmail"];
        if (!string.IsNullOrWhiteSpace(adminEmail)
            && user.EmailConfirmed
            && user.EmployeeTheaterId is null
            && string.Equals(user.Email, adminEmail.Trim(), StringComparison.OrdinalIgnoreCase)
            && await RoleManager.RoleExistsAsync(Roles.Admin)
            && !await UserManager.IsInRoleAsync(user, Roles.Admin))
        {
            await UserManager.AddToRoleAsync(user, Roles.Admin);
        }

        var identity = await base.GenerateClaimsAsync(user);

        if (user.EmployeeTheaterId is int theaterId)
            identity.AddClaim(new Claim(AppClaims.EmployeeTheater, theaterId.ToString()));
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            identity.AddClaim(new Claim("drivein:display_name", user.DisplayName));

        return identity;
    }
}
