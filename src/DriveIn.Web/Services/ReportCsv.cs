using System.Globalization;
using System.Text;

namespace DriveIn.Web.Services;

// Reports as CSV for spreadsheets and bookkeeping: RFC 4180 quoting, invariant numbers, and a UTF-8 byte order mark so
// Excel reads accents correctly. Text that a spreadsheet would run as a formula (buyer emails and names are typed by
// the public) is prefixed with an apostrophe.
public static class ReportCsv
{
    public static readonly IReadOnlyList<string> Kinds = ["showings", "days", "films", "giftcards"];

    public static string Showings(SalesReport report) => Build(
        ["Starts", "Screen", "Films", "Capacity", "Cars", "Free", "Admitted", "No-shows", "Gross", "Paid by gift card", "Paid by card"],
        report.Showings.Select(r => new object?[]
        {
            r.StartsLocal, r.Screen, r.Films, r.Capacity, r.Cars, r.Free, r.Admitted, r.NoShows, r.Gross, r.GiftCardPaid,
            r.Gross - r.GiftCardPaid,
        }));

    public static string Days(SalesReport report) => Build(
        ["Date", "Showings", "Cars", "Admitted", "Gross"],
        report.Days.Select(r => new object?[] { r.Date, r.Showings, r.Cars, r.Admitted, r.Gross }));

    public static string Films(SalesReport report) => Build(
        ["Film", "Showings", "Cars", "Admitted", "Gross"],
        report.Films.Select(r => new object?[] { r.Title, r.Showings, r.Cars, r.Admitted, r.Gross }));

    public static string GiftCards(GiftCardReport report) => Build(
        ["Card", "Bought", "Bought by", "For", "Value", "Balance", "Last used", "Test"],
        report.Outstanding.Select(r => new object?[]
        {
            "…" + r.Last4, r.PurchasedLocal, r.PurchaserEmail, r.RecipientName, r.InitialAmount, r.Balance, r.LastUsedLocal,
            r.IsTest ? "yes" : "",
        }));

    // Admin billing (BillingReportService), at /admin/billing/{kind}.csv.
    public static readonly IReadOnlyList<string> BillingKinds = ["invoices", "payments", "aging"];

    public static string BillingInvoices(BillingReport report) => Build(
        ["Invoice", "Theater", "Period", "Issued", "Due", "Status", "Total", "Paid", "Balance"],
        report.Invoices.Select(i => new object?[]
        {
            i.DisplayNumber, i.TheaterName, i.PeriodMonth, i.IssuedAt is DateTimeOffset at ? DateOnly.FromDateTime(at.UtcDateTime) : null,
            i.DueOn, i.Status.ToString(), i.Total, i.AmountPaid, i.Balance,
        }));

    public static string BillingPayments(BillingReport report) => Build(
        ["Received", "Invoice", "Theater", "Period", "Method", "Reference", "Amount"],
        report.Payments.Select(p => new object?[]
        {
            p.Payment.ReceivedOn, p.Invoice.DisplayNumber, p.Invoice.TheaterName, p.Invoice.PeriodMonth,
            Data.PaymentMethods.Describe(p.Payment.Method), p.Payment.Reference, p.Payment.Amount,
        }));

    public static string BillingAging(BillingReport report) => Build(
        ["Theater", "Current", "1-30 days", "31-60 days", "61-90 days", "Over 90 days", "Total"],
        report.Aging.Append(report.AgingTotal).Select(a => new object?[]
        {
            a.TheaterName, a.Current, a.Days1To30, a.Days31To60, a.Days61To90, a.Over90, a.Total,
        }));

    public static string Build(IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', header.Select(Cell)).Append("\r\n");
        foreach (var row in rows)
            sb.AppendJoin(',', row.Select(Cell)).Append("\r\n");
        return sb.ToString();
    }

    // The byte order mark then the text, written into one buffer.
    public static byte[] ToBytes(string csv)
    {
        var bom = Encoding.UTF8.Preamble;
        var bytes = new byte[bom.Length + Encoding.UTF8.GetByteCount(csv)];
        bom.CopyTo(bytes);
        Encoding.UTF8.GetBytes(csv, bytes.AsSpan(bom.Length));
        return bytes;
    }

    public static string Cell(object? value)
    {
        var text = value switch
        {
            null => "",
            decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
            DateTime t => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            string s => Neutralize(s),
            _ => Neutralize(value.ToString() ?? ""),
        };
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    // Spreadsheets skip leading whitespace before deciding a cell is a formula, so look past it.
    private static string Neutralize(string s)
    {
        var first = s.TrimStart(' ', ' ');
        return first.Length > 0 && "=+-@\t\r\n".Contains(first[0]) ? "'" + s : s;
    }
}
