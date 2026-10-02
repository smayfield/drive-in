using DriveIn.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DriveIn.Web.Tests;

public class SecurityHeadersTests
{
    private static async Task<(HttpContext Context, string? NonceSeenByPage)> SendAsync(bool development = false)
    {
        string? seen = null;
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSecurityHeaders(development);
        app.Run(context =>
        {
            seen = SecurityHeaders.Nonce(context);
            return Task.CompletedTask;
        });
        var http = new DefaultHttpContext();
        await app.Build()(http);
        return (http, seen);
    }

    [Fact]
    public async Task Every_response_gets_the_security_headers()
    {
        var (http, _) = await SendAsync();
        var headers = http.Response.Headers;

        Assert.Equal("nosniff", headers.XContentTypeOptions);
        Assert.Equal("DENY", headers.XFrameOptions);
        Assert.Equal("strict-origin-when-cross-origin", headers["Referrer-Policy"]);
        Assert.Equal("same-origin", headers["Cross-Origin-Opener-Policy"]);
        Assert.Contains("geolocation=(self)", headers["Permissions-Policy"].ToString());
        Assert.Contains("microphone=()", headers["Permissions-Policy"].ToString());
    }

    [Fact]
    public async Task Csp_allows_only_our_own_scripts_plus_the_nonced_import_map()
    {
        var (http, nonce) = await SendAsync();
        var csp = http.Response.Headers.ContentSecurityPolicy.ToString();

        Assert.False(string.IsNullOrEmpty(nonce));
        Assert.Contains($"script-src 'self' 'nonce-{nonce}'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        // Google sign-in is a form post that redirects to Google; form-action covers the redirect.
        Assert.Contains("form-action 'self' https://accounts.google.com", csp);
        Assert.Contains("connect-src 'self';", csp);
    }

    [Fact]
    public async Task Each_request_gets_its_own_nonce()
    {
        var (_, first) = await SendAsync();
        var (_, second) = await SendAsync();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Development_lets_the_browser_refresh_socket_connect()
    {
        var (http, _) = await SendAsync(development: true);

        Assert.Contains("connect-src 'self' ws://localhost:* wss://localhost:*", http.Response.Headers.ContentSecurityPolicy.ToString());
    }

    [Fact]
    public void Without_the_middleware_there_is_no_nonce() =>
        Assert.Equal("", SecurityHeaders.Nonce(new DefaultHttpContext()));
}
