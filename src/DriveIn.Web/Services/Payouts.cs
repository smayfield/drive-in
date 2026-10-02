using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// A theater's payout account at the payment processor: where its ticket and gift card money goes. With Stripe it's a
// Connect (Express) account the owner creates through Stripe's hosted onboarding; Stripe collects the business and bank
// details and does the identity checks, so none of that is stored here.
public interface IPayoutAccounts
{
    // False when payments don't go through a processor that pays theaters (dummy, or none): nothing to set up.
    bool IsAvailable { get; }

    Task<string> CreateAccountAsync(Theater theater, string? email, CancellationToken ct = default);

    // A one-time link to the processor's onboarding for the account. returnUrl is where it sends the owner when they're
    // done (or leave); refreshUrl when the link has expired and a new one is needed.
    Task<string> CreateOnboardingLinkAsync(string accountId, string returnUrl, string refreshUrl, CancellationToken ct = default);

    Task<PayoutStatus> GetStatusAsync(string accountId, CancellationToken ct = default);
}

public sealed class UnavailablePayoutAccounts : IPayoutAccounts
{
    public bool IsAvailable => false;

    public Task<string> CreateAccountAsync(Theater theater, string? email, CancellationToken ct = default) =>
        throw new AppValidationException("Payouts aren't set up on this site yet.");

    public Task<string> CreateOnboardingLinkAsync(string accountId, string returnUrl, string refreshUrl, CancellationToken ct = default) =>
        throw new AppValidationException("Payouts aren't set up on this site yet.");

    public Task<PayoutStatus> GetStatusAsync(string accountId, CancellationToken ct = default) => Task.FromResult(PayoutStatus.None);
}

// For development (Payments:Provider = Dummy): an "account" that onboarding finishes at once, so the whole flow can be
// tried without a processor. The onboarding link goes straight back to the return page.
public sealed class DummyPayoutAccounts : IPayoutAccounts
{
    public bool IsAvailable => true;

    public Task<string> CreateAccountAsync(Theater theater, string? email, CancellationToken ct = default) =>
        Task.FromResult($"acct_test_{Guid.NewGuid():N}"[..26]);

    public Task<string> CreateOnboardingLinkAsync(string accountId, string returnUrl, string refreshUrl, CancellationToken ct = default) =>
        Task.FromResult(returnUrl);

    public Task<PayoutStatus> GetStatusAsync(string accountId, CancellationToken ct = default) =>
        Task.FromResult(accountId.StartsWith("acct_test_", StringComparison.Ordinal) ? PayoutStatus.Enabled : PayoutStatus.None);
}

public sealed record PayoutView(Theater Theater, bool Available, bool RequiredToSell);

// Manage → Payouts. Requires ManagePayouts (owner-only by default, like billing).
public sealed class PayoutService(IDbContextFactory<ApplicationDbContext> dbFactory, IAuthorizationService auth,
    IPayoutAccounts accounts, IPaymentProcessor payments, TimeProvider time, ILogger<PayoutService> logger)
{
    public async Task<PayoutView> GetAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindAsync(db, user, theaterId);
        return new PayoutView(theater, accounts.IsAvailable, payments.RequiresPayoutAccount);
    }

    // Creates the theater's account if it has none, and returns a link to the processor's onboarding for it. The owner
    // comes back to manage/{id}/payouts, which checks the account's status (RefreshAsync).
    public async Task<string> StartOnboardingAsync(ClaimsPrincipal user, int theaterId, string baseUri)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindAsync(db, user, theaterId, tracking: true);
        if (!accounts.IsAvailable)
            throw new AppValidationException("Payouts aren't set up on this site yet.");
        if (theater.PayoutAccountId is null)
        {
            var email = await db.Users.Where(u => u.Id == theater.OwnerId).Select(u => u.Email).FirstOrDefaultAsync();
            theater.PayoutAccountId = await accounts.CreateAccountAsync(theater, email);
            theater.PayoutStatus = PayoutStatus.Pending;
            theater.PayoutStatusCheckedAt = time.GetUtcNow();
            await db.SaveChangesAsync();
            logger.LogInformation("Created payout account {AccountId} for theater {TheaterId}", theater.PayoutAccountId, theater.Id);
        }
        var page = $"{baseUri.TrimEnd('/')}/manage/{theater.Id}/payouts";
        return await accounts.CreateOnboardingLinkAsync(theater.PayoutAccountId, $"{page}?done=1", $"{page}?expired=1");
    }

    // Asks the processor whether the account can take charges and receive payouts yet, and saves the answer.
    public async Task<Theater> RefreshAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindAsync(db, user, theaterId, tracking: true);
        if (theater.PayoutAccountId is not null && accounts.IsAvailable)
        {
            theater.PayoutStatus = await accounts.GetStatusAsync(theater.PayoutAccountId);
            theater.PayoutStatusCheckedAt = time.GetUtcNow();
            await db.SaveChangesAsync();
        }
        return theater;
    }

    private async Task<Theater> FindAsync(ApplicationDbContext db, ClaimsPrincipal user, int theaterId, bool tracking = false)
    {
        var theaters = tracking ? db.Theaters : db.Theaters.AsNoTracking();
        var theater = await theaters.FirstOrDefaultAsync(t => t.Id == theaterId) ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.ManagePayouts);
        return theater;
    }
}
