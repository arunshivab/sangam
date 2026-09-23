# LiPi Sans v1.0 — Self-hosted

A unified type system for the Indian web. **Fully offline** — no CDN
calls, no external license servers, no internet required after install.

LiPi Sans is a derivative work of Inter and Noto Sans, distributed under
the SIL Open Font License 1.1. See `CUSTOMIZATIONS.md` for what's been
changed from upstream and `LICENSE.txt` for the full license text.

## What's in this folder

```
lipi/
├── lipi.css                            ← drop-in stylesheet (@font-face × 10 scripts)
├── fonts/                              ← 11 variable font files (~5 MB total)
│   ├── LiPi-Sans-Latin.woff2
│   ├── LiPi-Sans-Latin-Italic.woff2
│   ├── LiPi-Sans-Devanagari.ttf
│   ├── LiPi-Sans-Bengali.ttf
│   ├── LiPi-Sans-Tamil.ttf
│   ├── LiPi-Sans-Telugu.ttf
│   ├── LiPi-Sans-Malayalam.ttf
│   ├── LiPi-Sans-Kannada.ttf
│   ├── LiPi-Sans-Gujarati.ttf
│   ├── LiPi-Sans-Gurmukhi.ttf
│   └── LiPi-Sans-Odia.ttf
├── LICENSE.txt                          ← SIL OFL 1.1
├── CUSTOMIZATIONS.md                    ← what's changed vs upstream
└── README.md                            ← this file
```

A "font" on the web is **two things**:

1. The **binary glyph data** — the `.woff2` and `.ttf` files in `fonts/`.
   These are the actual letterforms.
2. A **CSS `@font-face` declaration** — `lipi.css` — which tells the
   browser *which* file to use for *which* family, weight, and script.

You need both. This package ships both.

## Install

Copy the entire `lipi/` folder into your app — anywhere it can be served
as static assets (Next.js `public/`, Rails `public/`, Vite `public/`,
plain Apache, S3, whatever).

```html
<link rel="stylesheet" href="/lipi/lipi.css">
```

```css
body { font-family: "LiPi Sans", system-ui, sans-serif; }
```

That's it. The `unicode-range` declarations in `lipi.css` route each
character to the right font automatically — mix scripts in a single line
with no `lang=` attributes:

```html
<h1>Hello नमस्ते வணக்கம் ಲಿಪಿ</h1>
```

## What you get

| Script         | Languages                              | File                            |
|----------------|----------------------------------------|---------------------------------|
| Latin          | English & 200+ European                | LiPi-Sans-Latin.woff2           |
| Devanagari     | Hindi, Marathi, Sanskrit, Nepali       | LiPi-Sans-Devanagari.ttf        |
| Bengali        | Bengali, Assamese                      | LiPi-Sans-Bengali.ttf           |
| Tamil          | Tamil                                  | LiPi-Sans-Tamil.ttf             |
| Telugu         | Telugu                                 | LiPi-Sans-Telugu.ttf            |
| Malayalam      | Malayalam                              | LiPi-Sans-Malayalam.ttf         |
| Kannada        | Kannada                                | LiPi-Sans-Kannada.ttf           |
| Gujarati       | Gujarati                               | LiPi-Sans-Gujarati.ttf          |
| Gurmukhi       | Punjabi                                | LiPi-Sans-Gurmukhi.ttf          |
| Odia           | Odia                                   | LiPi-Sans-Odia.ttf              |

All files are **variable fonts** — any weight 100–900 is valid:

```css
.thin   { font-weight: 100; }
.light  { font-weight: 300; }
.medium { font-weight: 500; }
.bold   { font-weight: 700; }
.black  { font-weight: 900; }
.semi   { font-weight: 638; }   /* yes, any number works */
```

## License

SIL Open Font License 1.1 — see `LICENSE.txt`. You can:

- Use LiPi in any product, commercial or otherwise
- Embed it in apps, documents, websites
- Bundle and redistribute it with your software
- Modify it (under a different name)

You cannot:

- Sell LiPi by itself
- Re-license it under a different license
- Use the names "Inter" or "Noto" for derivative versions

No royalties. No attribution required in your UI. No license server.
No patent encumbrances.

## Upstream sources

- Inter: https://github.com/rsms/inter
- Noto Sans family: https://github.com/google/fonts/tree/main/ofl
