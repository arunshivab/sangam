# Contributing to Sangam

Thank you for considering a contribution. Sangam is an identity platform, so the bar for
correctness and review is deliberately high: a bug here is a bug in every partner application.

## Before you start

- Read [`docs/README.md`](docs/README.md) for the delivery plan and current decisions, and
  [`docs/design/README.md`](docs/design/README.md) for anything visual — the design handoff is
  authoritative and is not reopened in pull requests.
- **Scope discipline.** Sangam holds users, credentials, organisations, memberships, roles,
  consents and the audit log. App-specific settings, notifications, billing and clinical data
  belong in partner applications. Proposals that widen the scope are welcome as issues, not as
  pull requests.
- Open an issue first for anything larger than a bug fix, so the direction is agreed before
  the code is written.

## Development setup

```powershell
git clone https://github.com/arunshivab/sangam.git
cd sangam
.\scripts\bootstrap-dev.ps1
```

The script checks for the .NET 10 SDK, looks for a native PostgreSQL 16 on `localhost:5432`
(`-PgPort` if yours differs; `-InitDatabase` creates the role and database once) and runs the
full build gate. Docker is not required; `-WithDocker` starts the Compose stack instead.

## The build gate

Every pull request must pass all three locally before it is opened:

```powershell
Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force
dotnet build Sangam.sln -c Release
dotnet test  Sangam.sln -c Release --no-build
dotnet format Sangam.sln --verify-no-changes
```

Warnings are errors. The rules that most often bite:

- `CA1062` — `ArgumentNullException.ThrowIfNull(x)` on every public method parameter.
- `IDE0270` — `?? throw`, not `if (x is null) throw`.
- `IDE0005` — no unused `using` directives, in `src/` and `tests/`. Parent namespaces are
  implicit: inside `Sangam.Identity.Domain.Entities`, `using Sangam.Identity.Domain;` is redundant.
- `IDE0008` — no `var` in `src/` (tests may use it).
- `IDE0011` — braces on every control-flow statement.
- `CS1591` — XML documentation on every public member in `src/`.
- Tests: no `.ConfigureAwait(false)` inside `[Fact]` bodies (`xUnit1030`);
  `Assert.Contains(item, collection)` rather than `Assert.True(collection.Contains(item))`
  (`xUnit2017`); hoist `new[] { … }` into a `private static readonly` field (`CA1861`).

## Pull requests

- Branch from `main`; one pull request per unit of work (large, coherent PRs are fine).
- Fill in the pull request template; link the issue.
- Keep line endings LF and files UTF-8 without BOM (`.gitattributes` enforces this).
- Commits should read as sentences: "Add consent screen rate limiting", not "fix stuff".
- CI (build + test on Ubuntu and Windows, plus the format check) must be green before review.
- Visual changes are verified against the screenshots in `docs/design/screenshots/`.

## Layering rules (enforced by tests)

- `Sangam.Identity.Domain` references only the BCL, `Sangam.Shared` and
  `Microsoft.Extensions.Identity.Stores`; never EF Core, ASP.NET Core or a provider.
- `SangamUser` (and every entity) stays inside `Sangam.Identity.Infrastructure`. Hosts and
  the application layer see DTOs only — no type with a `PasswordHash` or `SecurityStamp`
  property crosses that boundary.
- Audit rows are written through `IAuditWriter`, never by adding to `AuditEvents` directly,
  so they survive the caller's rollback.
- Migrations are generated with `dotnet ef` (tool manifest in `.config/`), reviewed by hand,
  and exempt from style analysis. Hand-edit them only to add raw SQL the model cannot express
  (rules, triggers), and say so in a comment.

## Conventions

- Projects: `Sangam.<Component>.<Subcomponent>`; namespaces match project and folder.
- Entities: singular nouns; commands `VerbNounCommand`; queries `GetNounQuery`; handlers
  `<Command|Query>Handler`; interfaces `I<Name>`.
- Database: `snake_case` tables (plural) and columns; `idx_<table>_<columns>`; `fk_<table>_<ref>`.
- Tests: class `<ClassUnderTest>Tests`, method `<Method>_<Scenario>_<Expected>`.
- Wordmark: lowercase `sangam` in UI text, "Sangam" in prose, `SangamID` only for the domain
  and package namespace.

## Licence

By contributing you agree that your contribution is licensed under the Apache License 2.0,
the licence of this repository.
