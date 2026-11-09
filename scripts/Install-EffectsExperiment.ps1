<#
.SYNOPSIS
Manual in-game test of the effect experiment (scripts\New-EffectsExperiment.cs): installs the patched
system_season packages, shows their state, or restores the originals.

.DESCRIPTION
Only for a hand-run test of a build from work\ (e.g. a probe build). The launcher installs the effect packs itself
and treats an experiment build in the client as an unknown version, so restore it with -Restore before using them.
Install replaces a client file only when it is byte-identical to the copy in work\original-effects that the
experiment was built from, or to another build of the experiment (listed in builds.txt in the source dir or a sibling
dir such as work\out\effects-probe, which the build script keeps). -Status changes nothing: it tells for each file whether the client holds the original, the
current patch, an older build or something else (run it after L2Updater to see whether the updater put the
originals back). Restore puts the original back only where the client holds the current or an older build.
Install and Restore refuse to run while the game is running.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File scripts/Install-EffectsExperiment.ps1 -ClientRoot 'E:\L2 Dex'
powershell -ExecutionPolicy Bypass -File scripts/Install-EffectsExperiment.ps1 -ClientRoot 'E:\L2 Dex' -Status
powershell -ExecutionPolicy Bypass -File scripts/Install-EffectsExperiment.ps1 -ClientRoot 'E:\L2 Dex' -Restore
powershell -ExecutionPolicy Bypass -File scripts/Install-EffectsExperiment.ps1 -ClientRoot 'E:\L2 Dex' -Source work\out\effects-probe
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory = $true)]
    [string] $ClientRoot,

    [string] $Source,

    [Parameter(ParameterSetName = 'Restore')]
    [switch] $Restore,

    [Parameter(ParameterSetName = 'Status')]
    [switch] $Status
)

$ErrorActionPreference = 'Stop'

function Get-Sha256([string] $Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

# Windows PowerShell 5.1 has no $PSScriptRoot in parameter defaults, so the default is resolved here.
if (-not $Source) {
    $Source = Join-Path $PSScriptRoot '..\work\out\effects-test'
}

$sourceDir = (Resolve-Path -LiteralPath $Source).Path
$originalsDir = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\work\original-effects')).Path
$systemDir = Join-Path (Resolve-Path -LiteralPath $ClientRoot).Path 'system_season'

if (-not (Test-Path -LiteralPath (Join-Path $systemDir 'LineageEffect.u'))) {
    throw "No system_season\LineageEffect.u under '$ClientRoot' - not a game client folder."
}

if (-not $Status) {
    $running = Get-Process | Where-Object { $_.ProcessName -match '^L2(\.bin)?$' }
    if ($running) {
        throw "The game is running (process $($running[0].ProcessName), id $($running[0].Id)). Close it first."
    }
}

$patchedFiles = Get-ChildItem -LiteralPath $sourceDir -Filter '*.u' -File
if (-not $patchedFiles) {
    throw "No .u files in $sourceDir. Build them first: dotnet run scripts/New-EffectsExperiment.cs -- work/original-effects work/out/effects-test"
}

# "<file name> <sha256>" per line, every build the build script has written to this dir or a sibling one
# (e.g. work\out\effects-test and work\out\effects-probe), so the client can switch between them.
$builds = @{}
foreach ($dir in Get-ChildItem -LiteralPath (Split-Path -Parent $sourceDir) -Directory) {
    $buildList = Join-Path $dir.FullName 'builds.txt'
    if (-not (Test-Path -LiteralPath $buildList)) {
        continue
    }

    foreach ($line in Get-Content -LiteralPath $buildList) {
        $parts = $line -split ' '
        if ($parts.Count -eq 2) {
            $builds["$($parts[0])|$($parts[1].ToUpperInvariant())"] = $true
        }
    }
}

foreach ($patched in $patchedFiles) {
    $target = Join-Path $systemDir $patched.Name
    $original = Join-Path $originalsDir $patched.Name
    if (-not (Test-Path -LiteralPath $target)) {
        throw "Client has no system_season\$($patched.Name)."
    }

    if (-not (Test-Path -LiteralPath $original)) {
        throw "No original copy work\original-effects\$($patched.Name) to compare with."
    }

    $currentHash = Get-Sha256 $target
    $originalHash = Get-Sha256 $original
    $patchedHash = Get-Sha256 $patched.FullName
    $olderBuild = ($currentHash -ne $patchedHash) -and $builds.ContainsKey("$($patched.Name)|$currentHash")

    if ($Status) {
        if ($currentHash -eq $patchedHash) {
            Write-Host "$($patched.Name): PATCH installed."
        }
        elseif ($currentHash -eq $originalHash) {
            Write-Host "$($patched.Name): ORIGINAL."
        }
        elseif ($olderBuild) {
            Write-Host "$($patched.Name): OLDER BUILD of this experiment installed."
        }
        else {
            Write-Warning "$($patched.Name): neither the original nor a build of this experiment (server update?)."
        }

        continue
    }

    if ($Restore) {
        if ($currentHash -eq $originalHash) {
            Write-Host "$($patched.Name): already original."
        }
        elseif ($currentHash -eq $patchedHash -or $olderBuild) {
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

    if ($currentHash -ne $originalHash -and -not $olderBuild) {
        throw "$($patched.Name): client file is neither the original in work\original-effects nor a build of this experiment (server update?). Copy the new file there and rebuild first."
    }

    Copy-Item -LiteralPath $patched.FullName -Destination $target -Force
    if ((Get-Sha256 $target) -ne $patchedHash) {
        throw "$($patched.Name): install verification failed."
    }

    Write-Host "$($patched.Name): installed patch."
}
