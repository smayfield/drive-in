using System.Net;
using DriveIn.Web.Data;

namespace DriveIn.Web.Services;

// The emailed gift card: its code and value, and how to spend it. Sent to the buyer, and to the recipient when the buyer
// gave an address (with the buyer's message).
public sealed record GiftCardEmail(string Subject, string Html)
{
    public static GiftCardEmail Build(GiftCard card, Theater theater, string baseUri, string? fromName, bool forRecipient)
    {
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        var link = baseUri.TrimEnd('/') + "/theaters/" + Uri.EscapeDataString(theater.Slug);
        var html = new System.Text.StringBuilder();
        if (card.IsTest)
            html.Append("""<p style="background: #fff3cd; padding: 8px 12px; border-radius: 6px; font-family: Helvetica, Arial, sans-serif">Test gift card from a theater in demo mode: no money was charged and it stops working once the theater goes live.</p>""");
        html.Append($"""
            <div style="font-family: Helvetica, Arial, sans-serif; max-width: 560px; color: #1a1a1a">
            <h1 style="font-size: 22px; margin: 0 0 4px">{E(theater.Name)} gift card</h1>
            <p style="margin: 0 0 16px; color: #555">{E(TicketReceipt.Address(theater))}</p>
            """);
        if (forRecipient)
        {
            html.Append($"<p>{E(card.RecipientName is { Length: > 0 } n ? $"Hi {n}," : "Hi,")} {E(fromName is { Length: > 0 } f ? f : "someone")} sent you a gift card.</p>");
            if (card.Message is { Length: > 0 } m)
                html.Append($"""<blockquote style="margin: 0 0 16px; padding-left: 12px; border-left: 3px solid #ccc; white-space: pre-line">{E(m)}</blockquote>""");
        }
        html.Append($"""
            <p style="text-align: center; margin: 0 0 4px; font-size: 14px">Value</p>
            <p style="text-align: center; margin: 0 0 12px; font-size: 28px; font-weight: bold">{Money.Format(card.InitialAmount)}</p>
            <p style="text-align: center; margin: 0 0 4px; font-size: 14px">Gift card code</p>
            <p style="text-align: center; margin: 0 0 16px; font-size: 24px; font-weight: bold; letter-spacing: 3px; font-family: Menlo, Consolas, monospace">{E(GiftCardCodes.Format(card.Code))}</p>
            <p style="font-size: 14px">Enter the code when paying for tickets at <a href="{E(link)}">{E(theater.Name)}</a>, or show it at the gate. It pays toward the ticket, and whatever is left stays on the card for next time.</p>
            {(forRecipient ? $"""<p style="font-size: 14px">It's yours to use: you don't need the buyer's account. Sign in with this email address and it's listed under My tickets too.</p>""" : "")}
            <p style="font-size: 13px; color: #555">Good only at {E(theater.Name)}. It doesn't expire, can't be exchanged for cash and can't be refunded. Anyone with the code can spend it, so keep it safe.</p>
            </div>
            """);
        return new GiftCardEmail(
            forRecipient ? $"{(card.IsTest ? "[TEST] " : "")}A {theater.Name} gift card for you" : $"{(card.IsTest ? "[TEST] " : "")}Your {theater.Name} gift card",
            html.ToString());
    }
}
