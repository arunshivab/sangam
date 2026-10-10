// Progressive enhancement only: the meter and its requirement tokens are rendered by the
// server and are correct without JavaScript (after any post). This file updates the same
// markup as the user types, using the same rules as Sangam.Identity.Application's
// PasswordStrength (rc.5): at least the policy's length (data-min, 12 or more), the four
// character types only where the policy requires them (data-types), not a repeated character.
// The list of common passwords and the breached-password check run on the server when the
// password is saved. Served from this origin; nothing is fetched and nothing is sent anywhere.
(function () {
    "use strict";

    var field = document.querySelector("[data-sg-password]");
    var group = field && field.closest(".sg-field-group");
    if (!field || !group) {
        return;
    }

    var meter = group.querySelector("[data-sg-meter]");
    var segments = group.querySelectorAll(".sg-meter-seg");
    var verdictText = group.querySelector("[data-sg-verdict]");
    var tokens = group.querySelectorAll("[data-sg-token]");
    if (!meter || segments.length !== 4 || !verdictText || tokens.length === 0) {
        return;
    }

    var minimum = parseInt(meter.getAttribute("data-min"), 10) || 12;
    var requireTypes = meter.getAttribute("data-types") === "true";

    // PR-18: the verdicts in the reader's language, rendered by the server; English is the fallback.
    function label(name, fallback) {
        return verdictText.getAttribute("data-label-" + name) || fallback;
    }

    function evaluate(value) {
        var rules = {
            "length": value.length >= minimum,
            "upper": /[A-Z]/.test(value),
            "lower": /[a-z]/.test(value),
            "number": /[0-9]/.test(value),
            "symbol": /[^A-Za-z0-9]/.test(value)
        };
        var typesOk = !requireTypes || (rules.upper && rules.lower && rules.number && rules.symbol);
        var repeated = value.length > 0 && value.split("").every(function (c) { return c === value[0]; });
        var notBlocked = value.length > 0 && !repeated;

        if (!rules.length || !typesOk || !notBlocked || value.length > 128) {
            return { level: "weak", label: value.length === 0 ? "" : label("weak", "Too weak"), filled: value.length === 0 ? 0 : 1, rules: rules };
        }

        var filled = 2 + (value.length >= 14 ? 1 : 0) + (value.length >= 16 ? 1 : 0);
        return { level: filled >= 4 ? "strong" : "fair", label: filled >= 4 ? label("strong", "Strong") : label("fair", "Fair"), filled: filled, rules: rules };
    }

    function render() {
        var result = evaluate(field.value);
        meter.className = "sg-meter sg-meter--" + result.level;
        for (var i = 0; i < segments.length; i++) {
            segments[i].className = i < result.filled ? "sg-meter-seg sg-meter-seg--on" : "sg-meter-seg";
        }

        verdictText.className = "sg-meter-verdict sg-meter-verdict--" + result.level;
        verdictText.textContent = result.label;

        for (var t = 0; t < tokens.length; t++) {
            var token = tokens[t];
            var met = result.rules[token.getAttribute("data-sg-token")] === true;
            token.className = met ? "sg-token sg-token--met" : "sg-token";
            var mark = token.querySelector(".sg-token-mark");
            if (mark) {
                mark.textContent = met ? "✓" : "·";
            }
        }
    }

    field.addEventListener("input", render);
    render();
})();
