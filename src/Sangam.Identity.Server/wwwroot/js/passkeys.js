// Sangam passkeys (PR-14, SGM-205). Progressive enhancement: the buttons stay hidden unless the browser
// supports WebAuthn, so password and code sign-in keep working with no JavaScript at all (D-063).
(function () {
  'use strict';
  if (!window.PublicKeyCredential || !navigator.credentials) { return; }

  function fromB64url(s) {
    s = s.replace(/-/g, '+').replace(/_/g, '/');
    while (s.length % 4) { s += '='; }
    var bin = atob(s), out = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
    return out.buffer;
  }

  function toB64url(buf) {
    var bytes = new Uint8Array(buf), bin = '';
    for (var i = 0; i < bytes.length; i++) { bin += String.fromCharCode(bytes[i]); }
    return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }

  function token() {
    var input = document.querySelector('input[name="__RequestVerificationToken"]');
    return input ? input.value : '';
  }

  function post(url, body) {
    return fetch(url, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
      body: JSON.stringify(body || {})
    }).then(function (r) { return r.json(); });
  }

  function descriptors(list) {
    return (list || []).map(function (d) { return { type: d.type || 'public-key', id: fromB64url(d.id), transports: d.transports }; });
  }

  function show(el, message) {
    var target = document.getElementById(el.getAttribute('data-message'));
    if (target) { target.textContent = message; target.hidden = !message; }
  }

  function credentialJson(c, isCreate) {
    var r = c.response, response = isCreate
      ? { attestationObject: toB64url(r.attestationObject), clientDataJSON: toB64url(r.clientDataJSON), transports: r.getTransports ? r.getTransports() : [] }
      : { authenticatorData: toB64url(r.authenticatorData), clientDataJSON: toB64url(r.clientDataJSON), signature: toB64url(r.signature), userHandle: r.userHandle ? toB64url(r.userHandle) : null };
    return { id: c.id, rawId: toB64url(c.rawId), type: c.type, response: response, clientExtensionResults: c.getClientExtensionResults ? c.getClientExtensionResults() : {} };
  }

  document.querySelectorAll('[data-passkey-signin]').forEach(function (button) {
    button.hidden = false;
    button.addEventListener('click', function () {
      show(button, '');
      button.disabled = true;
      post(button.getAttribute('data-options-url')).then(function (start) {
        var o = start.options;
        return navigator.credentials.get({ publicKey: {
          challenge: fromB64url(o.challenge), rpId: o.rpId, timeout: o.timeout,
          userVerification: o.userVerification || 'required', allowCredentials: descriptors(o.allowCredentials)
        } }).then(function (c) {
          return post(button.getAttribute('data-verify-url'), { challengeId: start.challengeId, credential: JSON.stringify(credentialJson(c, false)) });
        });
      }).then(function (result) {
        if (result.redirect) { window.location.assign(result.redirect); return; }
        show(button, result.error || 'That passkey could not sign you in.');
        button.disabled = false;
      }).catch(function () {
        show(button, 'The passkey request was cancelled or is not available on this device.');
        button.disabled = false;
      });
    });
  });

  document.querySelectorAll('[data-passkey-add]').forEach(function (button) {
    button.hidden = false;
    button.addEventListener('click', function () {
      show(button, '');
      button.disabled = true;
      var nameInput = document.getElementById(button.getAttribute('data-name'));
      post(button.getAttribute('data-options-url')).then(function (start) {
        var o = start.options;
        return navigator.credentials.create({ publicKey: {
          rp: o.rp, user: { id: fromB64url(o.user.id), name: o.user.name, displayName: o.user.displayName },
          challenge: fromB64url(o.challenge), pubKeyCredParams: o.pubKeyCredParams, timeout: o.timeout,
          attestation: o.attestation || 'none', authenticatorSelection: o.authenticatorSelection,
          excludeCredentials: descriptors(o.excludeCredentials)
        } }).then(function (c) {
          return post(button.getAttribute('data-verify-url'), { challengeId: start.challengeId, credential: JSON.stringify(credentialJson(c, true)), name: nameInput ? nameInput.value : '' });
        });
      }).then(function (result) {
        if (result.redirect) { window.location.assign(result.redirect); return; }
        show(button, result.error || 'This passkey could not be added.');
        button.disabled = false;
      }).catch(function () {
        show(button, 'The passkey request was cancelled or is not available on this device.');
        button.disabled = false;
      });
    });
  });
})();
