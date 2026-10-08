<#
.SYNOPSIS
    Checks that a sending domain has SPF, DKIM and DMARC records before Sangam sends mail (PR-09).

.DESCRIPTION
    Sangam submits its mail to Anjal, which signs it with DKIM. For codes to arrive, the sending
    domain must publish SPF and DMARC records and the DKIM public key for Anjal's selector.
    The DKIM selector is Anjal's and is supplied by the founder (OI-027).

.EXAMPLE
    .\scripts\check-mail-dns.ps1 -Domain sangamid.in -DkimSelector anjal
#>
param(
    [Parameter(Mandatory = $true)][string]$Domain,
    [Parameter(Mandatory = $true)][string]$DkimSelector
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

$dmarc = Get-Txt "_dmarc.$Domain" | Where-Object { $_ -like 'v=DMARC1*' }
if ($dmarc) { Write-Host "DMARC OK   $dmarc" } else { Write-Host "DMARC MISSING: publish a TXT record 'v=DMARC1; p=...' on _dmarc.$Domain"; $ok = $false }

$dkim = Get-Txt "$DkimSelector._domainkey.$Domain" | Where-Object { $_ -like '*p=*' }
if ($dkim) { Write-Host "DKIM  OK   selector '$DkimSelector' publishes a key" } else { Write-Host "DKIM  MISSING: no key at $DkimSelector._domainkey.$Domain"; $ok = $false }

if ($ok) {
    Write-Host ''
    Write-Host 'All three records are published. Now send one real code to a real inbox and check that'
    Write-Host 'the headers show spf=pass, dkim=pass and dmarc=pass (go-live checklist).'
    exit 0
}

exit 1
