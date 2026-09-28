// Clickable spots on a LotMap act as buttons: Space activates them (handled in LotMap.razor), so stop it also
// scrolling the page. Blazor can't cancel only some keys, and cancelling every key would break Tab.
document.addEventListener("keydown", e => {
    if (e.key === " " && e.target instanceof Element && e.target.closest(".lot-clickable"))
        e.preventDefault();
});
