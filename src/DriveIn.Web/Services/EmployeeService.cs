using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record EmployeeSummary(string Id, string Email, string? DisplayName, bool HasPassword, bool IsLockedOut, DateTimeOffset CreatedAt);

// Owner-side management of a theater's employee accounts. Inviting lives in InvitationService.
public sealed class EmployeeService(
    IServiceScopeFactory scopeFactory,
    IAuthorizationService auth,
    IEmailSender<ApplicationUser> identityEmail,
    TimeProvider time)
{
    public async Task<List<EmployeeSummary>> ListAsync(ClaimsPrincipal actor, int theaterId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await RequireAsync(db, actor, theaterId);
        var now = time.GetUtcNow();
        return await db.Users.AsNoTracking()
            .Where(u => u.EmployeeTheaterId == theaterId)
            .OrderBy(u => u.Email)
            .Select(u => new EmployeeSummary(u.Id, u.Email!, u.DisplayName, u.PasswordHash != null,
                u.LockoutEnd != null && u.LockoutEnd > now, u.CreatedAt))
            .ToListAsync();
    }

    public async Task SendPasswordResetAsync(ClaimsPrincipal actor, int theaterId, string userId, string baseUri)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await RequireAsync(db, actor, theaterId);
        var user = await LoadEmployeeAsync(users, theaterId, userId);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        await identityEmail.SendPasswordResetLinkAsync(user, user.Email!,
            System.Net.WebUtility.HtmlEncode(AccountLinks.PasswordReset(baseUri, token)));
    }

    public async Task DeleteAsync(ClaimsPrincipal actor, int theaterId, string userId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await RequireAsync(db, actor, theaterId);
        var user = await LoadEmployeeAsync(users, theaterId, userId);
        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
            throw new AppValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    private async Task RequireAsync(ApplicationDbContext db, ClaimsPrincipal actor, int theaterId)
    {
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(actor, theater, TheaterOperations.ManageEmployees);
    }

    // Only accounts employed by this theater; an owner can't touch anyone else's account.
    private static async Task<ApplicationUser> LoadEmployeeAsync(UserManager<ApplicationUser> users, int theaterId, string userId)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null || user.EmployeeTheaterId != theaterId)
            throw new NotFoundException("Employee not found.");
        return user;
    }
}
