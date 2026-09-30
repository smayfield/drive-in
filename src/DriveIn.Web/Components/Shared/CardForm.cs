using DriveIn.Web.Services;

namespace DriveIn.Web.Components.Shared;

// What a buyer has typed into CardFields. It goes to the payment processor and is never stored.
public sealed class CardForm
{
    public string Name { get; set; } = "";
    public string Number { get; set; } = "";
    public int ExpiryMonth { get; set; }
    public int ExpiryYear { get; set; }
    public string Cvc { get; set; } = "";

    public CardInput ToInput() => new(Name, Number, ExpiryMonth, ExpiryYear, Cvc);

    // After a charge, the number and code shouldn't linger in the page's state.
    public void Clear() => Number = Cvc = "";
}
