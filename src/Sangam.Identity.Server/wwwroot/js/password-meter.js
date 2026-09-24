// Progressive enhancement only: the meter and the requirement rows are rendered by the
// server and are correct without JavaScript (after any post). This file updates the same
// markup as the user types, using the identical rules as Sangam.Identity.Application's
// PasswordStrength: 8+ characters, all four character classes, not a repeated character.
// Served from this origin; nothing is fetched and nothing is sent anywhere.
(function () {
    "use strict";

    var field = document.querySelector("[data-sg-password]");
    var group = field && field.closest(".sg-field-group");
    if (!field || !group) {
        return;
    }

    var meter = group.querySelector(".sg-meter");
    var segments = group.querySelectorAll(".sg-meter-seg");
    var verdictText = group.querySelector(".sg-meter-verdict");
    var rows = group.querySelectorAll(".sg-reqs li");
    if (!meter || segments.length !== 4 || !verdictText || rows.length !== 3) {
        return;
    }

    function evaluate(value) {
        var minimum = value.length >= 8;
        var classes = (/[a-z]/.test(value) ? 1 : 0) + (/[A-Z]/.test(value) ? 1 : 0) +
            (/[0-9]/.test(value) ? 1 : 0) + (/[^A-Za-z0-9]/.test(value) ? 1 : 0);
        var allClasses = classes === 4;
        var repeated = value.length > 0 && value.split("").every(function (c) { return c === value[0]; });
        var notBlocked = value.length > 0 && !repeated;

        if (!minimum || !allClasses || !notBlocked) {
            return {
                level: "weak",
                label: value.length === 0 ? "" : "Too weak",
                filled: value.length === 0 ? 0 : 1,
                rows: [minimum, allClasses, notBlocked]
            };
        }

        var filled = 2 + (value.length >= 11 ? 1 : 0) + (value.length >= 14 ? 1 : 0);
        return {
            level: filled >= 4 ? "strong" : "fair",
            label: filled >= 4 ? "Strong" : "Fair",
            filled: filled,
            rows: [true, true, true]
        };
    }

    function render() {
        var result = evaluate(field.value);
        meter.className = "sg-meter sg-meter--" + result.level;
        for (var i = 0; i < segments.length; i++) {
            segments[i].className = i < result.filled ? "sg-meter-seg sg-meter-seg--on" : "sg-meter-seg";
        }

        verdictText.className = "sg-meter-verdict sg-meter-verdict--" + result.level;
        verdictText.textContent = result.label;

        for (var r = 0; r < rows.length; r++) {
            var met = result.rows[r];
            rows[r].className = met ? "sg-req--met" : "";
            var mark = rows[r].querySelector(".sg-req-mark");
            if (mark) {
                mark.textContent = met ? "\u2713" : "\u2014";
            }
        }
    }

    field.addEventListener("input", render);
    render();
})();
