<#
.SYNOPSIS
    End-to-end check of the launcher self-update with real processes, on a FAKE client.

.DESCRIPTION
    Two scenarios, each in its own folder under -Work:
      update    launcher 1.0.0 updates itself to 1.0.1 (hand-off, swap, restart, cleanup);
      rollback  launcher 1.0.0 updates itself to a 1.0.1 that crashes at startup; the crash starts 1.0.0 again
                from .old, which puts itself back and skips 1.0.1 from then on.
    The source (scripts/New-DevSource.cs, throwaway key) is served by "python -m http.server" on 127.0.0.1:
    the updater accepts plain http only on loopback. "--smoke-update" makes the old launcher click "Aktualizuj";
    the launchers started by the updater run without it and are closed by this script.

    Needs three single-file builds in <Work>\builds\<name>\CpLauncher.exe (same flags as in src\CpLauncher\README.md):
      1.0.0          (default version)
      1.0.1          -p:Version=1.0.1
      1.0.1-broken   -p:Version=1.0.1 -p:CpLauncherSimulateStartupFailure=true   (test build, throws at startup)

    Never point this at a real client: everything it writes is under -Work, which must be inside a work\ folder.

.EXAMPLE
    pwsh -File scripts/Test-SelfUpdate.ps1 -Original C:\Lineage2Dex\.modding\work\original\Icon.utx
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Original,
    [string]$Work = 'work\e2e-update',
    [int]$Port = 8765,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$work = [IO.Path]::GetFullPath((Join-Path $repo $Work))
if ($work -notmatch '\\work(\\|$)') {
    throw "$work is not inside a work\ folder."
}

$builds = Join-Path $work 'builds'
$results = [Collections.Generic.List[string]]::new()

function Assert-That([bool]$condition, [string]$message) {
    if (-not $condition) {
        throw "FAILED: $message"
    }

    $results.Add("ok  $message")
    Write-Host "ok  $message"
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

function Get-LauncherProcesses([string]$folder) {
    return @(Get-CimInstance Win32_Process -Filter "Name LIKE 'CpLauncher%'" | Where-Object {
        $null -ne $_.ExecutablePath -and $_.ExecutablePath.StartsWith($folder, [StringComparison]::OrdinalIgnoreCase)
    })
}

function Wait-Until([scriptblock]$condition, [string]$what) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (& $condition)) {
        if ((Get-Date) -gt $deadline) {
            throw "Timed out after $TimeoutSeconds s waiting for: $what"
        }

        Start-Sleep -Milliseconds 250
    }
}

function Read-Text([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        return ''
    }

    # Shared for writing, so the launcher can keep appending to its log meanwhile.
    try {
        $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            return [IO.StreamReader]::new($stream).ReadToEnd()
        }
        finally {
            $stream.Dispose()
        }
    }
    catch [IO.IOException] {
        return ''
    }
}

# Waits for the log to contain all of $expected; stops at once when an update step failed.
function Wait-ForLog([string]$path, [string[]]$expected, [scriptblock]$also, [string]$what) {
    Wait-Until {
        $text = Read-Text $path
        foreach ($marker in 'Update failed:', 'Hand-off failed:', 'Hand-off refused:', 'Rollback failed:', 'Rollback impossible:') {
            if ($text.Contains($marker)) {
                throw "An update step failed ($marker). Log:`n$text"
            }
        }

        foreach ($line in $expected) {
            if (-not $text.Contains($line)) {
                return $false
            }
        }

        return (& $also)
    } $what
}

function Close-Launchers([string]$folder) {
    foreach ($process in Get-LauncherProcesses $folder) {
        $running = Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $running) {
            [void]$running.CloseMainWindow()
        }
    }

    Wait-Until { @(Get-LauncherProcesses $folder).Count -eq 0 } "the launchers in $folder to close"
}

function Invoke-Scenario([string]$name, [string]$releaseBuild) {
    Write-Host "=== $name (release: $releaseBuild)"
    $root = Join-Path $work $name
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }

    Push-Location $repo
    try {
        dotnet run scripts/New-DevSource.cs -- $Original $root --launcher 1.0.1 (Join-Path $builds "$releaseBuild\CpLauncher.exe") | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw 'New-DevSource failed.'
        }
    }
    finally {
        Pop-Location
    }

    $app = Join-Path $root 'app'
    $exe = Join-Path $app 'CpLauncher.exe'
    $data = Join-Path $root 'data'
    $source = Join-Path $root 'source'
    $log = Join-Path $data 'launcher.log'
    New-Item -ItemType Directory -Path $app | Out-Null
    Copy-Item -LiteralPath (Join-Path $builds '1.0.0\CpLauncher.exe') -Destination $exe
    $arguments = @('--data', $data, '--client', (Join-Path $root 'client'), '--source', "http://127.0.0.1:$Port/", '--dev-public-key', (Join-Path $source 'dev-public.pem'))

    $server = Start-Process python -ArgumentList @('-I', '-m', 'http.server', "$Port", '--bind', '127.0.0.1', '--directory', $source) -PassThru -WindowStyle Hidden
    try {
        Wait-Until {
            try {
                (Invoke-WebRequest "http://127.0.0.1:$Port/manifest.json" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200
            }
            catch {
                $false
            }
        } 'the local source'

        $first = Join-Path $root 'report-first.txt'
        $started = Get-Date
        Start-Process -FilePath $exe -ArgumentList ($arguments + @('--smoke-update', $first)) -WorkingDirectory $app | Out-Null
        Wait-Until { (Read-Text $first).Contains('update_enabled=') } 'the first report'
        Assert-That ((Read-Text $first).Contains('update_enabled=yes')) "$name`: launcher 1.0.0 offers 1.0.1 and the button is enabled"
        return @{ Root = $root; App = $app; Exe = $exe; Log = $log; Data = $data; Arguments = $arguments; Started = $started }
    }
    catch {
        Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
        throw
    }
    finally {
        $script:server = $server
    }
}

function Assert-NoSideFiles([string]$exe, [string]$name) {
    foreach ($suffix in '.new', '.old', '.tmp') {
        Assert-That (-not (Test-Path -LiteralPath ($exe + $suffix))) "$name`: no $suffix left"
    }
}

function Test-SecondStart($scenario, [string]$name, [string]$expectedVersion) {
    $second = Join-Path $scenario.Root 'report-second.txt'
    Start-Process -FilePath $scenario.Exe -ArgumentList ($scenario.Arguments + @('--smoke-update', $second)) -WorkingDirectory $scenario.App | Out-Null
    Wait-Until { (Read-Text $second).Contains('update_enabled=') } 'the second report'
    Wait-Until { @(Get-LauncherProcesses $scenario.App).Count -eq 0 } 'the second launcher to close by itself'
    $text = Read-Text $second
    Assert-That ($text.Contains("version=wersja $expectedVersion")) "$name`: the next start runs $expectedVersion"
    Assert-That ($text.Contains('update=none')) "$name`: the next start offers no update"
    return $text
}

$running = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'CpLauncher%'")
if ($running.Count -gt 0) {
    throw "Close the running CpLauncher first (pid $($running.ProcessId -join ', ')): the single-instance lock is per Windows session."
}

foreach ($build in '1.0.0', '1.0.1', '1.0.1-broken') {
    if (-not (Test-Path -LiteralPath (Join-Path $builds "$build\CpLauncher.exe"))) {
        throw "Missing build $builds\$build\CpLauncher.exe (see the help of this script)."
    }
}

$hash100 = Get-Sha256 (Join-Path $builds '1.0.0\CpLauncher.exe')
$hash101 = Get-Sha256 (Join-Path $builds '1.0.1\CpLauncher.exe')
Assert-That ((Get-Item (Join-Path $builds '1.0.1\CpLauncher.exe')).VersionInfo.ProductVersion -like '1.0.1*') 'the 1.0.1 build says 1.0.1'
Assert-That ((Get-Item (Join-Path $builds '1.0.0\CpLauncher.exe')).VersionInfo.ProductVersion -like '1.0.0*') 'the 1.0.0 build says 1.0.0'

$server = $null
try {
    # --- 1.0.0 -> 1.0.1 ---
    $update = Invoke-Scenario 'update' '1.0.1'
    Wait-ForLog $update.Log @('Launcher 1.0.1 is up.') { -not (Test-Path -LiteralPath ($update.Exe + '.old')) } 'launcher 1.0.1 to start and clean up'
    $seconds = [math]::Round(((Get-Date) - $update.Started).TotalSeconds, 1)
    $text = Read-Text $update.Log
    Assert-That ((Get-Sha256 $update.Exe) -eq $hash101) 'update: CpLauncher.exe is now the 1.0.1 build'
    Assert-NoSideFiles $update.Exe 'update'
    Assert-That ($text.Contains('to install itself over 1.0.0')) 'update: the hand-off ran from .new'
    Assert-That ($text.Contains('Hand-off: launcher 1.0.1 installed and started.')) 'update: the hand-off installed 1.0.1'
    Assert-That ($text.Contains('(updated from 1.0.0)')) 'update: 1.0.1 was started with --updated-from 1.0.0'
    $processes = @(Get-LauncherProcesses $update.App)
    Assert-That ($processes.Count -eq 1 -and $processes[0].ExecutablePath -eq $update.Exe) 'update: exactly one launcher runs, from CpLauncher.exe'
    Write-Host "    1.0.0 started -> 1.0.1 up and cleaned: $seconds s"
    Close-Launchers $update.App
    [void](Test-SecondStart $update 'update' '1.0.1')
    $settings = Get-Content -Raw (Join-Path $update.Data 'settings.json') | ConvertFrom-Json
    Assert-That ($null -eq $settings.PSObject.Properties['skippedLauncherVersion'] -or $null -eq $settings.skippedLauncherVersion) 'update: no version is skipped'
    Stop-Process -Id $server.Id -ErrorAction SilentlyContinue

    # --- 1.0.0 -> broken 1.0.1 -> rollback to 1.0.0 ---
    $rollback = Invoke-Scenario 'rollback' '1.0.1-broken'
    Wait-ForLog $rollback.Log @('(rolled back from 1.0.1)') {
        $text = Read-Text $rollback.Log
        $back = $text.IndexOf('(rolled back from 1.0.1)')
        $text.IndexOf('Launcher 1.0.0 is up.', $back) -ge 0 -and -not (Test-Path -LiteralPath ($rollback.Exe + '.old')) -and -not (Test-Path -LiteralPath ($rollback.Exe + '.new'))
    } 'launcher 1.0.0 to come back and clean up'
    $seconds = [math]::Round(((Get-Date) - $rollback.Started).TotalSeconds, 1)
    $text = Read-Text $rollback.Log
    Assert-That ((Get-Sha256 $rollback.Exe) -eq $hash100) 'rollback: CpLauncher.exe is the 1.0.0 build again'
    Assert-NoSideFiles $rollback.Exe 'rollback'
    Assert-That ($text.Contains('Launcher 1.0.1 crashed: System.InvalidOperationException: Simulated startup failure')) 'rollback: 1.0.1 crashed at startup'
    Assert-That ($text.Contains('Rollback: launcher 1.0.0 was started to put itself back.')) 'rollback: 1.0.1 started 1.0.0 from .old'
    Assert-That ($text.Contains('to put itself back over 1.0.1')) 'rollback: the rollback hand-off ran from .old'
    Assert-That ($text.Contains('(rolled back from 1.0.1)')) 'rollback: 1.0.0 was started with --rolled-back-from 1.0.1'
    $settings = Get-Content -Raw (Join-Path $rollback.Data 'settings.json') | ConvertFrom-Json
    Assert-That ($settings.skippedLauncherVersion -eq '1.0.1') 'rollback: 1.0.1 is skipped in settings.json'
    Write-Host "    1.0.0 started -> 1.0.1 crashed -> 1.0.0 up and cleaned: $seconds s"
    Close-Launchers $rollback.App
    $second = Test-SecondStart $rollback 'rollback' '1.0.0'
    Assert-That ($second.Contains('pominięta')) 'rollback: the next start says 1.0.1 is skipped'
}
finally {
    if ($null -ne $server) {
        Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
    }

    foreach ($name in 'update', 'rollback') {
        $folder = Join-Path (Join-Path $work $name) 'app'
        foreach ($process in Get-LauncherProcesses $folder) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ''
Write-Host "All $($results.Count) checks passed."
