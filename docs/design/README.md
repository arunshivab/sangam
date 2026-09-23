# Handoff: Sangam — brand system + 12 product screens

## Overview

Sangam is an open-source identity and tenancy platform for Indian healthcare software.
Users register once at `id.sangamid.in` and sign in across partner applications
(LiPi, Aarogya Clinic Suite, …) via OAuth/OIDC redirect.

This bundle covers three surfaces:

| Surface | Host | Stack (as briefed) | Notes |
|---|---|---|---|
| Authentication | `id.sangamid.in` | ASP.NET Razor Pages | **Must work with JavaScript disabled** |
| Self-service portal | `account.sangamid.in` | Blazor Server, authenticated | 1024 px minimum |
| Admin console | `admin.sangamid.in` | Blazor Server, MFA mandatory | 1024 px minimum, dark chrome |

## About the design files

The files in this bundle are **design references written in HTML** — prototypes that show
intended look, states and behaviour. They are **not production code to copy**. The task is to
**recreate these designs in the target codebase** (Razor Pages + Blazor components here) using its
established patterns, layout primitives and CSS pipeline. `sangam-tokens.css` is the one file
meant to be used as-is (or translated into the project's token format).

The `.dc.html` files are self-rendering design documents; open them in a browser to see the
designs. They reference `support.js`, which is included in this folder — keep the four files in
the same directory. They use React-flavoured attributes (`style-hover`, `style-focus`,
`className`); treat those as **declarations of the intended CSS states**, not as API.

## Fidelity

**High fidelity.** Colours, type, spacing, sizes, states and copy are final unless the team
decides otherwise. Recreate closely. All hex values, sizes and ratios in this README are
authoritative; where the HTML and this README disagree, this README wins.

---

## Brand foundation

### Logo

The mark is a **palm-leaf folio**: a rounded-rectangle leaf, two incised text rules, and the
binding cord hole. Final geometry (64 × 64 viewBox, round-03):

```svg
<svg viewBox="0 0 64 64" role="img" aria-label="Sangam">
  <rect x="3.25" y="16.25" width="57.5" height="31.5" rx="15.75"
        fill="none" stroke="#0F3B38" stroke-width="5.5"/>
  <g stroke="#0F3B38" stroke-width="4" stroke-linecap="round">
    <path d="M33 26.5 H51"/><path d="M33 37.5 H51"/>
  </g>
  <circle cx="20" cy="32" r="6.25" fill="#8A6A2F"/>
</svg>
```

Single colour: set both `#0F3B38` and `#8A6A2F` to `currentColor` (or `#000` / `#FFF`).
Reversed on teal: stroke `#F2EFE8`, hole `#D9C9A8`.

- **Wordmark:** lowercase `sangam`, Newsreader 500, `letter-spacing: -0.012em`, colour `#0F3B38`.
  Never capitalised as a logotype. In running prose write "Sangam". "SangamID" is the domain /
  package namespace only.
- **Functional lockup** (consent, console, docs): `sangam` + `ID` where ID is Mukta 600,
  ~0.5× the wordmark size, `letter-spacing: 0.14em`, colour `#8A6A2F`, baseline-raised.
- **Lockup metrics:** mark height = cap height × 1.6; gap between mark and word = one hole
  diameter.
- **Clear space:** x = cord-hole diameter = 0.195 × mark height, on all four sides; 0.75x
  minimum in cramped UI chrome.
- **Minimum sizes:** mark 16 px screen / 6 mm print; primary lockup 96 px wide / 28 mm print.
  Below 16 px ship a hinted 1-bit favicon, don't scale the SVG.
- **Never:** stretch, rotate, shadow/bevel, recolour off-palette, place on gradients or busy
  imagery, or crowd the clear space.

### Colour

Primary — signature `#0F3B38` Sangam Teal · neutral `#F2EFE8` Manuscript Cream ·
accent `#8A6A2F` Cord Ochre.

| Token | Hex | RGB | HSL | On white | On teal |
|---|---|---|---|---|---|
| `--sg-teal` | #0F3B38 | 15 59 56 | 176 59% 15% | 12.33 AAA | — |
| `--sg-cream` | #F2EFE8 | 242 239 232 | 42 28% 93% | 1.15 | 10.74 AAA |
| `--sg-ochre` | #8A6A2F | 138 106 47 | 39 49% 36% | 5.02 AA | 2.46 ✕ |
| `--sg-ink` | #14202E | 20 32 46 | 212 39% 13% | 16.46 AAA | 1.33 ✕ |
| `--sg-palm-shade` | #1F5A52 | 31 90 82 | 172 49% 24% | 7.95 AAA | 1.55 ✕ |
| `--sg-leaf-mist` | #CBD9D3 | 203 217 211 | 154 16% 82% | surface only | 8.46 AAA |
| `--sg-sandalwood` | #D9C9A8 | 217 201 168 | 40 39% 75% | surface only | 7.56 AAA |
| `--sg-sindoor` | #9C3B24 | 156 59 36 | 12 63% 38% | 6.85 AAA | 1.80 ✕ |
| `--sg-slate` | #6B6A63 | 107 106 99 | 53 4% 40% | 5.43 AA | 2.27 ✕ |

Semantic: success `#276B4E` (6.36) · warning `#7E5A14` (6.25) · danger `#9C3B24` (6.85) ·
info `#1F5A52` (7.95). Tinted grounds: `#EDF2EC` / `#F6F1E4` / `#F9EFEC` / `#EDF1EF`.

Surfaces: page `#F7F5F0`, card `#FFFFFF`, sunk `#F2EFE8`, border `#E7E2D6`,
strong border `#D7D2C6`, inverse border `rgba(242,239,232,0.18)`.

**Dark (admin) ground overrides** — light-mode semantics fail on `#16403C`, so use:
active `#5FBF8F`, warning `#E0A93F`, danger `#C2603F`, danger text `#F0C9BE`,
muted text `#9DB3AC` / `#8FAFA6`, page `#0B2C2A`, sidebar `#082321`, card `#16403C`,
top bar `#0F3B38`.

Leaf Mist and Sandalwood are **surface/border/reversed-text only** — never text on white.

### Typography

- **Display — Newsreader** (Google Fonts, weights 300–600, optical-size axis). Headlines,
  card titles, portal statistics.
- **Body — Mukta** (300–700). Latin + Devanagari drawn together, required for bilingual
  consent screens. Fallback stack: `"Mukta", "Noto Sans Devanagari", system-ui, sans-serif`.
- **Mono — JetBrains Mono** (400–500). Endpoints, IDs, timestamps, metadata labels, all caps
  micro-labels at 10–11 px with `letter-spacing: 0.12em`.

| Token | Size / line-height | Tracking | Use |
|---|---|---|---|
| display-64 | 64 / 1.02 | -0.02em | Marketing hero |
| display-48 | 48 / 1.06 | -0.015em | Page hero |
| head-38 | 38 / 1.12 | -0.01em | Portal greeting |
| head-30 | 30 / 1.2 | -0.01em | Section / card heading |
| title-24 | 24 / 1.3 | -0.005em | Card title (Mukta 600) |
| title-21 | 21 / 1.4 | 0 | Subsection (Mukta 500) |
| body-18 | 18 / 1.65 | 0 | Long-form |
| body-16 | 16 / 1.6 | 0 | Default UI + all inputs |
| body-14 | 14 / 1.55 | 0.005em | Secondary / helper |
| caption-12 | 12 / 1.5 | 0.12em caps | Mono metadata only |

12 px is a floor and mono-only. **Devanagari runs +0.1 line-height** to clear shirorekha and
matras. Inputs are never below 16 px (iOS zooms on focus).

### Space, shape, motion

4 px base: 4, 8, 12, 16, 24, 32, 48, 64, 96 (`--sg-space-1 … -24`).
Grid: 12 columns, 24 px gutters, 1160 px max content, 32/24/16 px page margins.
Auth screens: **single 400 px column centred**, no sidebar; consent uses 520 px.
Radius: 2 px controls, 4 px cards, 12 px app icons (the mark is the only fully round shape).
Borders 1 px. **No shadows anywhere** except the focus ring.
Focus: `border-color: #8A6A2F; box-shadow: 0 0 0 2px rgba(138,106,47,0.28)`
(dark: `0 0 0 2px rgba(217,201,168,0.35)`).
Controls 44 px tall = minimum touch target. Transitions: 150 ms on colour only. No entrance
animation, no parallax, no toasts that move layout.

### Ornament

Permitted graphic devices, and nothing else: the **cord rule** (1 px line – 7 px ochre dot – 1 px
line) as section divider; the **cord dot** (8 px circle) as status atom; the **drop cap**
(Newsreader, ~58 px, `float:left`, `line-height:0.82`) in long-form docs only. `सं` may appear
once per surface as a 4–6 % opacity watermark behind a page header, never coloured, never a
control. No spot illustration, no isometric diagrams; technical figures use 1.5 px strokes,
palette colours and mono labels.

---

## Screens

Shared auth anatomy (screens 1–8), top to bottom, 400 px column on `#F7F5F0`:

1. Centred Sangam lockup — 26 px mark + 21 px wordmark, 9 px gap.
2. Partner context chip (only where a partner is in the flow): white card, 1 px `#E7E2D6`,
   **3 px left border in the partner's colour**, 10/12 px padding, 26 px rounded partner glyph
   (6 px radius) + 14 px text.
3. White card, 1 px `#E7E2D6`, 4 px radius, 26 px padding, 16 px vertical rhythm.
4. Footer links 13 px `#6B6A63`: Terms · Privacy · Help · `id.sangamid.in` in mono 11 px.

Field pattern: label Mukta 500 14 px → input 44 px, 16 px text, 12 px horizontal padding,
1 px `#D7D2C6`, 2 px radius, `box-sizing:border-box`, `width:100%`.
Primary button: 44 px, `#0F3B38` on `#F2EFE8` text, 15 px 600; hover `#1F5A52`.
Secondary: transparent, 1 px `#0F3B38` text `#0F3B38`; hover ground `#E7EDEA`.
Danger: text `#9C3B24`, border `#D9B3A8`; hover fills `#9C3B24` with white text.
Disabled: `#EFEAE0` ground, `#E7E2D6` border, `#9A9890` text, `cursor:not-allowed`.

**Turnstile strip** (a fixed-height reassurance row, not a widget slot): `#F2EFE8` ground,
1 px `#E7E2D6`, 9/12 px padding, 17 px shield-tick icon (`#1F5A52`, 1.6 stroke), text
"Verified without a challenge" 13 px, mono "TURNSTILE" 10 px right-aligned. On challenge the
server swaps the strip's contents — **same box, same height**.

### 1. Login — `/login`
Email; Password with "Forgot password?" as a 13 px link on the label row; Turnstile strip;
primary "Sign in"; cord-rule divider; "New to Sangam? Create an account".
Two partner variants shipped: **LiPi** `#1D4E89`, glyph `लि` (Noto Sans Devanagari) and
**Aarogya Health** `#2E7D52`, glyph `A`. The forms are otherwise **pixel-identical** — partner
branding must never touch structure (anti-phishing requirement). Mobile variant shown at 320 px:
same order, card padding 20 px, wordmark 19 px, heading 25 px, "Forgot?" abbreviated.

### 2. Registration — `/register`
Chip reads "Creating your account for LiPi". Fields: Full name, Email, Password.
**Strength meter:** four 4 px segments, 5 px gap, filled `#276B4E` (strong) / `#7E5A14` (fair),
empty `#E2DDD0`; below it a fixed-height caption row — left the verdict in the matching colour,
right factual guidance ("12+ characters, not a known breach"). Reserve the row in every state so
the button never moves. ToS checkbox (18 px, `accent-color:#0F3B38`) is **unchecked by default**.
Turnstile strip, primary "Create account", "Already registered? Sign in".

### 3. Email verification sent — `/verify-sent`
Centred card: 46 px circle, 1 px `#D9C9A8` on `#F6F1E4`, containing a 10 px ochre cord dot;
"Check your email"; the address in mono 14 px; "valid for 30 minutes, can be used once";
**disabled** button labelled "Resend in 0:47"; divider; "Wrong address? Change it · Stuck?
help@sangamid.in". After 60 s the **same button box** returns as an enabled secondary
"Resend verification email". No-JS implementation: serve the page with
`<meta http-equiv="refresh" content="60">`; the server decides the button's state and label.

### 4. Email verification confirmed — `/verify?token=…`
46 px circle, 1 px `#BFD6C7` on `#EDF2EC`, with a `#276B4E` tick (2.4 stroke, round caps);
"Email verified"; primary "Continue to LiPi" containing a 20 px partner glyph so the
destination is unambiguous; tertiary link "Go to your Sangam account instead".

### 5. Forgot password — `/forgot`
**No partner chip** (recovery is user↔Sangam only). Email, Turnstile, "Send reset link", then the
enumeration note stated plainly: "For your safety we show the same confirmation whether or not an
account exists." + "Back to sign in".

### 6. Reset password — `/reset?token=…`
New password + strength meter (shown in the "Fair" state), Confirm password, and a fixed
three-row requirement list on `#F2EFE8`: met rows lead with the 15 px tick, unmet with an em dash
in `#9A9890` — rows never appear or disappear. Primary "Set new password"; footnote "Setting a
new password signs you out of Sangam on all devices."

### 7. Consent — `/consent` (the trust moment; 520 px card, 30 px padding)
- **Relationship row:** 54 px partner tile (12 px radius, brand colour) → arrow "→" 20 px
  `#8A6A2F` with mono caption **REQUESTS** under it → 54 px `#0F3B38` tile holding the reversed
  mark. Direction is partner → Sangam: the partner is asking, Sangam is holding.
- Heading 30 px: "LiPi would like to use your Sangam account"; subline names the legal operator.
- **Account row** on `#F2EFE8`: 36 px teal avatar with initials, name 15 px, email mono 12 px,
  "Switch account" right.
- **Two equal columns.** Left "WILL BE SHARED" (mono 10 px `#276B4E`): three tick rows, each with
  the **real value** beneath the claim (Your name → Ravi Menon; email; organisations), then scope
  chips `openid` `profile` `orgs.read` (mono 11 px, `#EDF1EF` on `#1F5A52`).
  Right "WILL NOT BE SHARED" (mono 10 px `#9C3B24`) on `#FBFAF6` with 1 px `#EFEAE0`: password,
  data held by other apps, clinical records or prescriptions, sign-in history, payment details.
- **Buttons 48 px:** Cancel secondary `flex:1` (min 130 px), Allow access primary `flex:2`
  (min 180 px). Cancel is a full button, never a link.
- Footnote: withdrawal at `account.sangamid.in`; after approval the partner's privacy policy
  governs.

### 8. Sign out — `/logout`
"Sign out of Sangam?" + honest scope copy (partner sessions may persist until they expire);
"SIGNED IN AS" block on `#F2EFE8` with 32 px avatar; two **equal-width 44 px** buttons — "Stay
signed in" (secondary) and "Sign out" (primary, *not* styled destructive); divider; partner glyph
+ "Return to LiPi without signing out".

### 9. Portal dashboard — `account.sangamid.in`
White top bar, 1 px bottom border, 28 px side padding: lockup (24 px mark + 19 px word), tab nav
(Overview · Connected apps · Devices · Audit log · Personal details) with the active tab carrying a
**2 px `#8A6A2F` underline**, then name + 34 px avatar right. No sidebar — five destinations, and a
sidebar would read as an operator tool.
Body 34/28 px padding: greeting "Namaste, Ravi" at head-38 + one-line subtitle; **three stat cards**
(mono 10 px label, **Newsreader 38 px value**, 13 px footnote) — Linked apps 4, Organisations 2,
Last sign-in Today; then a 2 × 2 card grid (Connected apps, Devices and sessions, Security,
Personal details) where each card has a Newsreader 22 px title, 14 px description and a
`Manage →` link. Cards hover to `border-color:#8A6A2F`. The **only** coloured status on the page
is the 2FA nudge (ochre dot + `#7E5A14` text).

### 10. Linked apps — `account.sangamid.in/apps`
Header row: title + subtitle left, "Download access report" secondary button right.
Table is a CSS grid, columns `2.2fr 1fr 1fr 1.4fr 120px`, 16 px gap, header row mono 10 px on
1 px `#EFEAE0`, body rows 16/20 px padding, row hover `#FBFAF6`.
Per row: 38 px app tile (9 px radius) + name 15 px + one-line description 13 px; linked date;
last-used with **cord-dot status** (`#276B4E` active · `#7E5A14` expiring · `#B9B5AA` dormant);
organisations; action button 36 px. **Rule:** apps dormant for months lead with a **Revoke**
(danger) button instead of Manage — the system nudges toward less access.
**Empty state:** white card, 48/28 px padding, 52 px mark in Leaf Mist `#CBD9D3`, "No apps linked
yet", 430 px explanatory paragraph, "How consent works →". This is the only context where the
mark appears in Leaf Mist.

### 11. Admin dashboard — `admin.sangamid.in`
Grid `216px 1fr` on `#0B2C2A`.
**Sidebar** `#082321`, 1 px right border `rgba(242,239,232,0.12)`: reversed lockup + a
**Sandalwood ADMIN badge** (mono 9 px, `#0B2C2A` on `#D9C9A8`); nav items 10/18 px with the active
item on `rgba(242,239,232,0.1)` and a 3 px `#D9C9A8` left rule; pinned to the bottom, an OPERATOR
block with email and `role: platform-admin`.
**Top bar** `#0F3B38`: "PRODUCTION · ap-south-1" in Sandalwood mono; right, shield icon +
"MFA verified · session expires in 12 min" + an "Extend" ghost button. This is permanent chrome,
never a toast.
**Body:** "Platform overview" + quick actions (Register application — the one Sandalwood-filled
button; Invite operator; Rotate signing key). Four stat cards on `#16403C`: Total users 128,420 ·
Daily active 21,338 · Applications 37 · Uptime 30 d 99.98%. Values are **JetBrains Mono 27 px** —
the deliberate inverse of the portal's serif numerals.
**Recent audit events** table, columns `150px 200px 1fr 150px`: time (mono), actor, action (IDs
in Sandalwood mono), source IP. A failed-MFA row is washed `rgba(156,59,36,0.16)` with `#F0C9BE`
text.

### 12. Users table — `admin.sangamid.in/users`
Title + "128,420 total · 3 selected"; "Export CSV" (Sandalwood) right.
Filter row: search input (flex, min 240 px) + two selects + "More filters", all 40 px on `#16403C`
with `rgba(242,239,232,0.24)` borders.
**Bulk bar** sits **in flow above the table** (never floating): `#16403C` with a 1 px `#D9C9A8`
border, "3 SELECTED" in Sandalwood mono, actions Resend verification / Add to tenant / Lock
accounts (danger), and the note "Bulk actions are logged to the audit trail with your operator id."
Table grid `38px 2.2fr 1.6fr 90px 130px 110px`: checkbox (`accent-color:#D9C9A8`), name + email
(mono), organisations, app count (mono), status (dot + label, dark-ground colours), Manage button.
**Selected rows carry a `rgba(217,201,168,0.08)` wash** so selection is visible after the bar
scrolls away. Footer: "Showing 1–5 of 128,420" + Previous (disabled) / Next.
**Empty state:** 44 px Leaf Mist mark at 55 % opacity, "No users match these filters", the active
filters **restated verbatim** in Sandalwood mono, and "Clear all filters".

---

## Interactions & behaviour

- **No JavaScript on auth.** Every state in screens 1–8 is a distinct server response. Countdown =
  `meta refresh` + server-rendered label. Strength meter may be progressively enhanced client-side,
  but must render server-side after a failed post.
- **No layout shift**, ever: all state pairs (disabled/enabled, empty/filled strength, met/unmet
  requirement, passive/challenge Turnstile) occupy identical boxes.
- **Hover** (150 ms, colour only): primary → `#1F5A52`; secondary → ground `#E7EDEA`; tertiary →
  `#8A6A2F`; danger → filled `#9C3B24` + white; cards → `border-color:#8A6A2F`; table rows →
  `#FBFAF6` (light) / `rgba(242,239,232,0.06)` (dark).
- **Focus** is always the ochre ring above; never remove outlines without replacing them.
- **Errors:** field border `#9C3B24`, ground `#F9EFEC`, `aria-invalid="true"`, message 13 px
  `#9C3B24` directly under the field in a reserved row. Page-level errors use the danger banner
  (3 px left rule, `#F9EFEC` ground).
- **Responsive:** auth 320 → 1600 px, single column throughout, card padding 26 → 20 px below
  400 px. Portal and admin assume ≥ 1024 px and may scroll horizontally below that.
- **Accessibility:** AA minimum everywhere (most pairs are AAA), 44 px targets, visible focus,
  the mark carries `role="img"` + `aria-label="Sangam"`, status is never colour alone — every dot
  has an adjacent text label.

## State (portal / admin, Blazor)

Auth pages are stateless posts. Portal: current user, linked-app list (id, name, icon, colour,
linked date, last used, orgs, status), org list, session list, 2FA flag. Admin: operator identity
+ role, MFA state and session countdown, region/environment, platform counters, audit page,
user query (search + status + tenant + page), selection set for bulk actions, and the resulting
bulk-action confirmations. Every admin mutation writes an audit event carrying the operator id.

## Assets

- **Logo:** inline SVG above — no image file needed. Produce favicon 16/32, apple-touch 180,
  app icon 512 (12 px-equivalent radius, `#0F3B38` ground, cream mark, Sandalwood hole).
- **Fonts:** Google Fonts — Newsreader, Mukta, JetBrains Mono, Noto Sans Devanagari (fallback).
  Self-host for production; subset Devanagari separately.
- **Partner logos in the mocks are placeholders** (coloured tiles with a glyph). Real partner
  assets come from the partner registry; render them at 26/38/54 px with 6/9/12 px radius on a
  neutral tile, and never let a partner asset set page or button colour.
- No icon library is assumed: the tick and shield are inline SVGs at 1.6–2.4 stroke.

## Files in this bundle

| File | What it is |
|---|---|
| `Sangam Product Screens.dc.html` | All 12 screens, each with an annotation column. Primary reference. |
| `Sangam Identity System.dc.html` | Brand system: logo usage, misuse, colour, type, space, components, ornament, token export. |
| `SangamID Logo Concepts.dc.html` | Logo exploration and the selected palm-leaf direction (context only). |
| `sangam-tokens.css` | **Use this.** CSS custom properties, base control classes, and a `[data-scope="admin"]` block that re-maps surfaces for the admin console. |
| `support.js` | Runtime needed to open the three `.dc.html` files locally. Not part of the product. |
| `screenshots/01-…12-…png` | One 2× PNG per screen (with its annotation column), numbered to match the sections above. |
| `screenshots/brand-01…06-….png` | 2× PNGs of the brand system: logo usage, colour, typography, space/grid, components, ornament. |

## Implementation order (suggested)

1. Token layer + font loading + the logo component (all three lockups, all colour modes).
2. Shared auth layout (wordmark, partner chip slot, card, footer) and the control set.
3. Screens 1–8 as Razor Pages, verifying each state with JS disabled.
4. Portal shell + screens 9–10.
5. Admin shell with `data-scope="admin"` + screens 11–12.
