[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Chrome', 'Edge', 'Brave')]
    [string]$Browser,
    [string]$BrowserPath,
    [string]$UserDataDir = (Join-Path $env:LOCALAPPDATA 'Chrome-ChildSession'),
    [string]$Cli = $env:WINDOWS_CHILD_SESSION_EXE
)

$ErrorActionPreference = 'Stop'

if (-not $Cli) {
    $Cli = Join-Path $env:LOCALAPPDATA 'WindowsChildSession\bin\WindowsChildSession.exe'
}
if (-not (Test-Path -LiteralPath $Cli -PathType Leaf)) {
    throw "WindowsChildSession.exe was not found at '$Cli'. See the installation steps in README.md."
}
$Cli = (Get-Item -LiteralPath $Cli).FullName

if (-not $BrowserPath) {
    $relativePath = switch ($Browser) {
        'Chrome' { 'Google\Chrome\Application\chrome.exe' }
        'Edge'   { 'Microsoft\Edge\Application\msedge.exe' }
        'Brave'  { 'BraveSoftware\Brave-Browser\Application\brave.exe' }
    }

    foreach ($root in @($env:LOCALAPPDATA, $env:ProgramW6432, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($root) {
            $candidate = Join-Path $root $relativePath
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $BrowserPath = $candidate
                break
            }
        }
    }
}
if (-not $BrowserPath -or -not (Test-Path -LiteralPath $BrowserPath -PathType Leaf)) {
    throw "$Browser executable could not be found. Supply the full path with -BrowserPath."
}

$executable = (Get-Item -LiteralPath $BrowserPath).FullName
$dataDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($UserDataDir)
$taskId = [guid]::NewGuid().ToString('D')
$requestPath = Join-Path $env:TEMP "windows-child-session-$taskId.json"

try {
    @{
        taskId = $taskId
        cwd = Split-Path -LiteralPath $executable
        program = $executable
        arguments = @("--user-data-dir=$dataDirectory")
        timeoutSeconds = 86400
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $requestPath -Encoding utf8

    & $Cli run --request $requestPath --detach
    $cliExitCode = $LASTEXITCODE
} finally {
    Remove-Item -LiteralPath $requestPath -ErrorAction SilentlyContinue
}

exit $cliExitCode
