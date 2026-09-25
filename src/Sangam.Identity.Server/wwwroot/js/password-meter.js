// Progressive enhancement only: the meter and its requirement tokens are rendered by the
// server and are correct without JavaScript (after any post). This file updates the same
// markup as the user types, using the identical rules as Sangam.Identity.Application's
// PasswordStrength: 8+ characters, all four character classes, not a repeated password.
// Served from this origin; nothing is fetched and nothing is sent anywhere.
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
    if (!meter || segments.length !== 4 || !verdictText || tokens.length !== 5) {
        return;
    }

    function evaluate(value) {
        var rules = {
            "8+": value.length >= 8,
            "upper": /[A-Z]/.test(value),
            "lower": /[a-z]/.test(value),
            "number": /[0-9]/.test(value),
            "symbol": /[^A-Za-z0-9]/.test(value)
        };
        var allClasses = rules.upper && rules.lower && rules.number && rules.symbol;
        var repeated = value.length > 0 && value.split("").every(function (c) { return c === value[0]; });
        var notBlocked = value.length > 0 && !repeated;

        if (!rules["8+"] || !allClasses || !notBlocked) {
            return { level: "weak", label: value.length === 0 ? "" : "Too weak", filled: value.length === 0 ? 0 : 1, rules: rules };
        }

        var filled = 2 + (value.length >= 11 ? 1 : 0) + (value.length >= 14 ? 1 : 0);
        return { level: filled >= 4 ? "strong" : "fair", label: filled >= 4 ? "Strong" : "Fair", filled: filled, rules: rules };
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
                mark.textContent = met ? "\u2713" : "\u00b7";
            }
        }
    }

    field.addEventListener("input", render);
    render();
})();
