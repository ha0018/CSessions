[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Chrome', 'Edge', 'Brave')]
    [string]$Browser,
    [string]$BrowserPath,
    [string]$UserDataDir,
    [string]$Cli
)

$launcher = Join-Path $PSScriptRoot '..\.agents\skills\windows-child-session\scripts\Open-ChildSessionBrowser.ps1'
& $launcher @PSBoundParameters
exit $LASTEXITCODE
