#Requires -Version 7.0

<#
.SYNOPSIS
Run the horizon-silhouette accelerator comparison on an isolated dedicated test server.

.DESCRIPTION
P0.3 of PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md.  The game process runs under a
repository-local, disposable data path, .testdata\silhouette-compare\<Profile>.  The script:

- copies the owner's enabled mods (every zip whose modid@version clientsettings.json does not
  disable) and ModConfig, plus the exact current Vintage Horizons zip;
- pins a Vintage Horizons server config that captures, serves, sweeps and generates nothing;
- starts the server with VINTAGEHORIZONS_SILHOUETTE_MEASURE=1, so it keeps the worldgen heights
  of every column it loads and offers players nothing.

It runs two sessions on one fresh world:

1. presample: /vhsilhouetteserver presample <Range> asks every accelerator for every cell of the
   square around the world centre before that land is generated, writes the answers to disk, and
   the server stops.  A second process, because a generator in the same process could reuse the
   samplers' cached region data and agree with them for the wrong reason.
2. compare: /wgen pregen <Range> generates the same square; once the queue has stayed empty,
   /vhsilhouetteserver compare <Range> measures the accelerators against the real heights, both
   as sampled now and as presampled, and writes a map of every cell.

Comparisons run while generation is idle, because whether the samplers are safe beside real
generation is unknown.  The report lines go to compare-report.txt, and
scripts/silhouette-compare-render.py draws the map as a side-by-side page.

-NoPresample skips the first session.  -PlanOnly lists the mods it would copy and launches nothing.

Examples:
  pwsh scripts/silhouette-compare.ps1 -PlanOnly
  pwsh scripts/silhouette-compare.ps1 -Profile p03-cold -Range 32
#>

[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$Profile = ('p03-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [ValidateRange(1, 48)]
    [int]$Range = 32,
    [ValidateRange(5, 240)]
    [int]$TimeoutMinutes = 60,
    [ValidateRange(1024, 65535)]
    [int]$Port = 42433,
    [string[]]$RequiredModIds = @('terraprety', 'watersheds', 'algernonsterrainsampler'),
    [switch]$NoPresample,
    [switch]$PlanOnly,
    [string]$GamePath,
    [string]$ModSource,
    [string]$ModConfigSource,
    [string]$ClientSettings
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repoRoot = Split-Path -Parent $PSScriptRoot
$compareBase = Join-Path $repoRoot '.testdata\silhouette-compare'
$profileRoot = Join-Path $compareBase $Profile
$dataPath = Join-Path $env:APPDATA 'VintagestoryData'
$GamePath = if ($GamePath) { [IO.Path]::GetFullPath($GamePath) } elseif ($env:VINTAGE_STORY) {
    [IO.Path]::GetFullPath($env:VINTAGE_STORY)
} else { Join-Path $env:APPDATA 'Vintagestory' }
$ModSource = if ($ModSource) { [IO.Path]::GetFullPath($ModSource) } else { Join-Path $dataPath 'Mods' }
$ModConfigSource = if ($ModConfigSource) { [IO.Path]::GetFullPath($ModConfigSource) } else {
    Join-Path $dataPath 'ModConfig'
}
$ClientSettings = if ($ClientSettings) { [IO.Path]::GetFullPath($ClientSettings) } else {
    Join-Path $dataPath 'clientsettings.json'
}
$effectiveRequiredModIds = @($RequiredModIds + 'vintagehorizons' | Sort-Object -Unique)

function Assert-UnderCompareBase {
    param([Parameter(Mandatory)][string]$Path)
    $root = [IO.Path]::GetFullPath($compareBase).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a silhouette-comparison operation outside $root : $full"
    }
}

function Get-GameProcesses {
    $names = @('dotnet.exe', 'Vintagestory.exe', 'VintagestoryServer.exe')
    @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $names -contains $_.Name } |
        Where-Object { ([string]$_.CommandLine).IndexOf(
                'Vintagestory', [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

function Assert-NoGameProcess {
    $running = @(Get-GameProcesses)
    if ($running.Count -gt 0) {
        $list = ($running | ForEach-Object { "$($_.Name) pid $($_.ProcessId)" }) -join ', '
        throw "A Vintage Story process is already running ($list). Close it before a test server starts."
    }
}

function Get-VintageHorizonsZip {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'VintageHorizons\VintageHorizons.csproj')
    $version = ([string]($project.Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1)).Trim()
    if (-not $version) { throw 'Could not read the Vintage Horizons project version.' }
    $zip = Join-Path $repoRoot "dist\vintagehorizons_$version.zip"
    if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) {
        throw "The exact current Vintage Horizons zip is missing: $zip"
    }
    return $zip
}

function Get-DisabledMods {
    if (-not (Test-Path -LiteralPath $ClientSettings -PathType Leaf)) {
        throw "Client settings not found, so the enabled mods are unknown: $ClientSettings"
    }
    $settings = Get-Content -LiteralPath $ClientSettings -Raw | ConvertFrom-Json
    return @($settings.stringListSettings.disabledMods | Where-Object { $_ })
}

# modid and version from a zip's modinfo.json, or $null when it has none that parses.
function Read-ModIdentity {
    param([Parameter(Mandatory)][IO.FileInfo]$File)
    $archive = [IO.Compression.ZipFile]::OpenRead($File.FullName)
    try {
        $entry = @($archive.Entries | Where-Object { $_.FullName -ieq 'modinfo.json' })[0]
        if ($null -eq $entry) { return $null }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    try { $info = $text | ConvertFrom-Json } catch { return $null }
    if (-not $info.modid) { return $null }
    return [pscustomobject]@{ Id = ([string]$info.modid).ToLowerInvariant(); Version = [string]$info.version }
}

# The owner's enabled mods. Vintage Horizons comes from the current build instead. A mod whose
# identity cannot be read is left out and named: copying it could enable something the owner
# switched off.
function Select-EnabledMods {
    $disabled = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in Get-DisabledMods) { [void]$disabled.Add([string]$entry) }
    $selected = @()
    $skipped = @()
    foreach ($item in Get-ChildItem -LiteralPath $ModSource -Force) {
        if ($item.Name -match '^vintagehorizons(?:_|\.|$)') { continue }
        $identity = if ($item -is [IO.FileInfo] -and $item.Extension -ieq '.zip') { Read-ModIdentity $item } else { $null }
        if ($null -eq $identity) {
            $skipped += "$($item.Name) (identity unreadable)"
        } elseif ($disabled.Contains("$($identity.Id)@$($identity.Version)")) {
            $skipped += "$($item.Name) (disabled)"
        } else {
            $selected += [pscustomobject]@{ File = $item; Id = $identity.Id; Version = $identity.Version }
        }
    }
    return [pscustomobject]@{ Selected = $selected; Skipped = $skipped }
}

function Assert-RequiredMods {
    param([Parameter(Mandatory)][object]$Mods)
    $ids = @($Mods.Selected | ForEach-Object { $_.Id }) + 'vintagehorizons'
    foreach ($required in $effectiveRequiredModIds) {
        if ($ids -notcontains $required) { throw "The owner's enabled mods include no $required." }
    }
}

function Initialize-Environment {
    param([Parameter(Mandatory)][object]$Mods)
    Assert-UnderCompareBase $profileRoot
    $modsTarget = Join-Path $profileRoot 'Mods'
    $configsTarget = Join-Path $profileRoot 'ModConfig'
    New-Item -ItemType Directory -Path $modsTarget, $configsTarget, (Join-Path $profileRoot 'tmp') -Force | Out-Null
    foreach ($mod in $Mods.Selected) { Copy-Item -LiteralPath $mod.File.FullName -Destination $modsTarget }
    if (Test-Path -LiteralPath $ModConfigSource -PathType Container) {
        foreach ($entry in Get-ChildItem -LiteralPath $ModConfigSource -Force) {
            Copy-Item -LiteralPath $entry.FullName -Destination $configsTarget -Recurse
        }
    }
    Copy-Item -LiteralPath (Get-VintageHorizonsZip) -Destination $modsTarget
    # Vintage Horizons runs nothing of its own: no capture, serving, sweep or generation.
    # Silhouettes stay automatic; the measuring switch is what keeps their heights here.
    @{
        EnableCapture = $false
        EnableServing = $false
        SweepSavegame = $false
        RefreshColumnsPerSecond = 0
        EnableGenerateCommand = $false
        EnableWorldGeneration = $false
        PregenRadiusChunks = 0
    } | ConvertTo-Json | Set-Content -LiteralPath (
        Join-Path $configsTarget 'vintagehorizons-server.json') -Encoding utf8
}

# One server session. 'presample' asks the accelerators about the square before it is generated;
# 'compare' generates the square, then compares. Both stop the server cleanly when done.
function Invoke-Server {
    param([Parameter(Mandatory)][ValidateSet('presample', 'compare')][string]$Session)
    $consolePath = Join-Path $profileRoot "console-$Session.log"
    $errorPath = Join-Path $profileRoot "console-$Session.err.log"
    $reportPath = Join-Path $profileRoot 'compare-report.txt'
    $pidPath = Join-Path $profileRoot 'server.pid'

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
    $psi.ArgumentList.Add($profileRoot)
    $psi.ArgumentList.Add("--withconfig={ Port: $Port, VerifyPlayerAuth: false, WhitelistMode: 'off', AdvertiseServer: false, DefaultRoleCode: 'admin' }")
    $psi.Environment['TEMP'] = Join-Path $profileRoot 'tmp'
    $psi.Environment['TMP'] = Join-Path $profileRoot 'tmp'
    $psi.Environment['VINTAGEHORIZONS_SILHOUETTE_MEASURE'] = '1'

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $expectedColumns = (2 * $Range + 1) * (2 * $Range + 1)
    $refusals = @('Horizon silhouettes are off on this server', 'No height accelerator is installed',
        'The server holds no real heights', 'is already running; its report will follow')
    $finishedLine = if ($Session -eq 'presample') { '[VintageHorizons] Accelerator presample finished' } else {
        '[VintageHorizons] Accelerator comparison finished' }
    $report = [Collections.Generic.List[string]]::new()
    $phase = 'startup'
    $zeroSamples = 0
    $nextPoll = 0.0
    $generationStarted = 0.0
    $stopSentAt = 0.0
    $failure = $null
    $finished = $false
    $started = $false

    function Send-ServerCommand([string]$Command, [switch]$Quiet) {
        $process.StandardInput.WriteLine($Command)
        Add-Content -LiteralPath $consolePath -Value "> $Command" -Encoding utf8
        if (-not $Quiet) { Write-Host "  sent $Command" }
    }

    function Request-Stop {
        Send-ServerCommand '/vhsilhouetteserver status'
        Send-ServerCommand '/stop'
        Set-Variable -Name stopSentAt -Scope 1 -Value $clock.Elapsed.TotalSeconds
        Set-Variable -Name phase -Scope 1 -Value 'stopping'
    }

    try {
        if (-not $process.Start()) { throw 'Vintage Story server process did not start.' }
        $started = $true
        $process.StandardInput.AutoFlush = $true
        Set-Content -LiteralPath $pidPath -Value $process.Id
        Write-Host "  $Session session: server pid $($process.Id), dataPath $profileRoot"
        $stdoutTask = $process.StandardOutput.ReadLineAsync()
        $stderrTask = $process.StandardError.ReadLineAsync()

        while (-not $process.HasExited) {
            while ($null -ne $stdoutTask -and $stdoutTask.IsCompleted) {
                $line = $stdoutTask.GetAwaiter().GetResult()
                if ($null -eq $line) { $stdoutTask = $null; break }
                Add-Content -LiteralPath $consolePath -Value $line -Encoding utf8
                if ($line.Contains('[VintageHorizons] Horizon silhouettes', [StringComparison]::Ordinal) -or
                    $line.Contains('[VintageHorizons] Accelerator ', [StringComparison]::Ordinal) -or
                    $line.Contains('Horizon silhouettes: measuring', [StringComparison]::Ordinal)) {
                    # The report is shareable: the server logs full file paths, so the machine's own
                    # folders are replaced (dev/DocCheck.ps1 rejects personal paths in text files).
                    $report.Add($line.Replace($profileRoot, '<profile>'))
                    Write-Host "  $line"
                }
                if ($line.Contains('Exception', [StringComparison]::OrdinalIgnoreCase)) { Write-Host "  log: $line" }
                if ($phase -in 'presampling', 'comparing') {
                    foreach ($refusal in $refusals) {
                        if ($line.Contains($refusal, [StringComparison]::Ordinal)) { $failure = "the $Session was refused: $refusal" }
                    }
                    if ($line.Contains($finishedLine, [StringComparison]::Ordinal)) { $finished = $true }
                    if ($finished -or $failure) { Request-Stop }
                }

                if ($phase -eq 'startup' -and $line.Contains('Dedicated Server now running', [StringComparison]::Ordinal)) {
                    Write-Host '  server ready'
                    if ($Session -eq 'presample') {
                        Send-ServerCommand "/vhsilhouetteserver presample $Range"
                        $phase = 'presampling'
                    } else {
                        Send-ServerCommand "/wgen pregen $Range"
                        $generationStarted = $clock.Elapsed.TotalSeconds
                        $phase = 'generating'
                    }
                } elseif ($phase -eq 'generating' -and
                    $line.Contains("Ok, $expectedColumns columns, generated!", [StringComparison]::Ordinal)) {
                    Write-Host '  pregen submitted its final batch; waiting for the queue to drain'
                    $zeroSamples = 0
                    $phase = 'draining'
                } elseif ($phase -eq 'draining' -and $line -match 'Currently (?<queue>[0-9]+) chunks in generation queue') {
                    if ([int]$Matches.queue -eq 0) { $zeroSamples++ } else { $zeroSamples = 0 }
                    if ($zeroSamples -ge 3) {
                        Write-Host ('  generation idle after {0:0}s' -f ($clock.Elapsed.TotalSeconds - $generationStarted))
                        Send-ServerCommand '/vhsilhouetteserver status'
                        Send-ServerCommand "/vhsilhouetteserver compare $Range"
                        $phase = 'comparing'
                    }
                }
                $stdoutTask = $process.StandardOutput.ReadLineAsync()
            }
            while ($null -ne $stderrTask -and $stderrTask.IsCompleted) {
                $line = $stderrTask.GetAwaiter().GetResult()
                if ($null -eq $line) { $stderrTask = $null; break }
                Add-Content -LiteralPath $errorPath -Value $line -Encoding utf8
                $stderrTask = $process.StandardError.ReadLineAsync()
            }

            if ($phase -eq 'draining' -and $clock.Elapsed.TotalSeconds -ge $nextPoll) {
                Send-ServerCommand '/debug chunk queue' -Quiet
                $nextPoll = $clock.Elapsed.TotalSeconds + 1
            }
            if ($phase -ne 'stopping' -and $clock.Elapsed.TotalMinutes -gt $TimeoutMinutes) {
                $failure = "no result within $TimeoutMinutes minutes (phase $phase)"
                Write-Warning $failure
                Request-Stop
            }
            if ($phase -eq 'stopping' -and $clock.Elapsed.TotalSeconds - $stopSentAt -gt 120) {
                Write-Warning "Exact sandbox process $($process.Id) did not stop after 120s; force-stopping the disposable copy."
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
            }
            Start-Sleep -Milliseconds 50
        }
        $process.WaitForExit()
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        $report | Add-Content -LiteralPath $reportPath -Encoding utf8

        $modLine = @(Get-Content -LiteralPath $consolePath |
            Where-Object { $_.Contains('Mods, sorted by dependency:', [StringComparison]::Ordinal) } |
            Select-Object -First 1)[0]
        foreach ($requiredMod in $effectiveRequiredModIds) {
            if (-not $modLine -or $modLine.IndexOf($requiredMod, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "The $Session server log does not prove that required mod '$requiredMod' loaded."
            }
        }
        if (-not (Select-String -LiteralPath $consolePath -SimpleMatch 'Horizon silhouettes: measuring heights only' -Quiet)) {
            throw "The $Session server log does not prove that the measuring switch took effect."
        }
        if ($failure) { throw $failure }
        if (-not $finished) { throw "The $Session server exited before it finished (phase $phase)." }
    } catch {
        if ($started -and -not $process.HasExited) {
            try { $process.StandardInput.WriteLine('/stop') } catch { }
            if (-not $process.WaitForExit(120000)) {
                Write-Warning "Exact sandbox process $($process.Id) did not stop after 120s; force-stopping the disposable copy."
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        if ($report.Count -gt 0) { $report | Add-Content -LiteralPath $reportPath -Encoding utf8 }
        throw
    } finally {
        $process.Dispose()
    }
}

$mods = Select-EnabledMods
Write-Host 'Enabled mods to copy:'
foreach ($mod in $mods.Selected) { Write-Host "  $($mod.Id) $($mod.Version)  ($($mod.File.Name))" }
Write-Host "  vintagehorizons from $(Get-VintageHorizonsZip)"
if ($mods.Skipped.Count -gt 0) {
    Write-Host 'Left out:'
    foreach ($entry in $mods.Skipped) { Write-Host "  $entry" }
}
Assert-RequiredMods $mods
if ($PlanOnly) { return }

Assert-NoGameProcess
if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'VintagestoryServer.dll') -PathType Leaf)) {
    throw "Vintage Story server not found: $GamePath"
}
if (Test-Path -LiteralPath $profileRoot) {
    throw "Profile already exists; choose another name rather than overwriting it: $profileRoot"
}
Initialize-Environment $mods
if (-not $NoPresample) { Invoke-Server -Session presample }
Invoke-Server -Session compare
Write-Host "  report: $(Join-Path $profileRoot 'compare-report.txt')"

$map = Get-ChildItem -LiteralPath (Join-Path $profileRoot 'ModData\vintagehorizons\silhouette-compare') -Filter '*-map-*.csv.gz' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$python = Get-Command python3, python -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $map) {
    Write-Warning 'The comparison wrote no map file.'
} elseif ($null -eq $python) {
    Write-Warning "Python is not installed, so the side-by-side was not drawn. Map: $($map.FullName)"
} else {
    & $python.Source (Join-Path $PSScriptRoot 'silhouette-compare-render.py') $map.FullName
    if ($LASTEXITCODE -ne 0) { throw "The side-by-side renderer failed with exit code $LASTEXITCODE" }
}
