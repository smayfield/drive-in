using System.Security.Claims;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace DriveIn.Web.Authorization;

public static class Roles
{
    public const string Admin = "Admin";
}

public static class Policies
{
    public const string Admin = "Admin";
}

public static class AppClaims
{
    // Present only on employee accounts; value is the theater id.
    public const string EmployeeTheater = "drivein:employee_theater";
}

public static class TheaterOperations
{
    // Edit the theater profile and screens: admin, owner, or one of its employees.
    public static readonly OperationAuthorizationRequirement Operate = new() { Name = nameof(Operate) };

    // Invite, reset, and delete employees: admin or owner only.
    public static readonly OperationAuthorizationRequirement ManageEmployees = new() { Name = nameof(ManageEmployees) };
}

public static class ClaimsPrincipalExtensions
{
    public static string? GetUserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static int? GetEmployeeTheaterId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(AppClaims.EmployeeTheater), out var id) ? id : null;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);
}

public sealed class TheaterAuthorizationHandler : AuthorizationHandler<OperationAuthorizationRequirement, Theater>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OperationAuthorizationRequirement requirement, Theater theater)
    {
        var user = context.User;
        var userId = user.GetUserId();
        if (userId is null)
            return Task.CompletedTask;

        var isAdmin = user.IsAdmin();
        var isOwner = theater.OwnerId == userId;
        var isEmployee = user.GetEmployeeTheaterId() == theater.Id;

        var allowed = requirement.Name switch
        {
            nameof(TheaterOperations.Operate) => isAdmin || isOwner || isEmployee,
            nameof(TheaterOperations.ManageEmployees) => isAdmin || isOwner,
            _ => false,
        };

        if (allowed)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
