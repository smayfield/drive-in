using System.Net;
using System.Text;
using DriveIn.Web.Data;
using QRCoder;

namespace DriveIn.Web.Services;

public static class TicketLinks
{
    // Where a ticket's QR code points: gate staff scan it to admit the car; the buyer sees their ticket.
    public static string Ticket(string baseUri, string code) => baseUri.TrimEnd('/') + "/tickets/" + Uri.EscapeDataString(code);

    // The ticket code from what gate staff type or paste: a bare code, or a ticket link (with any query or fragment).
    public static string CodeFrom(string? input)
    {
        var text = (input ?? "").Trim();
        text = text.Split('?', '#')[0].TrimEnd('/');
        // UnescapeDataString leaves malformed escapes (e.g. "%ZZ") as they are rather than throwing (see tests).
        return Uri.UnescapeDataString(text[(text.LastIndexOf('/') + 1)..]).Trim();
    }
}

public static class QrCodes
{
    public static byte[] Png(string text, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    public static string PngDataUri(string text, int pixelsPerModule = 8) =>
        "data:image/png;base64," + Convert.ToBase64String(Png(text, pixelsPerModule));
}

// The emailed receipt: what was bought, where and when, and the QR code to show at the gate.
public sealed record TicketReceipt(string Subject, string Html, IReadOnlyList<InlineImage> Images)
{
    public const string QrContentId = "ticket-qr";

    public static TicketReceipt Build(TicketView view, string baseUri)
    {
        var t = view.Ticket;
        var s = view.Showing;
        var link = TicketLinks.Ticket(baseUri, view.Code);
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

        var html = new StringBuilder();
        if (t.IsTest)
            html.Append("""<p style="background: #fff3cd; padding: 8px 12px; border-radius: 6px; font-family: Helvetica, Arial, sans-serif">Test ticket from a theater in demo mode: no money was charged and it won't admit a car once the theater goes live.</p>""");
        html.Append($"""
            <div style="font-family: Helvetica, Arial, sans-serif; max-width: 560px; color: #1a1a1a">
            <h1 style="font-size: 22px; margin: 0 0 4px">{E(view.Theater.Name)}</h1>
            <p style="margin: 0 0 16px; color: #555">{E(Address(view.Theater))}</p>
            <h2 style="font-size: 18px; margin: 0 0 4px">{E(s.Title)}</h2>
            <p style="margin: 0 0 4px"><strong>{s.StartsLocal:dddd, MMMM d, yyyy} at {s.StartsLocal:h:mm tt}</strong></p>
            <p style="margin: 0 0 16px">{E(s.ScreenName)} · Spot <strong style="font-size: 20px">{E(t.SpotLabel)}</strong>{(t.VehicleSize == VehicleSize.Large ? " (large vehicle)" : "")}</p>
            <p style="text-align: center; margin: 0 0 4px"><img src="cid:{QrContentId}" width="240" height="240" alt="Ticket QR code"></p>
            <p style="text-align: center; margin: 0 0 4px; font-size: 14px">Gate code</p>
            <p style="text-align: center; margin: 0 0 8px; font-size: 32px; font-weight: bold; letter-spacing: 6px; font-family: Menlo, Consolas, monospace">{E(t.ShortCode)}</p>
            <p style="text-align: center; margin: 0 0 16px; color: #555; font-size: 13px">
              Show the QR code at the gate, or tell the attendant your gate code. <a href="{E(link)}">View your ticket</a>
            </p>
            <table style="width: 100%; border-collapse: collapse; font-size: 14px">
              <tr><td style="padding: 4px 0">{E(t.OptionName)}</td><td style="text-align: right">{Money.Format(t.OptionPrice)}</td></tr>
            """);
        foreach (var a in t.AddOns)
            html.Append($"""<tr><td style="padding: 4px 0">{E(a.Name)}</td><td style="text-align: right">{SignedMoney(a.Effect)}</td></tr>""");
        html.Append($"""
              <tr><td style="padding: 6px 0; border-top: 1px solid #ccc"><strong>Total</strong></td>
                  <td style="text-align: right; border-top: 1px solid #ccc"><strong>{Money.Format(t.Total)}</strong></td></tr>
            """);
        if (t.GiftCardAmount > 0)
            html.Append($"""
              <tr><td style="padding: 4px 0">Gift card ending {E(t.GiftCardLast4)}</td><td style="text-align: right">{SignedMoney(-t.GiftCardAmount)}</td></tr>
              <tr><td style="padding: 4px 0">Charged to card</td><td style="text-align: right">{Money.Format(t.CardAmount)}</td></tr>
            """);
        html.Append("""
            </table>
            <p style="font-size: 13px; color: #555">{E(t.IsComp ? $"Free admission for {t.GuestName}" : PaidWith(t))} · {(t.IsComp ? "Issued" : "Purchased")} {view.SoldLocal:MMM d, yyyy h:mm tt}</p>
            <p style="font-size: 13px; color: #555">
              This ticket admits one car to this showing only. It can't be used for another showing or on a later date.{(t.IsComp ? "" : " All sales are final: no refunds or exchanges for any reason, including bad weather.")}
            </p>
            </div>
            """);

        return new TicketReceipt(
            $"{(t.IsTest ? "[TEST] " : "")}Your ticket: {s.Title} at {view.Theater.Name}, {s.StartsLocal:ddd MMM d}, spot {t.SpotLabel}",
            html.ToString(),
            [new InlineImage(QrContentId, "ticket.png", "image/png", QrCodes.Png(link))]);
    }

    public static string PaidWith(Ticket t)
    {
        if (t.Total == 0)
            return "No payment needed";
        var card = t.SoldAtGate ? "card at the gate" : $"{t.CardBrand} ending {t.CardLast4}";
        if (t.GiftCardAmount <= 0)
            return t.SoldAtGate ? "Paid by card at the gate" : $"Paid with {card}";
        var gift = $"gift card ending {t.GiftCardLast4}";
        return t.CardAmount > 0
            ? $"Paid with {gift} ({Money.Format(t.GiftCardAmount)}) and {card} ({Money.Format(t.CardAmount)})"
            : $"Paid with {gift}";
    }

    public static string SignedMoney(decimal amount) => amount < 0 ? "−" + Money.Format(-amount) : "+" + Money.Format(amount);

    public static string Address(Theater t) =>
        string.Join(", ", new[] { t.AddressLine1, t.AddressLine2, t.City, t.State }.Where(p => !string.IsNullOrWhiteSpace(p)));
}
