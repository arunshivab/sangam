// Sangam (R7, ASVS V2.1.12): every password field gets a button to show what was typed, so a long password can be
// checked before it is sent. Progressive enhancement: without JavaScript the field is an ordinary password field.
// The button sits inside the field, so nothing moves when it appears; its labels come from the page, in its language.
(function () {
  'use strict';
  var body = document.body;
  var show = body.getAttribute('data-reveal-show') || 'Show password';
  var hide = body.getAttribute('data-reveal-hide') || 'Hide password';
  var showShort = body.getAttribute('data-reveal-show-short') || 'Show';
  var hideShort = body.getAttribute('data-reveal-hide-short') || 'Hide';
  document.querySelectorAll('input[type="password"]').forEach(function (field) {
    var wrap = document.createElement('span');
    wrap.className = 'sg-reveal';
    field.parentNode.insertBefore(wrap, field);
    wrap.appendChild(field);
    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'sg-reveal-btn';
    button.setAttribute('aria-pressed', 'false');
    button.setAttribute('aria-label', show);
    button.textContent = showShort;
    if (field.id) { button.setAttribute('aria-controls', field.id); }
    // Typed text never runs under the button, whatever the language's word for it.
    function fit() { field.style.paddingRight = (button.getBoundingClientRect().width + 12) + 'px'; }
    button.addEventListener('click', function () {
      var shown = field.type === 'text';
      field.type = shown ? 'password' : 'text';
      button.setAttribute('aria-pressed', shown ? 'false' : 'true');
      button.setAttribute('aria-label', shown ? show : hide);
      button.textContent = shown ? showShort : hideShort;
      fit();
      field.focus();
    });
    wrap.appendChild(button);
    fit();
  });
  // A shown password is hidden again before the form is sent, so the browser never stores it as plain text.
  document.querySelectorAll('form').forEach(function (form) {
    form.addEventListener('submit', function () {
      form.querySelectorAll('.sg-reveal input').forEach(function (field) { field.type = 'password'; });
    });
  });
})();
