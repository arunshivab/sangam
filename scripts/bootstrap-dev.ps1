<#
.SYNOPSIS
    One-shot local setup for Sangam.

.DESCRIPTION
    Checks the .NET 10 SDK, looks for a PostgreSQL server (native install by
    default), optionally creates the sangam_identity role and database, then
    cleans, restores, builds and tests the solution. Safe to re-run.
    EF Core migrations are applied here from PR-02.

.PARAMETER InitDatabase
    Run deploy/postgres/init.sql through psql (as the postgres superuser) to
    create the sangam_identity role and database. Idempotent. Requires psql on
    PATH; you will be prompted for the postgres password unless PGPASSWORD is set.

.PARAMETER WithDocker
    Instead of a native PostgreSQL, start deploy/docker-compose.dev.yml
    (PostgreSQL 16 + Caddy). Not needed for laptop development.

.PARAMETER PgHost / PgPort
    Where to look for PostgreSQL. Defaults: localhost, 5432.

.PARAMETER SkipTests
    Build only; do not run the test suite.

.EXAMPLE
    .\scripts\bootstrap-dev.ps1
    .\scripts\bootstrap-dev.ps1 -InitDatabase
    .\scripts\bootstrap-dev.ps1 -PgPort 5433 -SkipTests
    .\scripts\bootstrap-dev.ps1 -WithDocker
#>
[CmdletBinding()]
param(
    [switch]$InitDatabase,
    [switch]$WithDocker,
    [string]$PgHost = 'localhost',
    [int]$PgPort = 5432,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Test-TcpPort([string]$TargetHost, [int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($TargetHost, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne(2000, $false)) { return $false }
        $client.EndConnect($async)
        return $true
    }
    catch { return $false }
    finally { $client.Close() }
}

# ---- 1. .NET SDK ------------------------------------------------------------
Write-Step "Checking prerequisites"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0"
}
$sdks = & dotnet --list-sdks
$hasNet10 = $sdks | Where-Object { $_ -match '^10\.0\.' }
if (-not $hasNet10) {
    throw ".NET 10 SDK is required (global.json pins 10.0.x). Installed:`n$($sdks -join "`n")"
}
Write-Host "  dotnet SDK: $((& dotnet --version).Trim())"

# ---- 2. PostgreSQL ------------------------------------------------------------
if ($WithDocker) {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw "Docker not found, but -WithDocker was requested. Install Docker Desktop or drop the switch to use a native PostgreSQL."
    }
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) { throw "Docker daemon is not running." }

    Write-Step "Starting PostgreSQL 16 + Caddy (deploy/docker-compose.dev.yml)"
    & docker compose -f deploy/docker-compose.dev.yml up -d
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed." }

    Write-Host "  waiting for PostgreSQL to report healthy..."
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $state = (& docker inspect --format '{{.State.Health.Status}}' sangam-postgres 2>$null)
    } while ($state -ne 'healthy' -and (Get-Date) -lt $deadline)
    if ($state -ne 'healthy') { throw "PostgreSQL did not become healthy within 60 s (state: $state)." }
    Write-Host "  postgres (container): healthy; init.sql ran on first start"
}
else {
    Write-Step "Looking for PostgreSQL at ${PgHost}:${PgPort}"
    $pgUp = Test-TcpPort -TargetHost $PgHost -Port $PgPort
    if ($pgUp) {
        Write-Host "  postgres: listening on ${PgHost}:${PgPort}"
        if (Get-Command Get-Service -ErrorAction SilentlyContinue) {
            $service = Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($service) { Write-Host "  service: $($service.Name) ($($service.Status))" }
        }
    }
    else {
        Write-Warning "No PostgreSQL server answered on ${PgHost}:${PgPort}. PR-01 builds without one; PR-02 onwards needs PostgreSQL 16 (native install: https://www.postgresql.org/download/windows/ , or re-run with -WithDocker). Use -PgPort if your instance listens elsewhere."
    }

    if ($InitDatabase) {
        if (-not $pgUp) { throw "-InitDatabase requested but PostgreSQL is not reachable." }
        $psql = Get-Command psql -ErrorAction SilentlyContinue
        if (-not $psql) {
            $candidate = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
            if ($candidate) { $psql = $candidate } else { throw "psql not found on PATH or under C:\Program Files\PostgreSQL. Add the PostgreSQL bin folder to PATH, or run deploy\postgres\init.sql from pgAdmin's Query Tool (as postgres, with 'autocommit' on)." }
        }
        Write-Step "Creating sangam_identity role and database (deploy/postgres/init.sql)"
        & $psql.Source -h $PgHost -p $PgPort -U postgres -d postgres -v ON_ERROR_STOP=1 -f 'deploy/postgres/init.sql'
        if ($LASTEXITCODE -ne 0) { throw "psql reported an error running init.sql." }
        Write-Host "  database: sangam_identity ready (role sangam_identity, dev password sangam_dev)"
    }
}

# ---- 3. Clean, restore, build ------------------------------------------------
Write-Step "Cleaning bin/obj"
Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force

Write-Step "Restoring packages"
& dotnet restore Sangam.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

Write-Step "Building (Release, warnings are errors)"
& dotnet build Sangam.sln --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

# ---- 4. Tests ---------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step "Running tests"
    & dotnet test Sangam.sln --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }
}

# ---- 5. Next steps ----------------------------------------------------------
Write-Step "Setup complete"
$dbHint = if ($InitDatabase -or $WithDocker) { "" } else { "  Create the database once:  .\scripts\bootstrap-dev.ps1 -InitDatabase   (PR-02 onwards)`n" }
Write-Host @"

  Run the hosts (each in its own terminal):
    dotnet run --project src/Sangam.Identity.Server     ->  http://localhost:5100
    dotnet run --project src/Sangam.SelfService.Web     ->  http://localhost:5200
    dotnet run --project src/Sangam.Admin.Web           ->  http://localhost:5300

$dbHint  Connection string (dev):  Host=${PgHost};Port=${PgPort};Database=sangam_identity;Username=sangam_identity;Password=sangam_dev
"@
