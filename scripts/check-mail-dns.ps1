<#
.SYNOPSIS
    Checks that a sending domain has SPF, DKIM and DMARC records before Sangam sends mail (PR-09, D-B).

.DESCRIPTION
    Sangam hands its mail to Anjal through Anjal's API (D-B; not SMTP). Anjal sends it from its own
    servers and signs it with DKIM for the sending domain, so for codes to arrive the domain must
    publish an SPF record that includes Anjal's sending servers, a DMARC record, and the DKIM public
    key for Anjal's selector. The selector and Anjal's SPF include are Anjal's (OI-027).
    Optionally pass -AnjalSpf (for example 'include:_spf.anjalmail.com' or 'ip4:203.0.113.10') to
    check that the SPF record actually authorises Anjal.

.EXAMPLE
    .\scripts\check-mail-dns.ps1 -Domain sangamid.in -DkimSelector anjal -AnjalSpf include:_spf.anjalmail.com
#>
param(
    [Parameter(Mandatory = $true)][string]$Domain,
    [Parameter(Mandatory = $true)][string]$DkimSelector,
    [string]$AnjalSpf
)

$ok = $true

function Get-Txt([string]$name) {
    try {
        (Resolve-DnsName -Name $name -Type TXT -ErrorAction Stop | Where-Object { $_.Strings }) |
            ForEach-Object { $_.Strings -join '' }
    } catch {
        @()
    }
}

$spf = Get-Txt $Domain | Where-Object { $_ -like 'v=spf1*' }
if ($spf) { Write-Host "SPF   OK   $spf" } else { Write-Host "SPF   MISSING: publish a TXT record 'v=spf1 ...' on $Domain"; $ok = $false }
if ($spf -and $AnjalSpf) {
    if ($spf -like "*$AnjalSpf*") { Write-Host "SPF   OK   authorises Anjal ($AnjalSpf)" } else { Write-Host "SPF   WRONG: the record does not contain '$AnjalSpf', so mail Anjal sends for $Domain fails SPF"; $ok = $false }
}

$dmarc = Get-Txt "_dmarc.$Domain" | Where-Object { $_ -like 'v=DMARC1*' }
if ($dmarc) { Write-Host "DMARC OK   $dmarc" } else { Write-Host "DMARC MISSING: publish a TXT record 'v=DMARC1; p=...' on _dmarc.$Domain"; $ok = $false }

$dkim = Get-Txt "$DkimSelector._domainkey.$Domain" | Where-Object { $_ -like '*p=*' }
if ($dkim) { Write-Host "DKIM  OK   selector '$DkimSelector' publishes a key" } else { Write-Host "DKIM  MISSING: no key at $DkimSelector._domainkey.$Domain"; $ok = $false }

if ($ok) {
    Write-Host ''
    Write-Host 'All three records are published. Now have Sangam send one real code through Anjal to a real'
    Write-Host 'inbox and check that the headers show spf=pass, dkim=pass and dmarc=pass (go-live checklist).'
    exit 0
}

exit 1
