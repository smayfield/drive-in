using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;

namespace DriveIn.Web.Tests;

public class TheaterAuthorizationHandlerTests
{
    private static readonly Theater Theater = new() { Id = 1, Name = "Starlight", Slug = "starlight", OwnerId = "owner" };

    public static TheoryData<string, bool, bool> Matrix => new()
    {
        // who, can operate, can manage employees
        { "admin", true, true },
        { "owner", true, true },
        { "own-employee", true, false },
        { "other-employee", false, false },
        { "other-owner", false, false },
        { "regular", false, false },
        { "anonymous", false, false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Enforces_theater_access(string who, bool canOperate, bool canManageEmployees)
    {
        var user = who switch
        {
            "admin" => Principals.Create("admin", admin: true),
            "owner" => Principals.Create("owner"),
            "own-employee" => Principals.Create("emp1", employeeTheaterId: 1),
            "other-employee" => Principals.Create("emp2", employeeTheaterId: 2),
            "other-owner" => Principals.Create("someone-else"),
            "regular" => Principals.Create("regular"),
            _ => Principals.Anonymous,
        };

        Assert.Equal(canOperate, await Authorize(user, TheaterOperations.Operate));
        Assert.Equal(canManageEmployees, await Authorize(user, TheaterOperations.ManageEmployees));
    }

    private static async Task<bool> Authorize(System.Security.Claims.ClaimsPrincipal user,
        Microsoft.AspNetCore.Authorization.Infrastructure.OperationAuthorizationRequirement op)
    {
        var context = new AuthorizationHandlerContext([op], user, Theater);
        await new TheaterAuthorizationHandler().HandleAsync(context);
        return context.HasSucceeded;
    }
}
