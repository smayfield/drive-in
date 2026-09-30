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

    public static string Build(IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', header.Select(Cell)).Append("\r\n");
        foreach (var row in rows)
            sb.AppendJoin(',', row.Select(Cell)).Append("\r\n");
        return sb.ToString();
    }

    public static byte[] ToBytes(string csv) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();

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

    private static string Neutralize(string s) => s.Length > 0 && "=+-@\t\r".Contains(s[0]) ? "'" + s : s;
}
