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

- **Every night (rc.2).** The `Nightly` workflow's `accessibility` job starts the four hosts on a fresh database
  and runs the walk (`tools/accessibility/axe_walk.py`) in all three languages. It fails on any axe violation, any
  policy violation, or an interactive page whose live connection breaks. Layout findings and words left in English
  are in its JSON report (the job's `accessibility` artifact). Run it by hand from the Actions tab before a release.
- **Run it locally** with the commands at the top of `axe_walk.py`. `SHOTS=1` keeps a screenshot of every screen.
- **Phone width (rc.3).** axe also runs at 390 pixels, where tables scroll sideways. The first nightly run found a
  table that could not be scrolled with the keyboard, only in Malayalam, where long values made it overflow; rc.3
  made every scrolling table a focusable, labelled region and added this step, so the case is always tested.
- **New screens need an entry in the walk.** rc.2 added choosing an organisation in the partner console: the panel
  it opens ended the browser's live connection from R3 to rc.1, and the walk never opened it.
- **When the connection drops**, the portal and the consoles say so and offer to try again or reload
  (`ConnectionStatus`, rc.2), instead of leaving buttons that do nothing.
- Automated checks find only part of WCAG failures. Before a formal accessibility statement, test with a screen
  reader (NVDA on Windows, TalkBack on Android) and with the keyboard only.
