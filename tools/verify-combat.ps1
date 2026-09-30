param(
    [switch]$BrowserOnly,
    [switch]$SkipServer,
    [switch]$SkipClientBuild,
    [string]$BrowserChannel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$previousChannel = $env:PLAYWRIGHT_CHANNEL

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    if ($BrowserChannel) { $env:PLAYWRIGHT_CHANNEL = $BrowserChannel }
    # Require the local, locked dependency rather than relying on NODE_PATH.
    if (-not (Test-Path 'Game.Server.Tests/Client/node_modules/playwright/package.json')) {
        throw 'Missing browser test dependencies. Run: pnpm --dir Game.Server.Tests/Client install --frozen-lockfile --ignore-scripts'
    }
    if (-not ($BrowserOnly -or $SkipServer)) {
        Invoke-Checked dotnet @('test', 'Game.Server.Tests/Game.Server.Tests.csproj', '--configuration', 'Debug', '--nologo')
        Invoke-Checked dotnet @('build', 'tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj', '--configuration', 'Debug', '--nologo')
    }
    if (-not $SkipClientBuild) {
        Invoke-Checked dotnet @('build', 'Game.Client/Game.Client.csproj', '--configuration', 'Debug', '--nologo')
    }
    Invoke-Checked node @('--test', 'Game.Server.Tests/Client/battle-combat.test.cjs')
}
finally {
    $env:PLAYWRIGHT_CHANNEL = $previousChannel
    Pop-Location
}
