<#
.SYNOPSIS
Installs patched icon packages (.utx) into the client's SysTextures folder, or restores the originals.

.DESCRIPTION
Install replaces a client file only when it is byte-identical to the copy in work\original that the patch
was built from; otherwise (e.g. the server updated it) it stops, so a newer server file is never overwritten.
Restore puts the original back only where the client still holds the patched file from -Source.
Both refuse to run while the game is running.

.EXAMPLE
.\Install-IconPatch.ps1 -Source ..\work\out\test-corners
.\Install-IconPatch.ps1 -Source ..\work\out\test-corners -Restore
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Source,

    [switch] $Restore,

    [string] $ClientRoot = (Join-Path $PSScriptRoot '..\..')
)

$ErrorActionPreference = 'Stop'

function Get-Sha256([string] $Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

$sourceDir = (Resolve-Path -LiteralPath $Source).Path
$originalsDir = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\work\original')).Path
$sysTextures = Join-Path (Resolve-Path -LiteralPath $ClientRoot).Path 'SysTextures'

if (-not (Test-Path -LiteralPath (Join-Path $sysTextures 'Icon.utx'))) {
    throw "No SysTextures\Icon.utx under '$ClientRoot' - not a game client folder."
}

$running = Get-Process | Where-Object { $_.ProcessName -match '^L2(\.bin)?$' }
if ($running) {
    throw "The game is running (process $($running[0].ProcessName), id $($running[0].Id)). Close it first."
}

$patchedFiles = Get-ChildItem -LiteralPath $sourceDir -Filter '*.utx' -File
if (-not $patchedFiles) {
    throw "No .utx files in $sourceDir."
}

foreach ($patched in $patchedFiles) {
    $target = Join-Path $sysTextures $patched.Name
    $original = Join-Path $originalsDir $patched.Name
    if (-not (Test-Path -LiteralPath $target)) {
        throw "Client has no SysTextures\$($patched.Name)."
    }

    if (-not (Test-Path -LiteralPath $original)) {
        throw "No original copy work\original\$($patched.Name) to compare with."
    }

    $currentHash = Get-Sha256 $target
    $originalHash = Get-Sha256 $original
    $patchedHash = Get-Sha256 $patched.FullName

    if ($Restore) {
        if ($currentHash -eq $originalHash) {
            Write-Host "$($patched.Name): already original."
        }
        elseif ($currentHash -eq $patchedHash) {
            Copy-Item -LiteralPath $original -Destination $target -Force
            if ((Get-Sha256 $target) -ne $originalHash) {
                throw "$($patched.Name): restore verification failed."
            }

            Write-Host "$($patched.Name): restored original."
        }
        else {
            Write-Warning "$($patched.Name): client file is neither the original nor this patch (server update?). Left untouched."
        }

        continue
    }

    if ($currentHash -eq $patchedHash) {
        Write-Host "$($patched.Name): patch already installed."
        continue
    }

    if ($currentHash -ne $originalHash) {
        throw "$($patched.Name): client file differs from work\original (server update or another patch). Rebuild the patch from the current file first."
    }

    Copy-Item -LiteralPath $patched.FullName -Destination $target -Force
    if ((Get-Sha256 $target) -ne $patchedHash) {
        throw "$($patched.Name): install verification failed."
    }

    Write-Host "$($patched.Name): installed patch."
}
