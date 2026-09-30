// Follows the visitor's OS/browser light or dark setting. Runs in <head>, before first paint.
// - data-bs-theme themes Bootstrap; color-scheme themes native controls and scrollbars.
// - The di-scheme cookie lets the server render the right MudBlazor palette on the first paint next time.
(function () {
  var query = window.matchMedia('(prefers-color-scheme: dark)');
  function apply() {
    var scheme = query.matches ? 'dark' : 'light';
    var root = document.documentElement;
    root.setAttribute('data-bs-theme', scheme);
    root.style.colorScheme = scheme;
    if (document.cookie.indexOf('di-scheme=' + scheme) < 0) {
      document.cookie = 'di-scheme=' + scheme + '; path=/; max-age=31536000; samesite=lax';
    }
  }
  apply();
  query.addEventListener('change', apply);
})();
