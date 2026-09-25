param([switch]$SkipBuild)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $PSScriptRoot 'Game.BalanceSimulator/bin/Debug/net8.0/Game.BalanceSimulator.dll'
$profilePath = 'docs/talent-build-profiles.json'

Push-Location $repositoryRoot
try {
    & node tools/audit-talent-paths.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Talent prerequisite audit failed' }
    if (-not $SkipBuild) {
        & dotnet build tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj --no-restore --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Simulator build failed' }
    }
    $profiles = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
    $names = @($profiles.PSObject.Properties.Name)
    $standard = @('knight', 'priest', 'elementalist', 'marksman', 'trickster')
    $compositions = foreach ($name in $names) {
        $members = $standard.Clone()
        $index = [Array]::IndexOf($members, $profiles.$name.Role)
        if ($index -lt 0) { throw "Unsupported profile role: $name" }
        $members[$index] = $name
        $members -join ','
    }
    # Same promotion within each class, nine talent points, no souls. The solo
    # grid measures local viability; one-slot party replacements expose utility.
    & dotnet $dll --talent-builds $profilePath --roles ($names -join ',') `
        --stages week --targets dungeon,elite --runs 3 --seed-start 2001 `
        --soul-loadouts none --output docs/talent-balance-solo.json
    if ($LASTEXITCODE -ne 0) { throw 'Solo talent simulation failed' }
    & dotnet $dll --talent-builds $profilePath --composition ($compositions -join ';') `
        --stages week --targets endgame --runs 3 --seed-start 2001 `
        --soul-loadouts none --output docs/talent-balance-party.json
    if ($LASTEXITCODE -ne 0) { throw 'Party talent simulation failed' }
    & node tools/verify-talent-balance.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Talent balance verification failed' }
}
finally { Pop-Location }
