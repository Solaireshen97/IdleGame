param([switch]$SkipBuild, [switch]$SkipVerification)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$simulator = Join-Path $repositoryRoot 'tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj'
$elements = 'Fire,Water,Earth,Wind,Light,Dark'
$standardComposition = 'knight,priest,elementalist,marksman,trickster'

function Invoke-Simulator([string[]]$arguments) {
    & dotnet run --project $simulator --no-build -- @arguments
    if ($LASTEXITCODE -ne 0) { throw "Balance simulator failed with exit code $LASTEXITCODE" }
}

if (-not $SkipBuild) {
    & dotnet build $simulator --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Balance simulator build failed with exit code $LASTEXITCODE" }
}

$pressureLoadouts = @(
    'none',
    'deep-overseer-core',
    'plague-widow-essence',
    'molten-warlord-brand',
    'frost-king-heart',
    'storm-matriarch-plume',
    'dawn-core-prism',
    'frost-king-heart,dawn-core-prism,storm-matriarch-plume,plague-widow-essence,deep-overseer-core',
    'frost-king-heart,dawn-core-prism,storm-matriarch-plume,plague-widow-essence,molten-warlord-brand'
) -join ';'
Invoke-Simulator @('--stages', 'raid', '--targets', 'endgame', '--elements', $elements,
    '--composition', $standardComposition, '--runs', '5', '--mode', 'auto',
    '--soul-loadouts', $pressureLoadouts,
    '--output', (Join-Path $repositoryRoot 'docs/t1-soul-imprint-balance.json'))

$specialistLoadouts = @(
    'none',
    'none,none,none,none,deep-overseer-core',
    'none,none,none,plague-widow-essence,none',
    'none,none,none,none,molten-warlord-brand',
    'frost-king-heart,none,none,none,none',
    'none,none,storm-matriarch-plume,none,none',
    'none,dawn-core-prism,none,none,none'
) -join ';'
Invoke-Simulator @('--stages', 'raid', '--targets', 'endgame', '--elements', $elements,
    '--composition', $standardComposition, '--runs', '3', '--mode', 'auto',
    '--soul-loadouts', $specialistLoadouts,
    '--output', (Join-Path $repositoryRoot 'docs/t1-soul-imprint-specialists.json'))

$roleCompositions = @(
    'warrior,priest,elementalist,marksman,trickster',
    'knight,inquisitor,elementalist,marksman,trickster',
    'knight,priest,arcanist,marksman,trickster',
    'knight,priest,elementalist,beastmaster,trickster',
    'knight,priest,elementalist,marksman,assassin'
) -join ';'
$roleLoadout = 'frost-king-heart,dawn-core-prism,storm-matriarch-plume,plague-widow-essence,deep-overseer-core'
Invoke-Simulator @('--stages', 'raid', '--targets', 'endgame', '--elements', $elements,
    '--composition', $roleCompositions, '--runs', '3', '--mode', 'auto',
    '--soul-loadouts', $roleLoadout,
    '--output', (Join-Path $repositoryRoot 'docs/t1-soul-imprint-role-coverage.json'))

if (-not $SkipVerification) {
    & (Join-Path $PSScriptRoot 'verify-t1-endgame-balance.ps1') -RequireFresh
}
