using System.Security.Claims;
using DriveIn.Web.Authorization;
using Microsoft.AspNetCore.Http;

namespace DriveIn.Web.Tests;

public class GrafanaAuthTests
{
    private static ClaimsPrincipal WithEmail(ClaimsPrincipal user, string email)
    {
        ((ClaimsIdentity)user.Identity!).AddClaim(new Claim(ClaimTypes.Email, email));
        return user;
    }

    [Fact]
    public void Admin_is_let_in_as_their_lowercased_email()
    {
        var decision = GrafanaAuth.Check(WithEmail(Principals.Create("u1", admin: true), "Boss@Example.com"), "/grafana/d/x");

        Assert.Equal(StatusCodes.Status200OK, decision.StatusCode);
        Assert.Equal("boss@example.com", decision.User);
    }

    [Fact]
    public void Admin_without_an_ascii_email_is_let_in_by_user_id()
    {
        var decision = GrafanaAuth.Check(WithEmail(Principals.Create("u1", admin: true), "jösé@example.com"), null);

        Assert.Equal(StatusCodes.Status200OK, decision.StatusCode);
        Assert.Equal("u1", decision.User);
    }

    [Fact]
    public void Signed_in_non_admin_is_refused()
    {
        var decision = GrafanaAuth.Check(WithEmail(Principals.Create("u2"), "owner@example.com"), "/grafana/");

        Assert.Equal(StatusCodes.Status403Forbidden, decision.StatusCode);
        Assert.Null(decision.User);
    }

    [Fact]
    public void Employee_is_refused()
    {
        var decision = GrafanaAuth.Check(Principals.Create("u3", employeeTheaterId: 1), "/grafana/");

        Assert.Equal(StatusCodes.Status403Forbidden, decision.StatusCode);
    }

    [Fact]
    public void Anonymous_is_sent_to_sign_in_and_back_to_the_page()
    {
        var decision = GrafanaAuth.Check(Principals.Anonymous, "/grafana/d/abc?from=now-7d");

        Assert.Equal(StatusCodes.Status302Found, decision.StatusCode);
        Assert.Null(decision.User);
        Assert.Equal("/Account/Login?ReturnUrl=%2Fgrafana%2Fd%2Fabc%3Ffrom%3Dnow-7d", decision.Location);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/grafana/")]
    [InlineData("/Account/Manage")]
    public void Sign_in_only_returns_into_grafana(string? forwardedUri)
    {
        var decision = GrafanaAuth.Check(Principals.Anonymous, forwardedUri);

        Assert.Equal("/Account/Login?ReturnUrl=%2Fgrafana%2F", decision.Location);
    }
}
