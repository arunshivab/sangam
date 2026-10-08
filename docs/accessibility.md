# Accessibility (R7, PR-31)

Sangam aims to meet WCAG 2.2 level AA on every screen, in every language it ships: Hindi, Malayalam and English. The
full report is SGM-507 in the document set.

## How it is checked

- **axe-core 4.14** runs on every screen of the four hosts (56 screens a language) in Chromium, with the WCAG 2.0,
  2.1 and 2.2 A and AA rules. The screens are reached through the real flows: registration and codes, two-step
  sign-in, consent, and the consoles with applications and people in them.
- **A layout check** runs at 390 pixels wide, measured against the screen. It looks for any sideways scrolling and
  any control or text off the screen. Long Malayalam labels are the usual cause.
- **A content security policy check** confirms that the browser reports no violation of the hosts' policy on any
  screen.

## Result at R7

168 of 168 screens pass: 0 axe violations, 0 layout issues and 0 policy violations in each language. On 7 screens,
axe could not work out the background behind some text, so it could not decide their colour contrast. Those need a
manual check.

## What R7 fixed

| Finding | WCAG | Fix |
|---|---|---|
| Text and controls below 4.5:1 contrast: hovered buttons, code tokens, the console's scope label, the language footer | 1.4.3 | Colours in `sangam-tokens.css` and each host's `site.css` |
| Links in running text marked only by colour | 1.4.1 | Underlined |
| The code screens refreshed themselves (meta refresh) | 2.2.1 | Removed. The resend countdown is a script (`resend.js`) |
| Fields without a programmatic label: the portal's mobile number and sign-in mode | 1.3.1, 4.1.2 | `label for` and `aria-labelledby` |
| Links in lists too close together | 2.5.8 | Spacing |
| Buttons, tables and page actions wider than a phone, with Malayalam labels | 1.4.10 | Buttons wrap; tables scroll inside a wrapper; page actions move under the title |
| No way to check a typed password | 3.3.2 | A show/hide button on every password field (also ASVS V2.1.12) |

## Keeping it

- Run the check in each release's gate, in all three languages.
- New screens need an entry in the walk.
- Automated checks find only part of WCAG failures. Before a formal accessibility statement, test with a screen
  reader (NVDA on Windows, TalkBack on Android) and with the keyboard only.
