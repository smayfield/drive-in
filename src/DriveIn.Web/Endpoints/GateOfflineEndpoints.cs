using System.Security.Cryptography;
using System.Text.Json;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace DriveIn.Web.Endpoints;

// The offline gate page's (Components/Pages/Manage/ManageGateOffline.razor) data, sync, service worker and manifest.
// The page is a plain HTML + JS app (wwwroot/gate-offline/) rather than a Blazor circuit, so it keeps working when the
// lot's signal drops: it keeps tonight's admit list in IndexedDB, checks cars in on the device, and syncs when it can.
public static class GateOfflineEndpoints
{
    // The antiforgery request token, sent with the admit list. The page sends it back on every sync in the
    // RequestVerificationToken header (antiforgery's default).
    public const string TokenHeader = "X-Gate-Token";

    public static string PagePath(int theaterId) => $"/manage/{theaterId}/gate/offline";

    public static IEndpointRouteBuilder MapGateOfflineEndpoints(this IEndpointRouteBuilder app)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // Tonight's admit list. The ETag lets the page poll cheaply: an unchanged list is a 304.
        app.MapGet("/manage/{theaterId:int}/gate/offline/data", async Task<IResult> (int theaterId, HttpContext http,
            TicketSalesService sales, IAntiforgery antiforgery) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            OfflineGateList list;
            try
            {
                list = await sales.GetOfflineGateListAsync(http.User, theaterId);
            }
            catch (AccessDeniedException)
            {
                return Results.Forbid();
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
            // Fresh each time; the page sends the latest one it has with its syncs.
            http.Response.Headers[TokenHeader] = antiforgery.GetAndStoreTokens(http).RequestToken;

            // Everything but when it was made, so the same list has the same tag.
            var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(list with { GeneratedAt = default }, json)))[..32]}\"";
            http.Response.Headers.ETag = etag;
            if (http.Request.Headers.IfNoneMatch.Contains(etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(JsonSerializer.SerializeToUtf8Bytes(list, json), "application/json");
        }).RequireAuthorization();

        // Check-ins made on the device. Needs the token from the data endpoint (see TokenHeader): the sign-in cookie
        // alone isn't enough, so another site can't post check-ins as a signed-in attendant.
        app.MapPost("/manage/{theaterId:int}/gate/offline/sync", async Task<IResult> (int theaterId, HttpContext http,
            TicketSalesService sales, IAntiforgery antiforgery) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { error = "token" });
            }
            OfflineSyncRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<OfflineSyncRequest>(json);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = "body" });
            }
            try
            {
                var results = await sales.SyncOfflineAdmissionsAsync(http.User, theaterId, request?.Admissions ?? []);
                return Results.Json(new { results }, json);
            }
            catch (AccessDeniedException)
            {
                return Results.Forbid();
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
            catch (AppValidationException ex)
            {
                return Results.BadRequest(new { error = "invalid", message = ex.Message });
            }
        }).RequireAuthorization();

        // The service worker, served next to the page so its scope can be the page's path (a worker can only control
        // paths under its own folder). Plain JS from wwwroot; no secrets, so no sign-in needed.
        app.MapGet("/manage/{theaterId:int}/gate/offline-sw.js", (int theaterId, IWebHostEnvironment env, HttpContext http) =>
        {
            var file = env.WebRootFileProvider.GetFileInfo("gate-offline/sw.js");
            if (!file.Exists)
                return Results.NotFound();
            // The browser checks for a new worker on each visit; don't let a cache stand in the way of updates.
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Stream(file.CreateReadStream(), "text/javascript; charset=utf-8");
        });

        // So the page can be installed to a home screen and opened like an app, straight to this theater's gate.
        app.MapGet("/manage/{theaterId:int}/gate/offline/manifest.webmanifest", (int theaterId, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-cache";
            var path = PagePath(theaterId);
            return Results.Json(new
            {
                name = "Drive-In Online gate",
                short_name = "Gate",
                description = "Check cars in at the gate, with or without a signal.",
                id = path,
                start_url = path,
                scope = path,
                display = "standalone",
                background_color = "#0a0d1f",
                theme_color = "#0a0d1f",
                icons = new[] { new { src = "/favicon.svg", sizes = "any", type = "image/svg+xml", purpose = "any" } },
            }, contentType: "application/manifest+json");
        });

        return app;
    }

    private sealed record OfflineSyncRequest(List<OfflineAdmission>? Admissions);
}
