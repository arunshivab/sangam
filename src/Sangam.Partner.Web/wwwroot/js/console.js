// rc.11 (R5): "Copy" next to a client id or an address. The clipboard needs a secure context; when it is refused the
// value stays selectable on the page, so nothing is lost.
window.sangamCopy = function (text) {
  if (!navigator.clipboard) { return Promise.resolve(false); }
  return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return false; });
};
