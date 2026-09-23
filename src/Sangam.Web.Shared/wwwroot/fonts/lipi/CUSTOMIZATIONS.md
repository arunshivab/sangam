# LiPi Sans — Customizations

LiPi Sans is a derivative of two upstream OFL families. Per SIL OFL §3
("No Modified Version of the Font Software may use the Reserved Font
Name(s)"), this fork avoids "Inter" and "Noto" in its public family
name and file names.

## What's actually changed in this v1.0 cut

| Change                       | Done | Notes                                       |
|------------------------------|------|---------------------------------------------|
| Public family name → "LiPi Sans" | ✅ | The CSS-facing identity. `font-family: "LiPi Sans"` is what consumers use. |
| File names rebranded         | ✅ | `LiPi-Sans-{Script}.{ttf,woff2}` instead of upstream filenames. |
| Unified `@font-face` stack    | ✅ | Single family across 10 scripts via `unicode-range`. |
| Opinionated feature stack     | ✅ | `ss01 cv11 calt kern liga` on by default — Inter's "Open" set + straight-leg L. |
| Tightened default line-height | ✅ | `1.45` (vs Inter's 1.5) — matches the visual rhythm of Devanagari/Bengali baselines. |
| Display tracking preset       | ✅ | `.lipi-display` class for headlines. |
| Numeric preset                | ✅ | `.lipi-num` — tabular + slashed zero. |
| Backwards-compat `"LiPi"` alias | ✅ | Both `"LiPi Sans"` and `"LiPi"` resolve. |
| Internal `name` table rebrand | ⏳ | Requires `fonttools` (Python) — see below. |
| Custom kerning pairs          | ⏳ | Manual TTX edit required. |
| Custom OpenType ligature set  | ⏳ | Manual feature-file edit. |

## To finish the binary-level rebrand

The CSS-facing rename is what users see and is legally sufficient under
OFL §3 (which restricts the "primary font name as presented to the
users"). The internal name table inside the `.ttf` / `.woff2` binaries
still says "Inter" / "Noto Sans X" — to flip those, run:

```bash
pip install fonttools
python rename.py
```

Where `rename.py` walks each font and writes new `name` table records.
A starter script is on the roadmap; for now, see the [fonttools docs](
https://fonttools.readthedocs.io/en/latest/ttLib/tables/_n_a_m_e.html).

## Attribution

Even with full binary rebranding, OFL §4 requires acknowledging the
original authors. LiPi credits its sources in `LICENSE.txt` and `README.md`,
which is sufficient — no UI attribution required.

## Roadmap for "real" font design work

If you want LiPi to become an original typeface rather than a curated
derivative, the realistic path is:

1. Hire a type designer (~$15k–$80k for a Latin + 1 Indic script)
2. Provide the glyph briefs — character set, weight axis, OpenType
   features, design reference
3. Iterate on `.glyphs` source files in [Glyphs.app](https://glyphsapp.com)
   or [FontForge](https://fontforge.org)
4. Compile to `.ttf` / `.woff2` with `fontmake`
5. Drop the resulting files into `lipi/fonts/` — the rest of this
   package (CSS, license, install instructions) carries over unchanged.
