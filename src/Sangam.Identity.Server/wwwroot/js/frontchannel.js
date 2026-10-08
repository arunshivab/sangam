// PR-20 front-channel logout: once every application's hidden sign-out page has loaded (or after three
// seconds, whichever comes first), carry on. Without JavaScript the Continue button does the same.
(function () {
  "use strict";
  var card = document.querySelector("[data-frontchannel]");
  if (!card) { return; }
  var frames = card.querySelectorAll("iframe.sg-frontchannel");
  var target = card.querySelector("[data-frontchannel-continue]");
  var pending = frames.length;
  var done = false;
  function go() {
    if (done || !target) { return; }
    done = true;
    if (target.tagName === "FORM") { target.submit(); } else { window.location.assign(target.getAttribute("href")); }
  }
  for (var i = 0; i < frames.length; i++) {
    frames[i].addEventListener("load", function () { pending--; if (pending <= 0) { go(); } });
  }
  window.setTimeout(go, 3000);
}());
