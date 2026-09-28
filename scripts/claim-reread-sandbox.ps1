#Requires -Version 7.0

<#
.SYNOPSIS
Headless dedicated-server check that claim re-reads survive a world save during the sweep's
load pass (Session 110, 0.4.128).

.DESCRIPTION
The owner's 2026-09-21 log showed claim refresh turning itself off for the session: an
autosave landed while the savegame sweep was still loading, the re-read answers were never
collected, three batches timed out, and "The savegame read path stopped answering" followed.

This runs one Vintage Horizons build on a disposable copy of the frozen bodanboys seed (whose
server cache predates fingerprints, so the load pass is long) with no player connected and a
short autosave interval, then stops the server cleanly. Everything lives under
.testdata/claim-reread/<Profile>; nothing outside it is written.

-Fresh restores the seed save and server cache before the run. Without it the profile's
existing copy is reused, which is how a second start of the same world is measured.

Examples:
  pwsh scripts/claim-reread-sandbox.ps1 -Profile control -Version 0.4.127 -Fresh
  pwsh scripts/claim-reread-sandbox.ps1 -Profile fixed -Version 0.4.128 -Fresh
  pwsh scripts/claim-reread-sandbox.ps1 -Profile fixed -Version 0.4.128 -Minutes 4

-WithoutMod runs the same world with no mods at all, so terrain changes while Vintage Horizons
is absent (terrain-change plan acceptance tests 2, 3 and 16):
  pwsh scripts/claim-reread-sandbox.ps1 -Profile fixed -WithoutMod -Minutes 6

-CrashAfterSeconds kills this script's own server process abruptly at that point instead of
stopping it, for acceptance test 5 (crash mid-session, restart, converge):
  pwsh scripts/claim-reread-sandbox.ps1 -Profile fixed -Minutes 5 -CrashAfterSeconds 170
#>

[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$Profile = 'claim-reread',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [ValidateRange(1, 60)]
    [int]$Minutes = 12,
    [ValidateRange(15, 3600)]
    [int]$AutoSaveSeconds = 120,
    [ValidateRange(1024, 65535)]
    [int]$Port = 42433,
    [switch]$Fresh,
    [switch]$WithoutMod,
    [ValidateRange(0, 3600)]
    [int]$CrashAfterSeconds = 0,
    [string]$GamePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$base = Join-Path $repoRoot '.testdata\claim-reread'
$dataPath = Join-Path $base $Profile
$seedDir = Join-Path $repoRoot '.testdata\profiles\sweep-ab\seed'
$seedSave = Join-Path $seedDir 'bodanboys.vcdbs'
$seedCache = Join-Path $seedDir '6bab2982-5cea-475f-b0f8-7d338748aadd-server.db'
$GamePath = if ($GamePath) { [IO.Path]::GetFullPath($GamePath) }
    elseif ($env:VINTAGE_STORY) { [IO.Path]::GetFullPath($env:VINTAGE_STORY) }
    else { Join-Path $env:APPDATA 'Vintagestory' }

if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'VintageHorizons\VintageHorizons.csproj')
    $Version = ([string]($project.Project.PropertyGroup.Version | Where-Object { $_ } |
        Select-Object -First 1)).Trim()
}
$zip = Join-Path $repoRoot "dist\vintagehorizons_$Version.zip"

function Assert-UnderBase([string]$Path) {
    $root = [IO.Path]::GetFullPath($base).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside $root : $full"
    }
}

function Get-GameProcesses {
    $names = @('dotnet.exe', 'Vintagestory.exe', 'VintagestoryServer.exe')
    @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $names -contains $_.Name } |
        Where-Object { ([string]$_.CommandLine).IndexOf('Vintagestory', [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

$running = @(Get-GameProcesses)
if ($running.Count -gt 0) {
    $list = ($running | ForEach-Object { "$($_.Name) pid $($_.ProcessId)" }) -join ', '
    throw "A Vintage Story process is already running ($list); not starting a test server beside it."
}
if (-not $WithoutMod -and -not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "Missing build: $zip" }
if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'VintagestoryServer.dll') -PathType Leaf)) {
    throw "No Vintage Story server at $GamePath"
}

Assert-UnderBase $dataPath
$saveTarget = Join-Path $dataPath 'Saves\default.vcdbs'
$cacheDir = Join-Path $dataPath 'ModData\vintagehorizons'
$cacheTarget = Join-Path $cacheDir ([IO.Path]::GetFileName($seedCache))

if ($Fresh -or -not (Test-Path -LiteralPath $saveTarget)) {
    foreach ($seed in @($seedSave, $seedCache)) {
        $wal = Get-Item -LiteralPath "$seed-wal" -ErrorAction SilentlyContinue
        if ($null -ne $wal -and $wal.Length -gt 0) { throw "The seed has a non-empty WAL: $($wal.FullName)" }
    }
    if (Test-Path -LiteralPath $dataPath) { Remove-Item -LiteralPath $dataPath -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path $saveTarget), $cacheDir -Force | Out-Null
    Copy-Item -LiteralPath $seedSave -Destination $saveTarget
    Copy-Item -LiteralPath $seedCache -Destination $cacheTarget
    Write-Host "  restored the seed save and server cache into $dataPath"
}

# Only the build under test: the sweep never generates, so the worldgen mods the seed was
# made with are not needed to load it (the sweep A/B ran the same way).
$modsDir = Join-Path $dataPath 'Mods'
if (Test-Path -LiteralPath $modsDir) { Remove-Item -LiteralPath $modsDir -Recurse -Force }
New-Item -ItemType Directory -Path $modsDir, (Join-Path $dataPath 'ModConfig'), (Join-Path $dataPath 'tmp') -Force | Out-Null
$label = if ($WithoutMod) { 'no-mod' } else { $Version }
if (-not $WithoutMod) { Copy-Item -LiteralPath $zip -Destination $modsDir }

@{
    EnableCapture = $true
    EnableServing = $false
    SweepSavegame = $true
    SweepRadiusChunks = 128
    SweepColumnsPerSecond = 16
    RefreshColumnsPerSecond = 1
    AuditColumnsPerSecond = 16
    EnableGenerateCommand = $false
    EnableWorldGeneration = $false
    PregenRadiusChunks = 0
    EnableSilhouettes = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataPath 'ModConfig\vintagehorizons-server.json') -Encoding utf8

@{
    ServerAutoSave = $AutoSaveSeconds
    ServerMagicNumVersion = '1.3'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataPath 'servermagicnumbers.json') -Encoding utf8

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$consolePath = Join-Path $dataPath "console-$label-$stamp.log"
$pidPath = Join-Path $dataPath 'server.pid'

$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = 'dotnet'
$psi.WorkingDirectory = $GamePath
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.ArgumentList.Add('VintagestoryServer.dll')
$psi.ArgumentList.Add('--dataPath')
$psi.ArgumentList.Add($dataPath)
$psi.ArgumentList.Add("--withconfig={ Port: $Port, VerifyPlayerAuth: false, WhitelistMode: 'off', AdvertiseServer: false }")
$psi.Environment['TEMP'] = Join-Path $dataPath 'tmp'
$psi.Environment['TMP'] = Join-Path $dataPath 'tmp'

$process = [Diagnostics.Process]::new()
$process.StartInfo = $psi
$clock = [Diagnostics.Stopwatch]::StartNew()
$started = $false
$ready = $false
$stopSent = $false
$gaveUp = $false
$saves = 0
$lastClaims = $null

try {
    if (-not $process.Start()) { throw 'The server process did not start.' }
    $started = $true
    $process.StandardInput.AutoFlush = $true
    Set-Content -LiteralPath $pidPath -Value $process.Id
    Write-Host "  server pid $($process.Id), build $label, autosave every ${AutoSaveSeconds}s, $Minutes min"
    $stdoutTask = $process.StandardOutput.ReadLineAsync()
    $stderrTask = $process.StandardError.ReadLineAsync()

    while (-not $process.HasExited) {
        while ($null -ne $stdoutTask -and $stdoutTask.IsCompleted) {
            $line = $stdoutTask.GetAwaiter().GetResult()
            if ($null -eq $line) { $stdoutTask = $null; break }
            Add-Content -LiteralPath $consolePath -Value $line -Encoding utf8
            if (-not $ready -and $line.Contains('Dedicated Server now running', [StringComparison]::Ordinal)) {
                $ready = $true
                $clock.Restart()
                Write-Host '  server ready'
            }
            if ($line.Contains('Autosaving game world', [StringComparison]::Ordinal)) {
                $saves++
                Write-Host ("  {0,5:0}s autosave {1}" -f $clock.Elapsed.TotalSeconds, $saves)
            }
            if ($line.Contains('stopped answering requests to re-read', [StringComparison]::Ordinal)) {
                $gaveUp = $true
                Write-Host ("  {0,5:0}s CLAIM REFRESH GAVE UP" -f $clock.Elapsed.TotalSeconds)
            }
            if ($line -match 'claims (?<n>\d+) recorded \((?<after>\d+) read back after a world save, (?<start>\d+) from the startup reading\)') {
                $claims = "$($Matches.n) recorded, $($Matches.after) after a save, $($Matches.start) from startup"
                if ($claims -ne $lastClaims) {
                    $lastClaims = $claims
                    Write-Host ("  {0,5:0}s claims: {1}" -f $clock.Elapsed.TotalSeconds, $claims)
                }
            }
            if ($line.Contains('[vintagehorizons] Savegame sweep', [StringComparison]::Ordinal)) {
                $text = $line -replace '^.*\[vintagehorizons\]\s*', ''
                Write-Host ("  {0,5:0}s {1}" -f $clock.Elapsed.TotalSeconds, $text.Substring(0, [Math]::Min(150, $text.Length)))
            }
            $stdoutTask = $process.StandardOutput.ReadLineAsync()
        }
        while ($null -ne $stderrTask -and $stderrTask.IsCompleted) {
            $line = $stderrTask.GetAwaiter().GetResult()
            if ($null -eq $line) { $stderrTask = $null; break }
            Add-Content -LiteralPath $consolePath -Value "ERR $line" -Encoding utf8
            $stderrTask = $process.StandardError.ReadLineAsync()
        }

        if ($CrashAfterSeconds -gt 0 -and $ready -and $clock.Elapsed.TotalSeconds -ge $CrashAfterSeconds) {
            # Only the exact process this script started, and only in the disposable copy.
            Write-Host ("  {0,5:0}s CRASHING the server (forced kill of pid {1})" -f $clock.Elapsed.TotalSeconds, $process.Id)
            $process.Kill()
            $process.WaitForExit(30000) | Out-Null
            break
        }
        if (-not $stopSent -and $ready -and $clock.Elapsed.TotalMinutes -ge $Minutes) {
            Write-Host '  time up, sending /stop'
            $process.StandardInput.WriteLine('/stop')
            $stopSent = $true
            $stopClock = [Diagnostics.Stopwatch]::StartNew()
        }
        if (-not $ready -and $clock.Elapsed.TotalMinutes -ge 10) { throw 'The server did not report ready within 10 minutes.' }
        if ($stopSent -and $stopClock.Elapsed.TotalSeconds -ge 300) { throw 'The server did not stop within 5 minutes of /stop.' }
        Start-Sleep -Milliseconds 200
    }
    if ($process.HasExited) { Write-Host "  server exited with code $($process.ExitCode)" }
} catch {
    if ($started -and -not $process.HasExited) {
        try { $process.StandardInput.WriteLine('/stop') } catch { }
        if (-not $process.WaitForExit(180000)) {
            Write-Warning "Sandbox server $($process.Id) did not stop; force-stopping the disposable copy."
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    throw
} finally {
    Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
    $process.Dispose()
}

Write-Host ''
Write-Host "Result for $label ($Profile): $saves autosaves, claim refresh gave up: $gaveUp"
Write-Host "  last claims line: $lastClaims"
Write-Host "  console: $consolePath"
Write-Host "  server log: $(Join-Path $dataPath 'Logs\server-main.log')"
