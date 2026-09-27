// Small progressive enhancements for the marketing pages. They are fully usable without this file.
// Blazor's enhanced navigation swaps page content without a full load, so init() runs on first
// load and again after every enhanced navigation; it only touches elements it hasn't seen yet.
(function () {
  document.documentElement.classList.add('js');
  var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function init() {
    var year = document.getElementById('year');
    if (year) year.textContent = new Date().getFullYear();

    // Staggered fade-in as elements scroll into view.
    var reveals = document.querySelectorAll('.marketing .reveal:not([data-reveal])');
    reveals.forEach(function (el, i) { el.dataset.reveal = '1'; el.style.setProperty('--i', i % 6); });
    if ('IntersectionObserver' in window && !reduceMotion) {
      var io = new IntersectionObserver(function (entries) {
        entries.forEach(function (e) {
          if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
        });
      }, { threshold: 0.1 });
      reveals.forEach(function (el) { io.observe(el); });
    } else {
      reveals.forEach(function (el) { el.classList.add('in'); });
    }

    var canvas = document.querySelector('.marketing .stars:not([data-stars])');
    if (canvas && canvas.getContext) startStars(canvas);
  }

  // Twinkling night sky. Stops itself once the canvas leaves the page.
  function startStars(canvas) {
    canvas.dataset.stars = '1';
    var ctx = canvas.getContext('2d');
    var stars = [];
    var w = 0, h = 0;

    function resize() {
      var dpr = Math.min(window.devicePixelRatio || 1, 2);
      w = canvas.clientWidth;
      h = canvas.clientHeight;
      canvas.width = w * dpr;
      canvas.height = h * dpr;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      var count = Math.round((w * h) / 4500);
      stars = [];
      for (var i = 0; i < count; i++) {
        stars.push({
          x: Math.random() * w,
          y: Math.random() * h * 0.75, // keep the sky above the cars
          r: Math.random() * 1.2 + 0.3,
          phase: Math.random() * Math.PI * 2,
          speed: Math.random() * 0.002 + 0.0006
        });
      }
      if (reduceMotion) draw(0);
    }

    function draw(t) {
      ctx.clearRect(0, 0, w, h);
      ctx.fillStyle = '#fff';
      for (var i = 0; i < stars.length; i++) {
        var s = stars[i];
        ctx.globalAlpha = reduceMotion ? 0.8 : 0.3 + 0.7 * (0.5 + 0.5 * Math.sin(t * s.speed + s.phase));
        ctx.beginPath();
        ctx.arc(s.x, s.y, s.r, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.globalAlpha = 1;
    }

    function loop(t) {
      if (!canvas.isConnected) { window.removeEventListener('resize', resize); return; }
      draw(t);
      requestAnimationFrame(loop);
    }

    window.addEventListener('resize', resize);
    resize();
    if (!reduceMotion) requestAnimationFrame(loop);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
  if (window.Blazor && Blazor.addEventListener) Blazor.addEventListener('enhancedload', init);
  else document.addEventListener('DOMContentLoaded', function () {
    if (window.Blazor && Blazor.addEventListener) Blazor.addEventListener('enhancedload', init);
  });
})();
