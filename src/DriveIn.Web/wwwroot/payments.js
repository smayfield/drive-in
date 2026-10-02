// Card entry for checkout (CardFields.razor). The card number and security code never reach the server: they're turned
// into a payment method token here, in the browser, and only the token is sent.
//   Stripe: Stripe's Payment Element, in Stripe's own iframe, and stripe.createPaymentMethod. Stripe.js is only loaded
//           when a publishable key is given, so without one this module does nothing.
//   Test:   the test card form, whose inputs Blazor never reads. tokenizeTest checks the card and makes a fake token,
//           pm_test_{brand}_{last4}_{random}, that the dummy processor accepts.
// UNTESTED against Stripe (no account yet); see README → Payments.

const stripeScript = "https://js.stripe.com/v3/";
let stripeLoaded;

function loadStripeJs() {
    stripeLoaded ??= new Promise((resolve, reject) => {
        if (window.Stripe) {
            resolve();
            return;
        }
        const script = document.createElement("script");
        script.src = stripeScript;
        script.onload = () => resolve();
        script.onerror = () => {
            stripeLoaded = undefined; // let a later checkout try again
            reject(new Error("Couldn't load Stripe."));
        };
        document.head.appendChild(script);
    });
    return stripeLoaded;
}

// Stripe won't create an amount below 50 cents; the server never charges less than the total, so this only sizes the
// Element (it shows no amount).
const elementAmount = cents => Math.max(50, Math.round(cents));

export async function mountStripe(host, publishableKey, amountCents, currency) {
    if (!host || !publishableKey)
        return false;
    await loadStripeJs();
    const stripe = window.Stripe(publishableKey);
    const elements = stripe.elements({
        mode: "payment",
        amount: elementAmount(amountCents),
        currency,
        paymentMethodCreation: "manual",
        paymentMethodTypes: ["card"],
    });
    const element = elements.create("payment");
    element.mount(host);
    host._driveInStripe = { stripe, elements, element };
    return true;
}

export function updateStripe(host, amountCents) {
    host?._driveInStripe?.elements.update({ amount: elementAmount(amountCents) });
}

export async function tokenizeStripe(host) {
    const s = host?._driveInStripe;
    if (!s)
        return { error: "Card entry isn't ready yet. Wait a moment and try again." };
    const submitted = await s.elements.submit();
    if (submitted.error)
        return { error: submitted.error.message };
    const { error, paymentMethod } = await s.stripe.createPaymentMethod({ elements: s.elements });
    return error ? { error: error.message } : { id: paymentMethod.id };
}

export function unmountStripe(host) {
    host?._driveInStripe?.element.destroy();
    if (host)
        delete host._driveInStripe;
}

// --- Test card form (demo theaters and development: the dummy processor) ---

const digits = text => (text || "").replace(/\D/g, "");

function passesLuhn(number) {
    let sum = 0;
    for (let i = 0; i < number.length; i++) {
        let d = number.charCodeAt(number.length - 1 - i) - 48;
        if (i % 2 === 1 && (d *= 2) > 9)
            d -= 9;
        sum += d;
    }
    return number.length > 0 && sum % 10 === 0;
}

// Stripe's brand codes, which the server shows on receipts.
function brandOf(number) {
    const two = Number(number.slice(0, 2)), three = Number(number.slice(0, 3)), four = Number(number.slice(0, 4));
    if (number.startsWith("4")) return "visa";
    if (two === 34 || two === 37) return "amex";
    if ((two >= 51 && two <= 55) || (four >= 2221 && four <= 2720)) return "mastercard";
    if (number.startsWith("6011") || number.startsWith("65") || (three >= 644 && three <= 649)) return "discover";
    return "card";
}

// Letters only, so a token never has a long run of digits (the server refuses card-number-like runs).
function randomId() {
    const letters = "abcdefghijklmnopqrstuvwxyz";
    return Array.from(crypto.getRandomValues(new Uint8Array(16)), b => letters[b % letters.length]).join("");
}

// Reads the test card form inside host. Returns { id } or { error }.
export function tokenizeTest(host, now) {
    const field = name => host?.querySelector(`[data-card-field="${name}"]`)?.value ?? "";
    const number = digits(field("number"));
    if (number.length < 13 || number.length > 19 || !passesLuhn(number))
        return { error: "That card number isn't valid." };
    const expiry = field("expiry").match(/^\s*(\d{1,2})\s*\/\s*(\d{2}|\d{4})\s*$/);
    if (!expiry)
        return { error: "Enter the card's expiry as MM/YY." };
    const month = Number(expiry[1]);
    const year = expiry[2].length === 2 ? 2000 + Number(expiry[2]) : Number(expiry[2]);
    if (month < 1 || month > 12)
        return { error: "Enter the card's expiry as MM/YY." };
    // A card is good through the last day of its expiry month.
    const today = now ? new Date(now) : new Date();
    if (year * 12 + month < today.getUTCFullYear() * 12 + today.getUTCMonth() + 1)
        return { error: "That card has expired." };
    const brand = brandOf(number);
    const cvc = field("cvc").trim();
    if (!/^\d+$/.test(cvc) || cvc.length !== (brand === "amex" ? 4 : 3))
        return { error: "Enter the security code from the card." };
    // Stripe's "always declined" test number, so declines can be tried in demo theaters.
    const kind = number === "4000000000000002" ? "decline" : brand;
    return { id: `pm_test_${kind}_${number.slice(-4)}_${randomId()}` };
}
