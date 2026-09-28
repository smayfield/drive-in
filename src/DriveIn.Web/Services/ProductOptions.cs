using System.Globalization;

namespace DriveIn.Web.Services;

// What the pricing page and plan estimates show. Unset prices are shown as placeholders ("$__").
public sealed class PlanOptions
{
    public const string Section = "Plans";

    // The Standard plan, per screen for each calendar month the theater's season is open. No per-ticket fees.
    public decimal? PricePerScreenPerMonth { get; set; }

    // Self-service sign-up limit, so one account can't create an unbounded number of demo theaters.
    public int MaxTheatersPerOwner { get; set; } = 3;

    public string PriceDisplay => PricePerScreenPerMonth is decimal p
        ? p.ToString("C0", CultureInfo.GetCultureInfo("en-US"))
        : "$__";
}

// The provider named in the Terms of Service, Privacy Policy and license. Until LegalName is set, the legal pages
// show placeholders and a banner saying they're drafts and not yet in effect.
public sealed class CompanyOptions
{
    public const string Section = "Company";

    public string? LegalName { get; set; }
    public string? MailingAddress { get; set; }
    // The state whose law governs the terms, e.g. "Texas".
    public string? GoverningState { get; set; }
    public string? ContactEmail { get; set; }
    public DateOnly? TermsEffectiveDate { get; set; }

    // Recorded on each theater when its owner accepts the terms at sign-up. Change it when the terms change.
    public string TermsVersion { get; set; } = "draft-2026-09-28";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(LegalName);

    public string Name => Or(LegalName, "[Company legal name]");
    public string Address => Or(MailingAddress, "[Mailing address]");
    public string State => Or(GoverningState, "[State]");
    public string Email => Or(ContactEmail, "[contact email]");
    public string EffectiveDate => TermsEffectiveDate?.ToString("MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US")) ?? "[effective date]";

    private static string Or(string? value, string placeholder) => string.IsNullOrWhiteSpace(value) ? placeholder : value.Trim();
}
