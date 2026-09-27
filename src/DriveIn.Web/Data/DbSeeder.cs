using DriveIn.Web.Authorization;
using Microsoft.AspNetCore.Identity;

namespace DriveIn.Web.Data;

public static class DbSeeder
{
    // Ensures the Admin role exists and, if Seed:AdminEmail names an existing confirmed account,
    // puts it in that role. Accounts registered later are promoted at sign-in by AppClaimsPrincipalFactory.
    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        if (!await roles.RoleExistsAsync(Roles.Admin))
            await roles.CreateAsync(new IdentityRole(Roles.Admin));

        var adminEmail = config["Seed:AdminEmail"];
        if (string.IsNullOrWhiteSpace(adminEmail))
            return;
        var admin = await users.FindByEmailAsync(adminEmail.Trim());
        if (admin is { EmailConfirmed: true, EmployeeTheaterId: null } && !await users.IsInRoleAsync(admin, Roles.Admin))
            await users.AddToRoleAsync(admin, Roles.Admin);
    }
}
