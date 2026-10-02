using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;
using DriveIn.Web.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DriveIn.Web.Services;

// One limit: at most PermitLimit attempts per fixed window of WindowSeconds.
public sealed class RateLimitRule
{
    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; }

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);

    public static RateLimitRule Of(int permitLimit, TimeSpan window) =>
        new() { PermitLimit = permitLimit, WindowSeconds = (int)window.TotalSeconds };
}

// The limits (config section RateLimits). Each is per signed-in user, or per client IP (an IPv6 /64) when signed out,
// except GiftCardMissesPerTheater.
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    // POSTs to the sign-in, registration and recovery pages (and the passkey and external-login endpoints).
    public RateLimitRule Account { get; set; } = RateLimitRule.Of(10, TimeSpan.FromMinutes(1));

    // Messages sent: new conversations and replies.
    public RateLimitRule Messages { get; set; } = RateLimitRule.Of(10, TimeSpan.FromMinutes(1));

    // "Theaters near" searches by place name (each may be a geocoder lookup).
    public RateLimitRule PlaceSearch { get; set; } = RateLimitRule.Of(20, TimeSpan.FromMinutes(1));

    // Gift card codes that matched nothing, at checkout or at the gate.
    public RateLimitRule GiftCardMissesPerUser { get; set; } = RateLimitRule.Of(10, TimeSpan.FromMinutes(10));

    // The same, counted across everyone at one theater: a ceiling on guessing spread over many accounts. High enough
    // that typos on a busy night never reach it.
    public RateLimitRule GiftCardMissesPerTheater { get; set; } = RateLimitRule.Of(100, TimeSpan.FromHours(1));

    // Gate codes (and ticket links) that matched no ticket.
    public RateLimitRule GateCodeMisses { get; set; } = RateLimitRule.Of(30, TimeSpan.FromMinutes(1));

    public RateLimitRule For(string policy) => policy switch
    {
        RateLimitPolicies.Account => Account,
        RateLimitPolicies.Messages => Messages,
        RateLimitPolicies.PlaceSearch => PlaceSearch,
        RateLimitPolicies.GiftCardMissesPerUser => GiftCardMissesPerUser,
        RateLimitPolicies.GiftCardMissesPerTheater => GiftCardMissesPerTheater,
        RateLimitPolicies.GateCodeMisses => GateCodeMisses,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown rate limit policy."),
    };
}

// Policy names: endpoint policies (EnableRateLimiting) and ActionRateLimiter's, and the "policy" tag of
// drivein.rate_limited.
public static class RateLimitPolicies
{
    public const string Account = "account";
    public const string Messages = "messages";
    public const string PlaceSearch = "place_search";
    public const string GiftCardMissesPerUser = "gift_card_misses_user";
    public const string GiftCardMissesPerTheater = "gift_card_misses_theater";
    public const string GateCodeMisses = "gate_code_misses";
}

// Limits for actions taken in an interactive circuit, which endpoint rate limiting never sees (they arrive over the
// circuit's one WebSocket). Services call it: Hit counts every attempt; Check before an attempt plus Miss after a
// failed one count only failures (e.g. wrong codes, so a busy gate never trips it). Over the limit, it throws
// AppValidationException, which pages show as an alert. Fixed windows on the app's clock, held in memory: like
// SpotEvents this assumes one server, and limits reset when the app restarts.
public sealed class ActionRateLimiter(IOptions<RateLimitOptions> options, TimeProvider time, DriveInMetrics metrics)
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(string Policy, string Key), Window> windows = new();
    private DateTimeOffset lastSweep = time.GetUtcNow();

    private sealed class Window(DateTimeOffset start, TimeSpan length)
    {
        public DateTimeOffset Start = start;
        public TimeSpan Length = length;
        public int Count;

        public DateTimeOffset End => Start + Length;
    }

    // Whose attempts these are: the signed-in user, else everyone signed out together (circuits don't carry a
    // reliable client IP; signed-out callers are rare, since most of these actions need an account).
    public static string KeyFor(ClaimsPrincipal user) => user.GetUserId() is string id ? KeyForUser(id) : "anonymous";

    public static string KeyForUser(string userId) => "user:" + userId;

    public static string KeyForTheater(int theaterId) => "theater:" + theaterId;

    // Counts an attempt, refusing it if the limit is already used up.
    public void Hit(string policy, string key)
    {
        var rule = options.Value.For(policy);
        var now = time.GetUtcNow();
        var window = Current(policy, key, rule, now);
        lock (window)
        {
            if (window.Count >= rule.PermitLimit)
                Reject(policy, window.End - now);
            window.Count++;
        }
        SweepIfDue(now);
    }

    // Refuses an attempt if this key's failures have used up the limit; counts nothing.
    public void Check(string policy, string key)
    {
        var rule = options.Value.For(policy);
        var now = time.GetUtcNow();
        if (windows.TryGetValue((policy, key), out var window))
        {
            lock (window)
            {
                if (now < window.End && window.Count >= rule.PermitLimit)
                    Reject(policy, window.End - now);
            }
        }
    }

    // Counts a failed attempt.
    public void Miss(string policy, string key)
    {
        var rule = options.Value.For(policy);
        var now = time.GetUtcNow();
        var window = Current(policy, key, rule, now);
        lock (window)
            window.Count++;
        SweepIfDue(now);
    }

    private Window Current(string policy, string key, RateLimitRule rule, DateTimeOffset now)
    {
        var window = windows.GetOrAdd((policy, key), _ => new Window(now, rule.Window));
        lock (window)
        {
            if (now >= window.End)
                (window.Start, window.Length, window.Count) = (now, rule.Window, 0);
        }
        return window;
    }

    private void Reject(string policy, TimeSpan retryAfter)
    {
        metrics.RateLimited(policy);
        throw new AppValidationException($"Too many attempts. Please wait {Describe(retryAfter)} and try again.");
    }

    internal static string Describe(TimeSpan wait)
    {
        var minutes = (int)Math.Ceiling(wait.TotalMinutes);
        return minutes <= 1 ? "a minute" : $"{minutes} minutes";
    }

    // Drops windows that have ended, so keys seen once don't stay in memory.
    private void SweepIfDue(DateTimeOffset now)
    {
        if (now - lastSweep < SweepEvery)
            return;
        lastSweep = now;
        foreach (var (k, window) in windows)
        {
            if (now >= window.End)
                windows.TryRemove(new KeyValuePair<(string, string), Window>(k, window));
        }
    }

    internal int TrackedCount => windows.Count;
}

// Endpoint rate limiting (ASP.NET Core's limiter) for requests that reach the server as HTTP: the Identity pages' form
// posts (the pages carry [EnableRateLimiting(RateLimitPolicies.Account)]) and the account endpoints. Runs after
// UseForwardedHeaders and UseAuthentication, so it sees the client's real address and who's signed in.
public static class HttpRateLimiting
{
    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // On a page, only its form posts count: viewing the sign-in page, or coming back to it, never does.
            o.AddPolicy(RateLimitPolicies.Account, http => HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
                ? RateLimitPartition.GetNoLimiter("")
                : FixedWindow(http, http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value.Account));
            o.OnRejected = OnRejectedAsync;
        });

    private static RateLimitPartition<string> FixedWindow(HttpContext http, RateLimitRule rule) =>
        RateLimitPartition.GetFixedWindowLimiter(PartitionKey(http), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rule.PermitLimit,
            Window = rule.Window,
            QueueLimit = 0,
        });

    // The signed-in user, else the client's address; IPv6 by its /64, since one machine usually has a whole one.
    internal static string PartitionKey(HttpContext http)
    {
        if (http.User.GetUserId() is string userId)
            return "user:" + userId;
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null)
            return "ip:unknown";
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return $"ip:{new IPAddress(bytes)}/64";
        }
        return "ip:" + ip;
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "unknown";
        http.RequestServices.GetRequiredService<DriveInMetrics>().RateLimited(policy);
        var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : TimeSpan.FromMinutes(1);
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers.CacheControl = "no-store";
        // A whole page, not a re-executed one: the status code pages would show "not found", and a form post that was
        // refused shouldn't run a component (or its antiforgery check) again.
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.WriteAsync(RejectedPage(ActionRateLimiter.Describe(wait)), ct);
    }

    internal static string RejectedPage(string wait) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <meta name="color-scheme" content="light dark" />
            <title>Too many attempts · Drive-In Online</title>
            <style>
                body { font-family: system-ui, sans-serif; max-width: 36rem; margin: 4rem auto; padding: 0 1rem; line-height: 1.5; }
            </style>
        </head>
        <body>
            <main>
                <h1>Too many attempts</h1>
                <p>That was tried too many times in a short while. Please wait {{wait}} and try again.</p>
                <p><a href="/">Back to Drive-In Online</a></p>
            </main>
        </body>
        </html>
        """;
}
