using System.Globalization;
using System.Net;
using System.Text;
using DriveIn.Web.Data;

namespace DriveIn.Web.Services;

public sealed record BillingEmail(string Subject, string Html);

// The emailed invoice, receipt and void notice, sent to the subscription's bill-to address. Laid out like the gift card
// and ticket emails: inline styles, no images. The link is to the invoice on the theater's Billing page.
public static class BillingEmails
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static BillingEmail Invoice(Invoice invoice, CompanyOptions company, string link)
    {
        var html = new StringBuilder();
        Open(html, company, $"Invoice {invoice.DisplayNumber}");
        html.Append($"""<p>Here's the {E(Period(invoice))} invoice for {E(invoice.TheaterName)}.</p>""");
        Details(html, invoice);
        Lines(html, invoice);
        if (invoice.Status == InvoiceStatus.Paid)
            html.Append("""<p style="font-weight: bold">Nothing to pay: this invoice is settled.</p>""");
        else
            html.Append($"""<p style="font-size: 18px; font-weight: bold">Amount due: {Money.Format(invoice.Balance)}{Due(invoice)}</p>""");
        html.Append($"""<p style="font-size: 14px">To pay, or with any questions, reply to this email or write to {E(company.Email)}. Quote the invoice number with your payment.</p>""");
        Close(html, link);
        return new BillingEmail($"Invoice {invoice.DisplayNumber} for {invoice.TheaterName} ({Period(invoice)})", html.ToString());
    }

    public static BillingEmail Receipt(Invoice invoice, InvoicePayment payment, CompanyOptions company, string link)
    {
        var html = new StringBuilder();
        Open(html, company, "Payment receipt");
        html.Append($"""<p>Thank you. We received {Money.Format(payment.Amount)} toward invoice {E(invoice.DisplayNumber)} for {E(invoice.TheaterName)}.</p>""");
        html.Append("""<table style="border-collapse: collapse; font-size: 14px; margin: 0 0 16px">""");
        Row(html, "Received", payment.ReceivedOn.ToString("MMM d, yyyy", Culture));
        Row(html, "Method", PaymentMethods.Describe(payment.Method) + (payment.Reference is { Length: > 0 } r ? $" ({r})" : ""));
        Row(html, "Invoice", $"{invoice.DisplayNumber} · {Period(invoice)}");
        Row(html, "Invoice total", Money.Format(invoice.Total));
        Row(html, "Paid to date", Money.Format(invoice.AmountPaid));
        Row(html, "Balance", Money.Format(invoice.Balance));
        html.Append("</table>");
        html.Append(invoice.Balance == 0
            ? """<p style="font-weight: bold">This invoice is paid in full.</p>"""
            : $"""<p style="font-weight: bold">{Money.Format(invoice.Balance)} is still due{Due(invoice)}.</p>""");
        Close(html, link);
        return new BillingEmail($"Receipt for invoice {invoice.DisplayNumber} ({invoice.TheaterName})", html.ToString());
    }

    public static BillingEmail Voided(Invoice invoice, CompanyOptions company, string link)
    {
        var html = new StringBuilder();
        Open(html, company, $"Invoice {invoice.DisplayNumber} is void");
        html.Append($"""<p>We've cancelled invoice {E(invoice.DisplayNumber)} ({E(Period(invoice))}, {Money.Format(invoice.Total)}) for {E(invoice.TheaterName)}. You don't need to pay it.</p>""");
        if (invoice.VoidReason is { Length: > 0 } reason)
            html.Append($"""<p style="white-space: pre-line">{E(reason)}</p>""");
        html.Append($"""<p style="font-size: 14px">Questions? Reply to this email or write to {E(company.Email)}.</p>""");
        Close(html, link);
        return new BillingEmail($"Invoice {invoice.DisplayNumber} for {invoice.TheaterName} is void", html.ToString());
    }

    public static string Period(Invoice invoice) => invoice.PeriodMonth.ToString("MMMM yyyy", Culture);

    private static void Open(StringBuilder html, CompanyOptions company, string title) => html.Append($"""
        <div style="font-family: Helvetica, Arial, sans-serif; max-width: 600px; color: #1a1a1a">
        <p style="margin: 0; font-weight: bold">{E(company.Name)}</p>
        <p style="margin: 0 0 16px; color: #555; font-size: 13px; white-space: pre-line">{E(company.Address)}</p>
        <h1 style="font-size: 22px; margin: 0 0 12px">{E(title)}</h1>
        """);

    private static void Close(StringBuilder html, string link) => html.Append($"""
        <p style="font-size: 14px"><a href="{E(link)}">See it on your theater's Billing page</a>.</p>
        </div>
        """);

    private static void Details(StringBuilder html, Invoice invoice)
    {
        html.Append("""<table style="border-collapse: collapse; font-size: 14px; margin: 0 0 16px">""");
        Row(html, "Invoice", invoice.DisplayNumber);
        if (invoice.IssuedAt is DateTimeOffset issued)
            Row(html, "Issued", issued.UtcDateTime.ToString("MMM d, yyyy", Culture));
        if (invoice.DueOn is DateOnly due)
            Row(html, "Due", due.ToString("MMM d, yyyy", Culture));
        Row(html, "Period", Period(invoice));
        Row(html, "Bill to", string.Join(" · ", new[] { invoice.BillToName, invoice.TheaterName, invoice.TheaterAddress }.Where(s => !string.IsNullOrWhiteSpace(s))));
        html.Append("</table>");
    }

    private static void Lines(StringBuilder html, Invoice invoice)
    {
        const string cell = "padding: 6px 8px; border-bottom: 1px solid #ddd";
        html.Append($"""<table style="border-collapse: collapse; font-size: 14px; width: 100%; margin: 0 0 16px"><tr><th style="{cell}; text-align: left">Description</th><th style="{cell}; text-align: right">Qty</th><th style="{cell}; text-align: right">Price</th><th style="{cell}; text-align: right">Amount</th></tr>""");
        foreach (var line in invoice.Lines.OrderBy(l => l.SortOrder))
            html.Append($"""<tr><td style="{cell}">{E(line.Description)}</td><td style="{cell}; text-align: right">{line.Quantity}</td><td style="{cell}; text-align: right">{Money.Format(line.UnitPrice)}</td><td style="{cell}; text-align: right">{Money.Format(line.Amount)}</td></tr>""");
        html.Append($"""<tr><td colspan="3" style="{cell}; text-align: right; font-weight: bold">Total</td><td style="{cell}; text-align: right; font-weight: bold">{Money.Format(invoice.Total)}</td></tr>""");
        if (invoice.AmountPaid != 0)
        {
            html.Append($"""<tr><td colspan="3" style="{cell}; text-align: right">Paid</td><td style="{cell}; text-align: right">{Money.Format(-invoice.AmountPaid)}</td></tr>""");
            html.Append($"""<tr><td colspan="3" style="{cell}; text-align: right; font-weight: bold">Balance</td><td style="{cell}; text-align: right; font-weight: bold">{Money.Format(invoice.Balance)}</td></tr>""");
        }
        html.Append("</table>");
    }

    private static void Row(StringBuilder html, string label, string value) =>
        html.Append($"""<tr><td style="padding: 2px 16px 2px 0; color: #555">{E(label)}</td><td style="padding: 2px 0">{E(value)}</td></tr>""");

    private static string Due(Invoice invoice) => invoice.DueOn is DateOnly due ? $" by {due.ToString("MMM d, yyyy", Culture)}" : "";

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
}
