// Reveal sections as they scroll into view. Content is visible by default; this only adds the entrance.
(function () {
  var root = document.documentElement;
  if (!root.classList.contains('js')) return;
  var items = document.querySelectorAll('.reveal');
  if (!('IntersectionObserver' in window)) { items.forEach(function (el) { el.classList.add('in'); }); return; }
  var io = new IntersectionObserver(function (entries) {
    entries.forEach(function (e) {
      if (!e.isIntersecting) return;
      e.target.classList.add('in');
      io.unobserve(e.target);
    });
  }, { rootMargin: '0px 0px -8% 0px', threshold: 0.12 });
  items.forEach(function (el) { io.observe(el); });
  // Safety net: never leave anything hidden (background tabs, headless renderers).
  setTimeout(function () { items.forEach(function (el) { el.classList.add('in'); }); }, 2500);
})();
