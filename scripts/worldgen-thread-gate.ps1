#Requires -Version 7.0

<#
.SYNOPSIS
Prepare and run the deterministic 1-thread-versus-6-thread world-generation gate.

.DESCRIPTION
P0 of PLAN_WORLD_GENERATION_AND_LOD_COVERAGE.md.  Every game process runs under a
repository-local, disposable data path.  The script copies the active mod set, pins
Vintage Horizons so it captures but starts no generation of its own, changes only the
sandbox servermagicnumbers.json, samples named native-thread CPU/RAM/queue depth, stops
the server cleanly, and invokes worldgen-save-compare.py.

Actions are deliberately separate.  Plan is read-only.  Seed creates a fresh settled
world with the active TerraPrety and Watersheds versions.  Prepare clones that save into
the two test data paths.  Run launches the two servers sequentially and compares them.
Compare repeats only the offline comparison.

Examples:
  pwsh scripts/worldgen-thread-gate.ps1 -Action Plan -Profile p0-20260913
  pwsh scripts/worldgen-thread-gate.ps1 -Action Seed -Profile p0-20260913
  pwsh scripts/worldgen-thread-gate.ps1 -Action Prepare -Profile p0-20260913
  pwsh scripts/worldgen-thread-gate.ps1 -Action Run -Profile p0-20260913

An existing settled save can replace Seed:
  pwsh scripts/worldgen-thread-gate.ps1 -Action Prepare -Profile p0-main `
      -SeedSave "$env:APPDATA\VintagestoryData\Saves\my world.vcdbs"

-ChunkThreadRest compares the game's chunk thread resting and spinning on the same seed
(Session 111).  On installs Vintage Horizons' rest and leaves it on.  Off installs it and
switches it off with /dlchunkrest before generating, so the run is the game's own
behaviour with the rest still counting passes and allocation.  Absent pins
RestIdleChunkThread false, so the patch is not installed at all.  Default leaves the mod's
own default in force.
#>

[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Seed', 'Prepare', 'Run', 'Compare')]
    [string]$Action = 'Plan',
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$Profile = 'p0-worldgen-threads',
    [string]$SeedSave,
    [ValidateRange(1, 50)]
    [int]$Range = 30,
    [ValidateRange(10, 240)]
    [int]$TimeoutMinutes = 120,
    [ValidateRange(1024, 65535)]
    [int]$Port = 42431,
    [ValidateSet(1, 2, 3, 4, 5, 6)]
    [int[]]$ThreadCounts = @(1, 6),
    [string[]]$RequiredModIds = @('terraprety', 'watersheds'),
    [ValidateSet('Default', 'On', 'Off', 'Absent')]
    [string]$ChunkThreadRest = 'Default',
    [switch]$AllowPartlyGeneratedSeed,
    [string]$GamePath,
    [string]$ModSource,
    [string]$ModConfigSource
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$gateBase = Join-Path $repoRoot '.testdata\worldgen-thread-gate'
$profileRoot = Join-Path $gateBase $Profile
$manifestPath = Join-Path $profileRoot 'manifest.json'
$compareScript = Join-Path $PSScriptRoot 'worldgen-save-compare.py'
$GamePath = if ($GamePath) { [IO.Path]::GetFullPath($GamePath) } elseif ($env:VINTAGE_STORY) {
    [IO.Path]::GetFullPath($env:VINTAGE_STORY)
} else { Join-Path $env:APPDATA 'Vintagestory' }
$dataPath = Join-Path $env:APPDATA 'VintagestoryData'
$ModSource = if ($ModSource) { [IO.Path]::GetFullPath($ModSource) } else {
    Join-Path $dataPath 'Mods'
}
$ModConfigSource = if ($ModConfigSource) { [IO.Path]::GetFullPath($ModConfigSource) } else {
    Join-Path $dataPath 'ModConfig'
}
$python = Get-Command python3, python -ErrorAction SilentlyContinue | Select-Object -First 1
$effectiveRequiredModIds = @($RequiredModIds + 'dodenslandscapes' | Sort-Object -Unique)

function Assert-UnderGateBase {
    param([Parameter(Mandatory)][string]$Path)
    $root = [IO.Path]::GetFullPath($gateBase).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a worldgen-gate operation outside $root : $full"
    }
}

function Assert-Inputs {
    if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'VintagestoryServer.dll') -PathType Leaf)) {
        throw "Vintage Story server not found: $GamePath"
    }
    if (-not (Test-Path -LiteralPath $compareScript -PathType Leaf)) {
        throw "Comparator is missing: $compareScript"
    }
    if ($null -eq $python) {
        throw 'Python 3 is required for the SQLite/protobuf comparator.'
    }
}

function Get-VintageHorizonsZip {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'VintageHorizons\VintageHorizons.csproj')
    $version = ([string]($project.Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1)).Trim()
    if (-not $version) { throw 'Could not read the Vintage Horizons project version.' }
    $zip = Join-Path $repoRoot "dist\dodenslandscapes_$version.zip"
    if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) {
        throw "The exact current Vintage Horizons zip is missing: $zip"
    }
    return $zip
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
        throw "A Vintage Story process is already running ($list). The measurement requires an idle machine."
    }
}

function Assert-SettledSave {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Save does not exist: $Path" }
    $wal = Get-Item -LiteralPath "$Path-wal" -ErrorAction SilentlyContinue
    if ($null -ne $wal -and $wal.Length -gt 0) {
        throw "The save has a non-empty SQLite WAL; stop the game cleanly before copying it: $($wal.FullName)"
    }
}

function Save-Manifest {
    param([Parameter(Mandatory)][object]$Manifest)
    Assert-UnderGateBase $manifestPath
    $temporary = "$manifestPath.tmp"
    $Manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $manifestPath -Force
}

function Read-Manifest {
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "No prepared gate profile exists at $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.format -ne 1 -or $manifest.profile -ne $Profile) {
        throw "Unsupported or mismatched gate manifest: $manifestPath"
    }
    if ([int]$manifest.range -ne $Range) {
        throw "The profile was prepared for radius $($manifest.range), not radius $Range."
    }
    return $manifest
}

function Write-PinnedConfig {
    param([Parameter(Mandatory)][string]$TargetDataPath, [Parameter(Mandatory)][int]$Threads)
    Assert-UnderGateBase $TargetDataPath
    New-Item -ItemType Directory -Path (Join-Path $TargetDataPath 'ModConfig') -Force | Out-Null
    $pinned = @{
        EnableCapture = $true
        EnableServing = $false
        SweepSavegame = $false
        RefreshColumnsPerSecond = 0
        EnableGenerateCommand = $false
        EnableWorldGeneration = $false
        PregenRadiusChunks = 0
    }
    if ($ChunkThreadRest -ne 'Default') { $pinned.RestIdleChunkThread = ($ChunkThreadRest -ne 'Absent') }
    $pinned | ConvertTo-Json | Set-Content -LiteralPath (
        Join-Path $TargetDataPath 'ModConfig\dodenslandscapes-server.json') -Encoding utf8
    @{
        MaxWorldgenThreads = $Threads
        ServerMagicNumVersion = '1.3'
    } | ConvertTo-Json | Set-Content -LiteralPath (
        Join-Path $TargetDataPath 'servermagicnumbers.json') -Encoding utf8
}

function Copy-DirectoryContents {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Target)
    Assert-UnderGateBase $Target
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { return }
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $entry.FullName -Destination $Target -Recurse
    }
}

function Initialize-ModEnvironment {
    param([Parameter(Mandatory)][string]$TargetDataPath)
    Assert-UnderGateBase $TargetDataPath
    $modsTarget = Join-Path $TargetDataPath 'Mods'
    $configsTarget = Join-Path $TargetDataPath 'ModConfig'
    New-Item -ItemType Directory -Path $modsTarget, $configsTarget -Force | Out-Null

    $copied = @()
    foreach ($entry in Get-ChildItem -LiteralPath $ModSource -Force) {
        if ($entry.Name -match '^dodenslandscapes(?:_|\.|$)') { continue }
        Copy-Item -LiteralPath $entry.FullName -Destination $modsTarget -Recurse
        $copied += $entry.Name
    }
    Copy-DirectoryContents $ModConfigSource $configsTarget
    $vhZip = Get-VintageHorizonsZip
    Copy-Item -LiteralPath $vhZip -Destination $modsTarget
    $copied += [IO.Path]::GetFileName($vhZip)
    foreach ($requiredMod in $effectiveRequiredModIds) {
        $escaped = [regex]::Escape($requiredMod)
        if (-not ($copied | Where-Object { $_ -match "^$escaped(?:[_\.-]|$)" })) {
            throw "No $requiredMod mod was found in $ModSource or the current build."
        }
    }
    Write-PinnedConfig $TargetDataPath 1
    return @($copied | Sort-Object)
}

function Copy-PreparedEnvironment {
    param([Parameter(Mandatory)][string]$SourceDataPath, [Parameter(Mandatory)][string]$TargetDataPath)
    Copy-DirectoryContents (Join-Path $SourceDataPath 'Mods') (Join-Path $TargetDataPath 'Mods')
    Copy-DirectoryContents (Join-Path $SourceDataPath 'ModConfig') (Join-Path $TargetDataPath 'ModConfig')
}

function Get-SeedFromProfile {
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $null }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $candidate = [string]$manifest.seed.path
    if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    return $null
}

function Resolve-SeedSave {
    if ($SeedSave) { return [IO.Path]::GetFullPath($SeedSave) }
    $profileSeed = Get-SeedFromProfile
    if ($profileSeed) { return $profileSeed }
    throw 'No seed exists. Run -Action Seed first, or pass -SeedSave to Prepare.'
}

function Invoke-ComparatorInspect {
    param([Parameter(Mandatory)][string]$Save)
    & $python.Source $compareScript inspect $Save --range $Range
    if ($LASTEXITCODE -ne 0) { throw "Save inspection failed with exit code $LASTEXITCODE" }
}

function Get-SeedInspection {
    param([Parameter(Mandatory)][string]$Save)
    $json = & $python.Source $compareScript inspect $Save --range $Range --json
    if ($LASTEXITCODE -ne 0) { throw "Save inspection failed with exit code $LASTEXITCODE" }
    return $json | ConvertFrom-Json
}

function Add-NativeThreadReader {
    if ('VintageHorizons.WorldgenGate.NativeThreadName' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace VintageHorizons.WorldgenGate
{
    public static class NativeThreadName
    {
        const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

        [DllImport("kernel32.dll")]
        static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);

        public static string Get(uint threadId)
        {
            IntPtr handle = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, threadId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                IntPtr value;
                if (GetThreadDescription(handle, out value) < 0 || value == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(value); }
                finally { LocalFree(value); }
            }
            finally { CloseHandle(handle); }
        }
    }
}
'@
}

function Csv-Quote {
    param([AllowNull()][string]$Value)
    if ($null -eq $Value) { return '' }
    return '"' + $Value.Replace('"', '""') + '"'
}

function Add-MetricSample {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$MetricsPath,
        [Parameter(Mandatory)][Diagnostics.Stopwatch]$Clock,
        [Parameter(Mandatory)][string]$Phase,
        [AllowNull()][Nullable[int]]$QueueDepth,
        [Parameter(Mandatory)][hashtable]$PreviousThreadCpu,
        [Parameter(Mandatory)][ref]$PreviousProcessCpu,
        [Parameter(Mandatory)][ref]$PreviousSampleSeconds
    )
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $Process.Refresh()
    $elapsed = $Clock.Elapsed.TotalSeconds
    $wallDelta = $elapsed - [double]$PreviousSampleSeconds.Value
    if ($wallDelta -le 0) { return }
    $processCpu = $Process.TotalProcessorTime.TotalSeconds
    $processCores = [Math]::Max(0, ($processCpu - [double]$PreviousProcessCpu.Value) / $wallDelta)
    # PowerShell unboxes Nullable[int] parameters with a value to System.Int32, so
    # accessing .Value returns null.  Format the boxed integer directly.
    $queueText = if ($null -eq $QueueDepth) { '' } else { $QueueDepth.ToString($culture) }
    $common = @(
        [DateTime]::UtcNow.ToString('o'),
        $elapsed.ToString('0.000', $culture),
        $Phase,
        $queueText,
        ($Process.WorkingSet64 / 1MB).ToString('0.0', $culture),
        ($Process.PrivateMemorySize64 / 1MB).ToString('0.0', $culture)
    )
    $line = (($common | ForEach-Object { Csv-Quote $_ }) -join ',') + ",process,,,$($processCores.ToString('0.0000', $culture))"
    Add-Content -LiteralPath $MetricsPath -Value $line -Encoding utf8

    $current = @{}
    foreach ($thread in $Process.Threads) {
        try {
            $id = [int]$thread.Id
            $cpu = $thread.TotalProcessorTime.TotalSeconds
            $current[$id] = $cpu
            $name = [VintageHorizons.WorldgenGate.NativeThreadName]::Get([uint32]$id)
            if ([string]::IsNullOrWhiteSpace($name) -or -not $PreviousThreadCpu.ContainsKey($id)) { continue }
            $cores = [Math]::Max(0, ($cpu - [double]$PreviousThreadCpu[$id]) / $wallDelta)
            $prefix = ($common | ForEach-Object { Csv-Quote $_ }) -join ','
            $threadLine = "$prefix,thread,$id,$(Csv-Quote $name),$($cores.ToString('0.0000', $culture))"
            Add-Content -LiteralPath $MetricsPath -Value $threadLine -Encoding utf8
        } catch {
            # A thread can exit between Process.Threads and TotalProcessorTime.  Its last
            # partial interval is intentionally omitted rather than failing the run.
        }
    }
    $PreviousThreadCpu.Clear()
    foreach ($entry in $current.GetEnumerator()) { $PreviousThreadCpu[$entry.Key] = $entry.Value }
    $PreviousProcessCpu.Value = $processCpu
    $PreviousSampleSeconds.Value = $elapsed
}

function Summarize-Metrics {
    param([Parameter(Mandatory)][string]$MetricsPath)
    $rows = @(Import-Csv -LiteralPath $MetricsPath | Where-Object { $_.phase -eq 'generation' })
    $processRows = @($rows | Where-Object { $_.scope -eq 'process' })
    $threadRows = @($rows | Where-Object { $_.scope -eq 'thread' })
    $threads = [ordered]@{}
    foreach ($group in $threadRows | Group-Object thread_name) {
        $values = @($group.Group | ForEach-Object { [double]$_.cpu_cores })
        $threads[$group.Name] = [ordered]@{
            samples = $values.Count
            averageCores = if ($values.Count) { ($values | Measure-Object -Average).Average } else { 0 }
            peakCores = if ($values.Count) { ($values | Measure-Object -Maximum).Maximum } else { 0 }
        }
    }
    $queue = @($processRows | Where-Object { $_.queue_depth -ne '' } | ForEach-Object { [int]$_.queue_depth })
    [ordered]@{
        samples = $processRows.Count
        averageProcessCores = if ($processRows.Count) {
            (@($processRows | ForEach-Object { [double]$_.cpu_cores }) | Measure-Object -Average).Average
        } else { 0 }
        peakWorkingSetMb = if ($processRows.Count) {
            (@($processRows | ForEach-Object { [double]$_.working_set_mb }) | Measure-Object -Maximum).Maximum
        } else { 0 }
        peakPrivateMb = if ($processRows.Count) {
            (@($processRows | ForEach-Object { [double]$_.private_mb }) | Measure-Object -Maximum).Maximum
        } else { 0 }
        peakQueueDepth = if ($queue.Count) { ($queue | Measure-Object -Maximum).Maximum } else { 0 }
        queueThresholdsObserved = @(
            foreach ($threshold in @(100, 400, 1200)) {
                if ($queue.Count -and ($queue | Measure-Object -Maximum).Maximum -ge $threshold) { $threshold }
            })
        namedThreads = $threads
    }
}

function Invoke-IsolatedServer {
    param(
        [Parameter(Mandatory)][string]$TargetDataPath,
        [Parameter(Mandatory)][int]$Threads,
        [Parameter(Mandatory)][int]$RunPort,
        [switch]$Generate
    )
    Assert-UnderGateBase $TargetDataPath
    Add-NativeThreadReader
    Write-PinnedConfig $TargetDataPath $Threads
    New-Item -ItemType Directory -Path (Join-Path $TargetDataPath 'tmp') -Force | Out-Null
    $consolePath = Join-Path $TargetDataPath 'console.log'
    $errorPath = Join-Path $TargetDataPath 'console.err.log'
    $metricsPath = Join-Path $TargetDataPath 'worldgen-thread-metrics.csv'
    $resultPath = Join-Path $TargetDataPath 'worldgen-thread-result.json'
    $pidPath = Join-Path $TargetDataPath 'server.pid'
    foreach ($path in @($consolePath, $errorPath, $metricsPath, $resultPath, $pidPath)) {
        if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite an existing run artifact: $path" }
    }
    'utc,elapsed_s,phase,queue_depth,working_set_mb,private_mb,scope,thread_id,thread_name,cpu_cores' |
        Set-Content -LiteralPath $metricsPath -Encoding utf8

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
    $psi.ArgumentList.Add($TargetDataPath)
    $psi.ArgumentList.Add("--withconfig={ Port: $RunPort, VerifyPlayerAuth: false, WhitelistMode: 'off', AdvertiseServer: false, DefaultRoleCode: 'admin' }")
    $psi.Environment['TEMP'] = Join-Path $TargetDataPath 'tmp'
    $psi.Environment['TMP'] = Join-Path $TargetDataPath 'tmp'
    $psi.Environment['VINTAGEHORIZONS_STATS'] = '1'

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    $commandSent = $false
    $submitted = $false
    $stopSent = $false
    $latestQueue = $null
    $peakQueue = 0
    $zeroSamples = 0
    $generationStartSeconds = $null
    $firstZeroSeconds = $null
    $stopSentSeconds = $null
    $nextQueuePoll = 0.0
    $nextMetric = 0.0
    $previousThreadCpu = @{}
    $previousProcessCpu = 0.0
    $previousSampleSeconds = 0.0
    $phase = 'startup'
    $timedOut = $false
    $started = $false

    try {
        if (-not $process.Start()) { throw 'Vintage Story server process did not start.' }
        $started = $true
        $process.StandardInput.AutoFlush = $true
        Set-Content -LiteralPath $pidPath -Value $process.Id
        Write-Host "  server pid $($process.Id), threads $Threads, dataPath $TargetDataPath"
        $stdoutTask = $process.StandardOutput.ReadLineAsync()
        $stderrTask = $process.StandardError.ReadLineAsync()

        while (-not $process.HasExited) {
            while ($null -ne $stdoutTask -and $stdoutTask.IsCompleted) {
                $line = $stdoutTask.GetAwaiter().GetResult()
                if ($null -eq $line) { $stdoutTask = $null; break }
                Add-Content -LiteralPath $consolePath -Value $line -Encoding utf8
                if ($line.Contains('Dedicated Server now running', [StringComparison]::Ordinal)) {
                    $ready = $true
                    Write-Host '  server ready'
                }
                if ($line -match 'Currently (?<queue>[0-9]+) chunks in generation queue') {
                    $latestQueue = [int]$Matches.queue
                    $peakQueue = [Math]::Max($peakQueue, $latestQueue)
                    if ($submitted -and $latestQueue -eq 0) {
                        if ($null -eq $firstZeroSeconds) { $firstZeroSeconds = $clock.Elapsed.TotalSeconds }
                        $zeroSamples++
                    } elseif ($submitted) { $zeroSamples = 0 }
                }
                if ($Generate -and $line.Contains("Ok, $((2 * $Range + 1) * (2 * $Range + 1)) columns, generated!", [StringComparison]::Ordinal)) {
                    $submitted = $true
                    $latestQueue = $null
                    $zeroSamples = 0
                    Write-Host '  vanilla command submitted its final batch; waiting for the real queue to drain'
                }
                if ($line.Contains('Exception', [StringComparison]::OrdinalIgnoreCase) -or
                    $line.Contains('error', [StringComparison]::OrdinalIgnoreCase)) {
                    Write-Host "  log: $line"
                }
                $stdoutTask = $process.StandardOutput.ReadLineAsync()
            }
            while ($null -ne $stderrTask -and $stderrTask.IsCompleted) {
                $line = $stderrTask.GetAwaiter().GetResult()
                if ($null -eq $line) { $stderrTask = $null; break }
                Add-Content -LiteralPath $errorPath -Value $line -Encoding utf8
                $stderrTask = $process.StandardError.ReadLineAsync()
            }

            if ($ready -and -not $commandSent) {
                if ($Generate -and $ChunkThreadRest -eq 'Off') {
                    $process.StandardInput.WriteLine('/dlchunkrest off')
                    Write-Host '  sent /dlchunkrest off'
                }
                if ($Generate) {
                    $phase = 'generation'
                    $generationStartSeconds = $clock.Elapsed.TotalSeconds
                    $process.StandardInput.WriteLine("/wgen pregen $Range")
                    Write-Host "  sent /wgen pregen $Range"
                } else {
                    # GameReady has finished creating and saving the initial spawn.  A few
                    # quiet seconds let the first autosave transaction settle before /stop.
                    $nextQueuePoll = $clock.Elapsed.TotalSeconds + 5
                }
                $commandSent = $true
            }

            if ($commandSent -and $Generate -and $clock.Elapsed.TotalSeconds -ge $nextQueuePoll -and -not $stopSent) {
                $process.StandardInput.WriteLine('/debug chunk queue')
                $nextQueuePoll = $clock.Elapsed.TotalSeconds + 1
            }
            if ($commandSent -and -not $Generate -and $clock.Elapsed.TotalSeconds -ge $nextQueuePoll -and -not $stopSent) {
                $phase = 'shutdown'
                $process.StandardInput.WriteLine('/stop')
                $stopSent = $true
                $stopSentSeconds = $clock.Elapsed.TotalSeconds
                Write-Host '  stopping fresh seed cleanly'
            }
            if ($submitted -and $zeroSamples -ge 3 -and -not $stopSent) {
                $phase = 'shutdown'
                $process.StandardInput.WriteLine('/stop')
                $stopSent = $true
                $stopSentSeconds = $clock.Elapsed.TotalSeconds
                Write-Host '  generation queue stayed at zero; stopping cleanly to flush the save'
            }

            if ($clock.Elapsed.TotalSeconds -ge $nextMetric) {
                Add-MetricSample $process $metricsPath $clock $phase $latestQueue `
                    $previousThreadCpu ([ref]$previousProcessCpu) ([ref]$previousSampleSeconds)
                $nextMetric = $clock.Elapsed.TotalSeconds + 1
            }
            if ($clock.Elapsed.TotalMinutes -gt $TimeoutMinutes -and -not $stopSent) {
                $timedOut = $true
                $phase = 'shutdown'
                $process.StandardInput.WriteLine('/stop')
                $stopSent = $true
                $stopSentSeconds = $clock.Elapsed.TotalSeconds
                Write-Warning "Run exceeded $TimeoutMinutes minutes; sent /stop."
            }
            if ($stopSent -and $null -ne $stopSentSeconds -and
                $clock.Elapsed.TotalSeconds - $stopSentSeconds -gt 120) {
                Write-Warning "Exact sandbox process $($process.Id) did not stop after 120s; force-stopping the disposable copy."
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
            }
            Start-Sleep -Milliseconds 50
        }
        $process.WaitForExit()
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        if ($process.ExitCode -ne 0) { throw "Server exited with code $($process.ExitCode)." }
        if (-not $ready) { throw 'Server exited before GameReady.' }
        $serverText = Get-Content -LiteralPath $consolePath -Raw
        $modLine = @(Get-Content -LiteralPath $consolePath |
            Where-Object { $_.Contains('Mods, sorted by dependency:', [StringComparison]::Ordinal) } |
            Select-Object -First 1)[0]
        foreach ($requiredMod in $effectiveRequiredModIds) {
            if (-not $modLine -or $modLine.IndexOf(
                    $requiredMod, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "The server log does not prove that required mod '$requiredMod' loaded."
            }
        }
        if ($serverText.IndexOf('Server LOD capture active', [StringComparison]::Ordinal) -lt 0 -or
            $serverText.IndexOf('serving off', [StringComparison]::Ordinal) -lt 0 -or
            $serverText.IndexOf('sweep off', [StringComparison]::Ordinal) -lt 0) {
            throw 'The server log does not prove that the pinned no-background-generation Vintage Horizons config loaded.'
        }
        $restProof = switch ($ChunkThreadRest) {
            'On' { 'Chunk thread rest installed and on' }
            'Off' { 'Chunk thread rest switched off by the console' }
            'Absent' { 'chunk thread rest off' }
            default { $null }
        }
        if ($null -ne $restProof -and $serverText.IndexOf($restProof, [StringComparison]::Ordinal) -lt 0) {
            throw "The server log does not prove the chunk thread rest state ($ChunkThreadRest): no '$restProof'."
        }
        $chunkThreadLine = @(Get-Content -LiteralPath $consolePath |
            Where-Object { $_.Contains('Chunk thread over this session', [StringComparison]::Ordinal) } |
            Select-Object -Last 1)[0]
        if ($Generate -and (-not $submitted -or $null -eq $firstZeroSeconds)) {
            throw 'Generation did not reach a confirmed empty queue.'
        }
        if ($timedOut) { throw "Generation exceeded $TimeoutMinutes minutes." }

        $requested = (2 * $Range + 1) * (2 * $Range + 1)
        $generationSeconds = if ($Generate) {
            [double]$firstZeroSeconds - [double]$generationStartSeconds
        } else { 0.0 }
        $metrics = Summarize-Metrics $metricsPath
        $result = [ordered]@{
            format = 1
            threads = $Threads
            requestedColumns = if ($Generate) { $requested } else { 0 }
            generationSeconds = $generationSeconds
            requestedColumnsPerSecond = if ($Generate -and $generationSeconds -gt 0) {
                $requested / $generationSeconds
            } else { 0 }
            peakQueueDepth = $peakQueue
            chunkThreadRest = $ChunkThreadRest
            chunkThreadSession = $chunkThreadLine
            metrics = $metrics
            completedUtc = [DateTime]::UtcNow.ToString('o')
        }
        $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resultPath -Encoding utf8
        if ($Generate) {
            Write-Host ("  complete: {0} columns in {1:0.0}s ({2:0.00}/s), peak queue {3}" -f `
                $requested, $generationSeconds, ($requested / $generationSeconds), $peakQueue)
        }
        return $result
    } catch {
        if ($started -and -not $process.HasExited) {
            try { $process.StandardInput.WriteLine('/stop') } catch { }
            if (-not $process.WaitForExit(120000)) {
                Write-Warning "Exact sandbox process $($process.Id) did not stop after 120s; force-stopping the disposable copy."
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        throw
    } finally {
        $process.Dispose()
    }
}

function Invoke-Comparison {
    param([Parameter(Mandatory)][object]$Manifest)
    $one = [string]($Manifest.runs | Where-Object { $_.threads -eq 1 }).save
    $six = [string]($Manifest.runs | Where-Object { $_.threads -eq 6 }).save
    Assert-SettledSave $one
    Assert-SettledSave $six
    $report = Join-Path $profileRoot 'terrain-comparison.json'
    # Capture the comparator's success-stream text so the function returns only its
    # integer exit code.  Letting the text escape here turns the caller's $exit into
    # an object array and can mask a failed terrain gate.
    $comparisonOutput = @(& $python.Source $compareScript compare $one $six --range $Range --json $report)
    $exit = [int]$LASTEXITCODE
    foreach ($line in $comparisonOutput) { Write-Host $line }

    $oneResultPath = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($one))) 'worldgen-thread-result.json'
    $sixResultPath = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($six))) 'worldgen-thread-result.json'
    if ((Test-Path -LiteralPath $oneResultPath) -and (Test-Path -LiteralPath $sixResultPath)) {
        $oneResult = Get-Content -LiteralPath $oneResultPath -Raw | ConvertFrom-Json
        $sixResult = Get-Content -LiteralPath $sixResultPath -Raw | ConvertFrom-Json
        $speedup = if ($oneResult.requestedColumnsPerSecond -gt 0) {
            $sixResult.requestedColumnsPerSecond / $oneResult.requestedColumnsPerSecond
        } else { 0 }
        Write-Host ("  throughput: 1 thread {0:0.00}/s; 6 threads {1:0.00}/s; speedup {2:0.00}x" -f `
            $oneResult.requestedColumnsPerSecond, $sixResult.requestedColumnsPerSecond, $speedup)
        Write-Host "  named-thread samples: $oneResultPath and $sixResultPath"
    }
    Write-Host "  terrain report: $report"
    return [int]$exit
}

Assert-Inputs
Assert-UnderGateBase $profileRoot

switch ($Action) {
    'Plan' {
        Write-Host
        Write-Host "  profile: $Profile"
        Write-Host "  sandbox: $profileRoot"
        Write-Host "  game: $GamePath"
        Write-Host "  active mods: $ModSource"
        Write-Host "  current mod: $(Get-VintageHorizonsZip)"
        Write-Host "  command per copy: /wgen pregen $Range ($((2 * $Range + 1) * (2 * $Range + 1)) columns)"
        if ($SeedSave) {
            $resolved = [IO.Path]::GetFullPath($SeedSave)
            Assert-SettledSave $resolved
            Invoke-ComparatorInspect $resolved
            $bytes = (Get-Item -LiteralPath $resolved).Length
            $reserve = 1GB + 2L * ((2 * $Range + 1) * (2 * $Range + 1)) * 512KB
            Write-Host ("  estimated free space needed: {0:0.0} GiB for two copies plus generated terrain" -f `
                ((2L * $bytes + $reserve) / 1GB))
        } else {
            Write-Host '  seed: a fresh disposable world will be created by -Action Seed'
            Write-Host '  disk: the fresh seed keeps the two copies small while still generating nearly all 3,721 columns'
        }
        Write-Host
        break
    }
    'Seed' {
        if ($SeedSave) { throw '-SeedSave is for Prepare; Seed creates a new isolated world.' }
        Assert-NoGameProcess
        if (Test-Path -LiteralPath $profileRoot) {
            throw "Profile already exists; choose another name rather than overwriting it: $profileRoot"
        }
        $seedData = Join-Path $profileRoot 'seed'
        New-Item -ItemType Directory -Path $seedData -Force | Out-Null
        $mods = Initialize-ModEnvironment $seedData
        $seedResult = Invoke-IsolatedServer $seedData 1 $Port
        $seedPath = Join-Path $seedData 'Saves\default.vcdbs'
        Assert-SettledSave $seedPath
        $seedFile = Get-Item -LiteralPath $seedPath
        $manifest = [ordered]@{
            format = 1
            profile = $Profile
            range = $Range
            createdUtc = [DateTime]::UtcNow.ToString('o')
            seed = [ordered]@{
                path = $seedFile.FullName
                bytes = $seedFile.Length
                sha256 = (Get-FileHash -LiteralPath $seedFile.FullName -Algorithm SHA256).Hash
                source = 'fresh-isolated-world'
            }
            mods = $mods
            requiredMods = $effectiveRequiredModIds
            runs = @()
        }
        Save-Manifest $manifest
        Write-Host "  fresh seed ready: $seedPath"
        Invoke-ComparatorInspect $seedPath
        break
    }
    'Prepare' {
        Assert-NoGameProcess
        $seedPath = Resolve-SeedSave
        Assert-SettledSave $seedPath
        $inspection = Get-SeedInspection $seedPath
        $existingColumns = [int]$inspection.target_rows.mapchunk
        $expectedColumns = [int]$inspection.expected_mapchunks
        $maximumExisting = [Math]::Max(100, [Math]::Floor($expectedColumns / 10))
        if ($existingColumns -gt $maximumExisting -and -not $AllowPartlyGeneratedSeed) {
            throw ("The target square already contains {0}/{1} map columns. P0 requires mostly " +
                "ungenerated terrain; create a fresh -Action Seed profile, or pass " +
                "-AllowPartlyGeneratedSeed only for a knowingly partial diagnostic.") -f `
                $existingColumns, $expectedColumns
        }
        Write-Host "  seed target currently contains $existingColumns/$expectedColumns map columns"
        $existingManifest = if (Test-Path -LiteralPath $manifestPath) { Read-Manifest } else { $null }
        if ($null -ne $existingManifest -and @($existingManifest.runs).Count -gt 0) {
            throw "Profile already has prepared runs; choose another profile rather than overwriting them: $profileRoot"
        }
        if ($null -eq $existingManifest) {
            New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
        }
        $seedFile = Get-Item -LiteralPath $seedPath
        $reserve = 1GB + 2L * ((2 * $Range + 1) * (2 * $Range + 1)) * 512KB
        $required = 2L * $seedFile.Length + $reserve
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($profileRoot))
        if ($drive.AvailableFreeSpace -lt $required) {
            throw ("Need about {0:0.0} GiB free but only {1:0.0} GiB is available on {2}" -f `
                ($required / 1GB), ($drive.AvailableFreeSpace / 1GB), $drive.Name)
        }
        Write-Host "  hashing frozen seed"
        $seedHash = (Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash
        $runs = @()
        $preparedMods = @()
        foreach ($threads in @($ThreadCounts | Sort-Object -Unique)) {
            $runData = Join-Path $profileRoot "threads-$threads"
            if (Test-Path -LiteralPath $runData) {
                throw "Prepared target already exists; refusing to overwrite it: $runData"
            }
            $saveDir = Join-Path $runData 'Saves'
            New-Item -ItemType Directory -Path $saveDir -Force | Out-Null
            $targetSave = Join-Path $saveDir 'default.vcdbs'
            Write-Host "  copying seed for $threads thread(s)"
            Copy-Item -LiteralPath $seedPath -Destination $targetSave
            if ((Get-FileHash -LiteralPath $targetSave -Algorithm SHA256).Hash -ne $seedHash) {
                throw "Seed hash mismatch after copying: $targetSave"
            }
            $profileSeedData = Join-Path $profileRoot 'seed'
            if ((Get-SeedFromProfile) -and (Test-Path -LiteralPath $profileSeedData -PathType Container)) {
                Copy-PreparedEnvironment $profileSeedData $runData
            } else {
                $modsForRun = @(Initialize-ModEnvironment $runData)
                if ($preparedMods.Count -eq 0) { $preparedMods = $modsForRun }
            }
            Write-PinnedConfig $runData $threads
            $runs += [ordered]@{
                threads = $threads
                dataPath = $runData
                save = $targetSave
                port = $Port + ($threads - 1)
                status = 'prepared'
            }
        }
        $manifest = if ($null -ne $existingManifest) { $existingManifest } else {
            [ordered]@{
                format = 1
                profile = $Profile
                range = $Range
                createdUtc = [DateTime]::UtcNow.ToString('o')
                seed = [ordered]@{}
                mods = @()
                requiredMods = $effectiveRequiredModIds
                runs = @()
            }
        }
        $manifest.seed = [ordered]@{
            path = $seedFile.FullName
            bytes = $seedFile.Length
            sha256 = $seedHash
            source = if ($SeedSave) { 'owner-save' } else { 'fresh-isolated-world' }
        }
        if (@($manifest.mods).Count -eq 0) { $manifest.mods = $preparedMods }
        if ($null -eq $manifest.PSObject.Properties['requiredMods']) {
            $manifest | Add-Member -NotePropertyName requiredMods -NotePropertyValue $effectiveRequiredModIds
        }
        $manifest.runs = $runs
        Save-Manifest $manifest
        $copyLabel = if ($runs.Count -eq 1) { 'copy' } else { 'copies' }
        Write-Host "  prepared $($runs.Count) verified $copyLabel under $profileRoot"
        break
    }
    'Run' {
        Assert-NoGameProcess
        $manifest = Read-Manifest
        if ($null -ne $manifest.requiredMods) {
            $effectiveRequiredModIds = @($manifest.requiredMods)
        }
        foreach ($run in $manifest.runs | Sort-Object threads) {
            if ($run.status -ne 'prepared') {
                throw "Run for $($run.threads) thread(s) is '$($run.status)', not prepared. Use a new profile to repeat it."
            }
            Assert-SettledSave ([string]$run.save)
            $hash = (Get-FileHash -LiteralPath ([string]$run.save) -Algorithm SHA256).Hash
            if ($hash -ne [string]$manifest.seed.sha256) {
                throw "Prepared copy changed before launch: $($run.save)"
            }
        }
        foreach ($run in $manifest.runs | Sort-Object threads) {
            $run.status = 'running'
            Save-Manifest $manifest
            Invoke-IsolatedServer ([string]$run.dataPath) ([int]$run.threads) ([int]$run.port) -Generate | Out-Null
            $run.status = 'complete'
            Save-Manifest $manifest
        }
        $counts = @($manifest.runs | ForEach-Object { [int]$_.threads })
        if ($counts -contains 1 -and $counts -contains 6) {
            $exit = Invoke-Comparison $manifest
            if ($exit -ne 0) { exit $exit }
        } else {
            Write-Host "  measured requested thread count(s): $($counts -join ', '); no 1-vs-6 comparison requested"
        }
        break
    }
    'Compare' {
        $manifest = Read-Manifest
        $exit = Invoke-Comparison $manifest
        if ($exit -ne 0) { exit $exit }
        break
    }
}
