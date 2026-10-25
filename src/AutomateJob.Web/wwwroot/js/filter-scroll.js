(function () {
  var key = "job-filter-scroll";
  var form = document.querySelector("form.date-filter");
  if (!form) {
    return;
  }

  var saved = sessionStorage.getItem(key);
  if (saved !== null) {
    sessionStorage.removeItem(key);
    var y = Number(saved);
    if (!Number.isNaN(y)) {
      if ("scrollRestoration" in history) {
        history.scrollRestoration = "manual";
      }
      var restore = function () {
        window.scrollTo(0, y);
      };
      restore();
      requestAnimationFrame(restore);
      window.addEventListener("load", restore);
    }
  }

  form.addEventListener("submit", function () {
    sessionStorage.setItem(key, String(window.scrollY));
  });
})();
