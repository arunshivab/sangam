# localpackages

A local NuGet feed for packages that are not published to nuget.org.
`nuget.config` at the repository root adds this folder as a package source, so
`dotnet restore` works on a fresh clone and in CI with no extra setup.

| Package | Version | Owner | Licence |
|---|---|---|---|
| `LiPicons.Blazor` | 1.1.0 | imagiQa Healthcare Services Pvt Ltd | **Proprietary** — see below |

## LiPicons.Blazor

LiPicons is the hand-drawn icon system used across imagiQa's LiPi products.
The package in this folder is **© 2026 imagiQa Healthcare Services Pvt Ltd, all
rights reserved**. LiPi™ and LiPicons™ are trademarks of imagiQa Healthcare
Services Pvt Ltd.

It is **not** covered by the Apache License 2.0 that applies to the rest of this
repository, and its presence here grants no licence to use, copy or redistribute
it outside of building and running Sangam. If you fork Sangam for another product,
replace it with an icon set you are licensed to use.

Add a new version by dropping the `.nupkg` here and bumping the version in
`Directory.Packages.props`.
