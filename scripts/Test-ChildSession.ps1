#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$Cli = (Join-Path $env:LOCALAPPDATA 'WindowsChildSession\bin\WindowsChildSession.exe')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$testRoot = Join-Path $env:TEMP "子会话 验收 $([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $testRoot
$powerShell = Join-Path $PSHOME 'pwsh.exe'
$fixture = Join-Path $testRoot '打印 参数.ps1'
@'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
switch ($args[0]) {
    'output' {
        [Console]::WriteLine(($args[1..($args.Count - 1)] | ConvertTo-Json -Compress))
        [Console]::Error.WriteLine('标准错误')
        exit 7
    }
    'tree' {
        $child = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList '-NoProfile','-Command','"Start-Sleep -Seconds 120"' -PassThru
        [Console]::WriteLine($child.Id)
        Start-Sleep -Seconds 120
    }
}
'@ | Set-Content -LiteralPath $fixture -Encoding utf8

function Assert-Value($Actual, $Expected, [string]$Label) {
    if ($Actual -cne $Expected) {
        throw "$Label expected '$Expected', received '$Actual'. Fixture directory is $testRoot"
    }
}

function New-Request([string]$Program, [string[]]$Arguments, [int]$TimeoutSeconds = 25, [string]$Cwd = $testRoot) {
    $taskId = [guid]::NewGuid().ToString('D')
    $path = Join-Path $testRoot "$taskId.json"
    @{
        taskId = $taskId
        cwd = $Cwd
        program = $Program
        arguments = $Arguments
        timeoutSeconds = $TimeoutSeconds
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path -Encoding utf8
    return @{ Path = $path; Id = $taskId }
}

function Invoke-Run($Request, [switch]$Detach) {
    if ($Detach) { $json = & $Cli run --request $Request.Path --detach }
    else { $json = & $Cli run --request $Request.Path }
    return $json | ConvertFrom-Json
}

function Wait-Task($Request, [string]$Expected) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $result = & $Cli status $Request.Id | ConvertFrom-Json
        if ($result.status -eq $Expected) { return $result }
        if ($result.status -notin 'preparing','running','unknown') {
            throw "Task $($Request.Id) reached $($result.status): $($result.error)"
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Task $($Request.Id) did not reach $Expected."
}

function Read-Descendant($Running) {
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $line = Get-Content -LiteralPath $Running.stdoutPath -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($line -match '^\d+$') {
            $descendant = Get-Process -Id ([int]$line)
            $null = $descendant.Handle
            Assert-Value $descendant.SessionId $Running.childSessionId 'descendant session'
            return $descendant
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "No descendant PID from task $($Running.taskId)."
}

$parent = & $Cli probe | ConvertFrom-Json
$probeRequest = New-Request $Cli @('probe')
$probe = Invoke-Run $probeRequest
Assert-Value $probe.status 'completed' "probe status $($probe.error)"
Assert-Value $probe.exitCode 0 'probe exit code'
Assert-Value $probe.parentSessionId $parent.processSessionId 'parent session'
Assert-Value $probe.processSessionId $probe.childSessionId 'process session'
if ($probe.parentSessionId -eq $probe.childSessionId) { throw 'Probe ran in the parent session.' }
$probeOutput = Get-Content -LiteralPath $probe.stdoutPath -Raw | ConvertFrom-Json
Assert-Value $probeOutput.processSessionId $probe.childSessionId 'independent probe session'
$repeat = Invoke-Run $probeRequest
Assert-Value $repeat.updatedAt $probe.updatedAt 'duplicate request timestamp'

$wrongSession = & $Cli executor $probe.parentSessionId $probe.childSessionId | ConvertFrom-Json
if ($wrongSession.error -notmatch 'Executor session mismatch') { throw 'Parent session executor was not rejected.' }

$argumentsToCheck = @('带 空格', 'quote " inside', '尾部\')
$failedRequest = New-Request $powerShell (@('-NoProfile','-File',$fixture,'output') + $argumentsToCheck)
$failed = Invoke-Run $failedRequest
Assert-Value $failed.status 'process_failed' "failure status $($failed.error)"
Assert-Value $failed.exitCode 7 'process exit code'
Assert-Value $failed.childSessionId $probe.childSessionId 'reused child session'
$parsed = Get-Content -LiteralPath $failed.stdoutPath -Raw | ConvertFrom-Json
for ($index = 0; $index -lt $argumentsToCheck.Count; $index++) {
    Assert-Value $parsed[$index] $argumentsToCheck[$index] "process argument $index"
}
Assert-Value ((Get-Content -LiteralPath $failed.stderrPath -Raw).Trim()) '标准错误' 'stderr'

$invalidProgram = Invoke-Run (New-Request (Join-Path $testRoot '不存在.exe') @())
Assert-Value $invalidProgram.status 'start_failed' 'missing program'
if ($invalidProgram.error -notmatch 'Executable does not exist') { throw $invalidProgram.error }
$invalidDirectory = Invoke-Run (New-Request $Cli @('probe') -Cwd (Join-Path $testRoot '不存在'))
Assert-Value $invalidDirectory.status 'start_failed' 'invalid working directory'
if ($invalidDirectory.error -notmatch 'Working directory does not exist') { throw $invalidDirectory.error }

$timedRequest = New-Request $powerShell @('-NoProfile','-File',$fixture,'tree') 9
$null = Invoke-Run $timedRequest -Detach
$descendant = Read-Descendant (Wait-Task $timedRequest 'running')
$timed = Wait-Task $timedRequest 'timed_out'
if (!$descendant.WaitForExit(3000)) { throw 'Descendant survived timeout.' }
$descendant.Dispose()

$cancelRequest = New-Request $powerShell @('-NoProfile','-File',$fixture,'tree') 45
$null = Invoke-Run $cancelRequest -Detach
$descendant = Read-Descendant (Wait-Task $cancelRequest 'running')
$active = & $Cli status | ConvertFrom-Json
Assert-Value $active.activeTaskId $cancelRequest.Id 'active task'
try {
    $disconnect = & $Cli disconnect | ConvertFrom-Json
    Assert-Value $disconnect.status 'disconnected' 'disconnect during task'
    $cancelling = & $Cli cancel $cancelRequest.Id | ConvertFrom-Json
    Assert-Value $cancelling.status 'cancelling' "cancel acknowledgement $($cancelling.error)"
    $cancelled = Wait-Task $cancelRequest 'cancelled'
    if (!$descendant.WaitForExit(3000)) { throw 'Descendant survived cancellation.' }
}
finally {
    $descendant.Dispose()
}

$reconnect = Invoke-Run (New-Request $Cli @('probe'))
Assert-Value $reconnect.status 'completed' "reconnect probe $($reconnect.error)"
Assert-Value $reconnect.processSessionId $reconnect.childSessionId 'reconnect probe session'
[ordered]@{
    parentSessionId = $probe.parentSessionId
    childSessionId = $probe.childSessionId
    probeTask = $probe.taskId
    failureTask = $failed.taskId
    invalidProgramTask = $invalidProgram.taskId
    invalidDirectoryTask = $invalidDirectory.taskId
    timeoutTask = $timed.taskId
    cancelledTask = $cancelled.taskId
    reconnectTask = $reconnect.taskId
    fixtureDirectory = $testRoot
} | ConvertTo-Json
