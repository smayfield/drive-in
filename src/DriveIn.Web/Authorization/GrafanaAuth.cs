using System.Security.Claims;

namespace DriveIn.Web.Authorization;

// Who may use the metrics site (Grafana at /grafana/). Caddy's forward_auth asks /ops/grafana-auth before every
// Grafana request: site admins get a 200 naming them in UserHeader, which Grafana's auth proxy signs them in as (and
// Caddy drops any copy of it the client sent). Anyone else gets this response instead of Grafana.
public static class GrafanaAuth
{
    public const string UserHeader = "X-WEBAUTH-USER";
    public const string BasePath = "/grafana/";

    public sealed record Decision(int StatusCode, string? User = null, string? Location = null);

    // forwardedUri is the Grafana path the browser asked for (Caddy's X-Forwarded-Uri), to come back to after sign-in.
    public static Decision Check(ClaimsPrincipal user, string? forwardedUri)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            // Only ever back into Grafana: the header comes from Caddy, but don't make it an open redirect anyway.
            var returnUrl = IsGrafanaPath(forwardedUri) ? forwardedUri! : BasePath;
            return new(StatusCodes.Status302Found, Location: "/Account/Login?ReturnUrl=" + Uri.EscapeDataString(returnUrl));
        }
        if (!user.IsAdmin())
            return new(StatusCodes.Status403Forbidden);

        // Grafana's login. The email (lowercased, so the seeded admin matches GF_SECURITY_ADMIN_USER however it was
        // typed), else the user id; a header value must be ASCII.
        var email = user.FindFirstValue(ClaimTypes.Email)?.ToLowerInvariant();
        var login = email is not null && email.All(char.IsAscii) && !email.Any(char.IsControl) ? email : user.GetUserId();
        return login is null ? new(StatusCodes.Status403Forbidden) : new(StatusCodes.Status200OK, User: login);
    }

    // Under BasePath, with no dot segments (plain or percent-encoded) or backslashes that a browser would resolve to
    // somewhere outside it, such as /grafana/../Account/Manage.
    private static bool IsGrafanaPath(string? uri)
    {
        if (uri is null || !uri.StartsWith(BasePath, StringComparison.Ordinal))
            return false;
        var path = Uri.UnescapeDataString(uri.Split('?', '#')[0]);
        return !path.Contains('\\') && path.Split('/').All(segment => segment is not ("." or ".."));
    }
}
