using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using DriveIn.Web.Components.Account.Pages;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Manage = DriveIn.Web.Components.Account.Pages.Manage;

namespace DriveIn.Web.Tests;

public class RateLimitingTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static (ActionRateLimiter Limiter, FakeTimeProvider Time, MetricCollector<long> Rejected) NewLimiter(
        Action<RateLimitOptions>? configure = null)
    {
        var options = new RateLimitOptions();
        configure?.Invoke(options);
        var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var time = new FakeTimeProvider(Start);
        var limiter = new ActionRateLimiter(Options.Create(options), time, new DriveInMetrics(factory));
        return (limiter, time, new MetricCollector<long>(factory, DriveInMetrics.MeterName, "drivein.rate_limited"));
    }

    // --- ActionRateLimiter ---

    [Fact]
    public void Hits_past_the_limit_are_refused_until_the_window_ends_and_each_refusal_is_counted()
    {
        var (limiter, time, rejected) = NewLimiter(o => o.Messages = RateLimitRule.Of(3, TimeSpan.FromMinutes(1)));

        for (var i = 0; i < 3; i++)
            limiter.Hit(RateLimitPolicies.Messages, "user:a");
        var refused = Assert.Throws<AppValidationException>(() => limiter.Hit(RateLimitPolicies.Messages, "user:a"));
        Assert.Equal("Too many attempts. Please wait a minute and try again.", refused.Message);
        limiter.Hit(RateLimitPolicies.Messages, "user:b"); // someone else's count is their own

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Throws<AppValidationException>(() => limiter.Hit(RateLimitPolicies.Messages, "user:a"));
        time.Advance(TimeSpan.FromSeconds(1));
        limiter.Hit(RateLimitPolicies.Messages, "user:a");

        var counted = rejected.GetMeasurementSnapshot();
        Assert.Equal(2, counted.Count);
        Assert.All(counted, m => Assert.Equal(RateLimitPolicies.Messages, m.Tags["policy"]));
    }

    [Fact]
    public void Only_misses_count_towards_a_failure_limit_and_the_wait_is_described_in_minutes()
    {
        var (limiter, time, _) = NewLimiter(o => o.GiftCardMissesPerUser = RateLimitRule.Of(2, TimeSpan.FromMinutes(10)));
        const string policy = RateLimitPolicies.GiftCardMissesPerUser;

        for (var i = 0; i < 20; i++)
            limiter.Check(policy, "user:a"); // successful attempts never count
        limiter.Miss(policy, "user:a");
        limiter.Check(policy, "user:a");
        limiter.Miss(policy, "user:a");

        time.Advance(TimeSpan.FromMinutes(3));
        var refused = Assert.Throws<AppValidationException>(() => limiter.Check(policy, "user:a"));
        Assert.Equal("Too many attempts. Please wait 7 minutes and try again.", refused.Message);
        time.Advance(TimeSpan.FromMinutes(7));
        limiter.Check(policy, "user:a");
    }

    [Fact]
    public void Ended_windows_are_swept_from_memory()
    {
        var (limiter, time, _) = NewLimiter();
        for (var i = 0; i < 50; i++)
            limiter.Hit(RateLimitPolicies.PlaceSearch, $"user:{i}");
        Assert.Equal(50, limiter.TrackedCount);

        time.Advance(TimeSpan.FromMinutes(6));
        limiter.Hit(RateLimitPolicies.PlaceSearch, "user:new");

        Assert.Equal(1, limiter.TrackedCount);
    }

    [Fact]
    public void Every_policy_has_a_rule_and_the_defaults_are_the_documented_ones()
    {
        var options = new RateLimitOptions();
        foreach (var policy in typeof(RateLimitPolicies).GetFields().Select(f => (string)f.GetValue(null)!))
            Assert.True(options.For(policy).PermitLimit > 0, policy);
        Assert.Equal((10, 60), (options.Account.PermitLimit, options.Account.WindowSeconds));
        Assert.Equal((10, 60), (options.Messages.PermitLimit, options.Messages.WindowSeconds));
        Assert.Equal((20, 60), (options.PlaceSearch.PermitLimit, options.PlaceSearch.WindowSeconds));
        Assert.Equal((10, 600), (options.GiftCardMissesPerUser.PermitLimit, options.GiftCardMissesPerUser.WindowSeconds));
        Assert.Equal((100, 3600), (options.GiftCardMissesPerTheater.PermitLimit, options.GiftCardMissesPerTheater.WindowSeconds));
        Assert.Equal((30, 60), (options.GateCodeMisses.PermitLimit, options.GateCodeMisses.WindowSeconds));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.For("nope"));
    }

    // --- Endpoint rate limiting ---

    // A minimal app with the real rate limiter setup, and an endpoint and a page-like endpoint under the account policy.
    private static async Task<(WebApplication App, HttpClient Client, MetricCollector<long> Rejected)> StartAsync(int permitLimit)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<DriveInMetrics>();
        builder.Services.Configure<RateLimitOptions>(o => o.Account = RateLimitRule.Of(permitLimit, TimeSpan.FromMinutes(1)));
        builder.Services.AddAppRateLimiting();
        var app = builder.Build();
        app.UseRateLimiter();
        app.MapMethods("/Account/Login", ["GET", "POST"], () => "ok")
            .WithMetadata(new EnableRateLimitingAttribute(RateLimitPolicies.Account));
        app.MapPost("/open", () => "ok");
        await app.StartAsync();
        var rejected = new MetricCollector<long>(app.Services.GetRequiredService<IMeterFactory>(), DriveInMetrics.MeterName,
            "drivein.rate_limited");
        return (app, app.GetTestClient(), rejected);
    }

    [Fact]
    public async Task Account_posts_past_the_limit_get_a_429_page_with_retry_after_while_viewing_the_page_never_counts()
    {
        var (app, client, rejected) = await StartAsync(permitLimit: 3);
        await using var _ = app;

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Login")).StatusCode);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/Account/Login", null)).StatusCode);
        var refused = await client.PostAsync("/Account/Login", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("text/html; charset=utf-8", refused.Content.Headers.ContentType?.ToString());
        Assert.Contains("Too many attempts", await refused.Content.ReadAsStringAsync());
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/open", null)).StatusCode); // no policy, no limit
        var counted = Assert.Single(rejected.GetMeasurementSnapshot());
        Assert.Equal(RateLimitPolicies.Account, counted.Tags["policy"]);
    }

    [Theory]
    [InlineData("203.0.113.7", "ip:203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "ip:203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "ip:2001:db8:1:2::/64")]
    public void Signed_out_requests_are_partitioned_by_address_and_ipv6_by_its_64(string address, string expected)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        Assert.Equal(expected, HttpRateLimiting.PartitionKey(http));
    }

    [Fact]
    public void Signed_in_requests_are_partitioned_by_user()
    {
        var http = new DefaultHttpContext { User = Principals.Create("u1") };
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        Assert.Equal("user:u1", HttpRateLimiting.PartitionKey(http));
    }

    // The account pages get the policy from Components/Account/Pages/_Imports.razor (and Invite its own attribute); as
    // components' attributes, they become their endpoints' metadata.
    [Theory]
    [InlineData(typeof(Login))]
    [InlineData(typeof(Register))]
    [InlineData(typeof(ForgotPassword))]
    [InlineData(typeof(ResendEmailConfirmation))]
    [InlineData(typeof(ResetPassword))]
    [InlineData(typeof(LoginWith2fa))]
    [InlineData(typeof(LoginWithRecoveryCode))]
    [InlineData(typeof(ExternalLogin))]
    [InlineData(typeof(Manage.ChangePassword))]
    [InlineData(typeof(Manage.DeletePersonalData))]
    [InlineData(typeof(Components.Pages.Invite))]
    public void Account_pages_carry_the_account_rate_limit(Type page)
    {
        var attribute = page.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.Equal(RateLimitPolicies.Account, attribute?.PolicyName);
    }

    [Fact]
    public void The_refusal_page_names_the_wait()
    {
        Assert.Contains("Please wait 2 minutes and try again.", HttpRateLimiting.RejectedPage(ActionRateLimiter.Describe(TimeSpan.FromSeconds(61))));
    }
}
