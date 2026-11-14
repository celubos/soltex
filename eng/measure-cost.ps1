[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [ValidateRange(5, 3600)][int]$Seconds = 60,
    [ValidateRange(200, 10000)][int]$SampleMilliseconds = 1000,
    [string]$Label = 'scenario',
    [string]$OutFile
)

# Measures the CPU time, memory, threads and handles of a process and every descendant over a
# fixed window, and records every descendant that appeared or exited during it (for example a
# PowerShell probe launched by a timer). Read-only: it never starts, stops or signals a process.

$ErrorActionPreference = 'Stop'
$logical = [Environment]::ProcessorCount

function Get-Tree([int]$rootId) {
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, CreationDate
    $byParent = @{}
    foreach ($p in $all) {
        $key = [int]$p.ParentProcessId
        if (-not $byParent.ContainsKey($key)) { $byParent[$key] = [System.Collections.Generic.List[object]]::new() }
        $byParent[$key].Add($p)
    }
    $root = $all | Where-Object ProcessId -EQ $rootId
    if (-not $root) { return @() }
    $result = [System.Collections.Generic.List[object]]::new(); $result.Add($root)
    $queue = [System.Collections.Generic.Queue[int]]::new(); $queue.Enqueue($rootId)
    while ($queue.Count) {
        $id = $queue.Dequeue()
        if ($byParent.ContainsKey($id)) {
            foreach ($child in $byParent[$id]) {
                # Guard against PID reuse: a child must be created after its parent.
                $parent = $result | Where-Object ProcessId -EQ $id | Select-Object -First 1
                if ($child.CreationDate -ge $parent.CreationDate) { $result.Add($child); $queue.Enqueue([int]$child.ProcessId) }
            }
        }
    }
    return $result
}

function Read-Counters([int]$id) {
    try {
        $p = [Diagnostics.Process]::GetProcessById($id)
        $p.Refresh()
        return [pscustomobject]@{
            Pid = $id; Name = $p.ProcessName; CpuMs = $p.TotalProcessorTime.TotalMilliseconds
            PrivateBytes = $p.PrivateMemorySize64; WorkingSet = $p.WorkingSet64
            Threads = $p.Threads.Count; Handles = $p.HandleCount
        }
    } catch { return $null }
}

if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { throw "Process $ProcessId is not running." }

$seen = @{}            # pid -> first/last counters and lifetime notes
$timer = [Diagnostics.Stopwatch]::StartNew()
$samples = 0
$peakPrivate = 0L
while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
    $tree = Get-Tree $ProcessId
    if (-not $tree) { break }
    $privateNow = 0L
    foreach ($node in $tree) {
        $c = Read-Counters ([int]$node.ProcessId)
        if (-not $c) { continue }
        $privateNow += $c.PrivateBytes
        if (-not $seen.ContainsKey($c.Pid)) {
            $seen[$c.Pid] = [pscustomobject]@{ Pid = $c.Pid; Name = $c.Name; First = $c; Last = $c; AppearedDuringWindow = ($samples -gt 0) }
        } else { $seen[$c.Pid].Last = $c }
    }
    if ($privateNow -gt $peakPrivate) { $peakPrivate = $privateNow }
    $samples++
    Start-Sleep -Milliseconds $SampleMilliseconds
}
$elapsedSeconds = $timer.Elapsed.TotalSeconds
$alive = @((Get-Tree $ProcessId) | ForEach-Object { [int]$_.ProcessId })

$rows = foreach ($entry in $seen.Values) {
    # Processes that appeared during the window are charged their whole observed CPU time.
    $cpu = if ($entry.AppearedDuringWindow) { $entry.Last.CpuMs } else { $entry.Last.CpuMs - $entry.First.CpuMs }
    [pscustomobject]@{
        Pid = $entry.Pid; Name = $entry.Name; AppearedDuringWindow = $entry.AppearedDuringWindow
        ExitedDuringWindow = ($alive -notcontains $entry.Pid)
        CpuMsPerSecond = [math]::Round($cpu / $elapsedSeconds, 2)
        PrivateMB = [math]::Round($entry.Last.PrivateBytes / 1MB, 1); WorkingSetMB = [math]::Round($entry.Last.WorkingSet / 1MB, 1)
        Threads = $entry.Last.Threads; Handles = $entry.Last.Handles
    }
}
$totalCpu = ($rows | Measure-Object CpuMsPerSecond -Sum).Sum
$result = [ordered]@{
    schemaVersion = 1; label = $Label; recordedAtUtc = [DateTime]::UtcNow.ToString('o')
    rootProcessId = $ProcessId; seconds = [math]::Round($elapsedSeconds, 1); samples = $samples; logicalProcessors = $logical
    total = [ordered]@{
        cpuMsPerSecond = [math]::Round($totalCpu, 2)
        percentOfMachine = [math]::Round($totalCpu / 1000 / $logical * 100, 3)
        privateMB = [math]::Round((($rows | Where-Object { -not $_.ExitedDuringWindow }) | Measure-Object PrivateMB -Sum).Sum, 1)
        peakPrivateMB = [math]::Round($peakPrivate / 1MB, 1)
        processes = @($rows | Where-Object { -not $_.ExitedDuringWindow }).Count
        transientProcesses = @($rows | Where-Object { $_.AppearedDuringWindow -or $_.ExitedDuringWindow }).Count
    }
    processes = @($rows | Sort-Object CpuMsPerSecond -Descending)
    note = 'Short-lived children that start and exit between samples can be missed; use a shorter -SampleMilliseconds or WPR for proof of absence.'
}
$json = $result | ConvertTo-Json -Depth 5
if ($OutFile) { $json | Set-Content -LiteralPath $OutFile -Encoding utf8 }
$json
