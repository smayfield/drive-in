using System.Security.Cryptography;

namespace DriveIn.Web.Services;

// Browser security headers for every response the app sends (pages, static files, endpoints). Grafana isn't covered:
// Caddy proxies /grafana/ to it directly. Registered after the exception handler so the re-executed error and
// not-found pages get them too (the handler clears the response's headers before re-executing).
public static class SecurityHeaders
{
    private const string NonceKey = "drivein:csp-nonce";

    // The only inline script is Blazor's import map (<ImportMap />); it carries this per-request nonce.
    public static string Nonce(HttpContext? context) =>
        context?.Items[NonceKey] as string ?? "";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, bool development) =>
        app.Use((context, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceKey] = nonce;
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy(nonce, development);
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = PermissionsPolicy;
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            // frame-ancestors supersedes it; kept for browsers that predate CSP 2.
            headers.XFrameOptions = "DENY";
            return next(context);
        });

    // Geolocation is for "theaters near me"; the camera is kept for a gate scanner. Everything else is off.
    public const string PermissionsPolicy =
        "camera=(self), microphone=(), geolocation=(self), payment=(self), usb=()";

    public static string ContentSecurityPolicy(string nonce, bool development) => string.Join("; ",
        "default-src 'self'",
        $"script-src 'self' 'nonce-{nonce}'",
        // MudBlazor and Quill set inline styles (style attributes and the theme's <style> element).
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
        "font-src 'self' https://fonts.gstatic.com",
        // Inline images: QR codes and pasted images are data: URIs; previews of a chosen file are blob: URLs.
        "img-src 'self' data: blob:",
        // 'self' covers the Blazor circuit's WebSocket on the same host. Locally, dotnet watch's browser refresh
        // connects to its own localhost port.
        development ? "connect-src 'self' ws://localhost:* wss://localhost:*" : "connect-src 'self'",
        // Sign-in with Google posts to us and is redirected to Google, and form-action applies to that redirect.
        "form-action 'self' https://accounts.google.com",
        "frame-ancestors 'none'",
        "frame-src 'none'",
        "object-src 'none'",
        "base-uri 'self'");
}
