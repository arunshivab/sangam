// Sangam (R7, WCAG 2.2.1): the "send again" buttons wait out their cool-down here, in the page, instead of the page
// reloading itself when it ends — a reload could wipe a code the person was typing. Without JavaScript the button
// simply works: pressed too early, the server says to wait.
(function () {
  'use strict';
  document.querySelectorAll('[data-resend-in]').forEach(function (button) {
    var left = parseInt(button.getAttribute('data-resend-in'), 10) || 0;
    var wait = button.getAttribute('data-resend-wait') || '';
    var ready = button.getAttribute('data-resend-ready') || button.textContent;
    function label(seconds) {
      var m = Math.floor(seconds / 60), s = seconds % 60;
      return wait.replace('{0}', m + ':' + (s < 10 ? '0' : '') + s);
    }
    if (left <= 0) { return; }
    button.disabled = true;
    button.textContent = label(left);
    var timer = window.setInterval(function () {
      left -= 1;
      if (left > 0) { button.textContent = label(left); return; }
      window.clearInterval(timer);
      button.disabled = false;
      button.textContent = ready;
    }, 1000);
  });
})();
