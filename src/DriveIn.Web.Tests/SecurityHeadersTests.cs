using DriveIn.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DriveIn.Web.Tests;

public class SecurityHeadersTests
{
    private static async Task<(HttpContext Context, string? NonceSeenByPage)> SendAsync(bool development = false, bool stripe = false)
    {
        string? seen = null;
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSecurityHeaders(development, stripe: stripe);
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
        Assert.Contains($"script-src 'self' 'nonce-{nonce}';", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        // Google sign-in is a form post that redirects to Google; form-action covers the redirect.
        Assert.Contains("form-action 'self' https://accounts.google.com", csp);
        Assert.Contains("connect-src 'self';", csp);
        Assert.Contains("frame-src 'none';", csp);
        Assert.DoesNotContain("stripe.com", csp); // not unless Stripe is the processor
        // Fonts are self-hosted (wwwroot/fonts), so no font CDN is allowed.
        Assert.Contains("font-src 'self';", csp);
        Assert.Contains("style-src 'self' 'unsafe-inline';", csp);
    }

    [Fact]
    public async Task With_Stripe_the_csp_lets_its_card_fields_load_and_nothing_else_frame()
    {
        var (http, nonce) = await SendAsync(stripe: true);
        var csp = http.Response.Headers.ContentSecurityPolicy.ToString();

        Assert.Contains($"script-src 'self' 'nonce-{nonce}' https://js.stripe.com;", csp);
        Assert.Contains("connect-src 'self' https://api.stripe.com;", csp);
        // Stripe's Payment Element runs in its own iframes; no other site can be framed.
        Assert.Contains("frame-src https://js.stripe.com https://hooks.stripe.com;", csp);
    }

    [Fact]
    public void The_image_cdn_and_stripe_can_both_be_allowed()
    {
        var csp = SecurityHeaders.ContentSecurityPolicy("n", development: false, imageOrigin: "https://images.example.test", stripe: true);

        Assert.Contains("img-src 'self' data: blob: https://images.example.test;", csp);
        Assert.Contains("connect-src 'self' https://api.stripe.com;", csp);
        Assert.Contains("frame-src https://js.stripe.com https://hooks.stripe.com;", csp);
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
