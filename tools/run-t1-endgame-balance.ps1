param(
    [ValidateSet('core', 'professions', 'holdout')]
    [string]$Suite = 'core',
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'Game.BalanceSimulator/Game.BalanceSimulator.csproj'
$dll = Join-Path $PSScriptRoot 'Game.BalanceSimulator/bin/Debug/net8.0/Game.BalanceSimulator.dll'
$standard = 'knight,priest,elementalist,marksman,trickster'
$replacements = @(
    'warrior,priest,elementalist,marksman,trickster',
    'knight,inquisitor,elementalist,marksman,trickster',
    'knight,priest,arcanist,marksman,trickster',
    'knight,priest,elementalist,beastmaster,trickster',
    'knight,priest,elementalist,marksman,assassin'
) -join ';'

function Invoke-Sample([string]$name, [string[]]$parameters) {
    & dotnet $dll @parameters --output (Join-Path $repositoryRoot "docs/$name.json")
    if ($LASTEXITCODE -ne 0) { throw "Simulator failed: $name" }
}

Push-Location $repositoryRoot
try {
    if (-not $SkipBuild) {
        & dotnet build $project --no-restore --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Simulator build failed' }
    }
    if ($Suite -eq 'professions') {
        Invoke-Sample 't1-profession-balance-final' @('--stages', 'week', '--targets', 'normal,dungeon,elite', '--runs', '3')
    }
    elseif ($Suite -eq 'holdout') {
        # These seeds were not used to tune the original encounters.
        $mixed = 'frost-king-heart,dawn-core-prism,storm-matriarch-plume,plague-widow-essence,deep-overseer-core'
        Invoke-Sample 't1-audit-auto-holdout' @('--stages', 'raid', '--targets', 'endgame', '--runs', '10',
            '--seed-start', '1001', '--composition', $standard, '--soul-loadouts', "none;native;$mixed")
        Invoke-Sample 't1-audit-roles-holdout' @('--stages', 'raid', '--targets', 'endgame', '--runs', '3',
            '--seed-start', '1001', '--composition', $replacements, '--soul-loadouts', $mixed)
        Invoke-Sample 't1-audit-party4-holdout' @('--stages', 'week', '--targets', 'endgame', '--runs', '10',
            '--seed-start', '1001', '--composition', 'knight,priest,elementalist,marksman', '--mode', 'manual')
        $singleBearer = @('none', 'deep-overseer-core', 'plague-widow-essence', 'molten-warlord-brand',
            'frost-king-heart', 'storm-matriarch-plume', 'dawn-core-prism') |
            ForEach-Object { "none,none,none,$_,none" }
        Invoke-Sample 't1-audit-same-bearer-holdout' @('--stages', 'raid', '--targets', 'endgame', '--runs', '3',
            '--seed-start', '1001', '--composition', $standard, '--soul-loadouts', ($singleBearer -join ';'))
        Invoke-Sample 't1-audit-element-matchups' @('--stages', 'raid', '--targets', 'endgame', '--runs', '3',
            '--seed-start', '1001', '--elements', 'Fire,Water,Earth,Wind', '--weapon-element', 'Water',
            '--composition', $standard, '--soul-loadouts', 'none')
        Invoke-Sample 't1-audit-duo-prepared' @('--stages', 'raid', '--targets', 'endgame', '--runs', '3',
            '--seed-start', '1001', '--composition', 'knight,priest', '--mode', 'manual',
            '--soul-loadouts', 'none;frost-king-heart,dawn-core-prism')
    }
    else {
        Invoke-Sample 't1-endgame-solo-boundary' @('--stages', 'week', '--targets', 'endgame', '--runs', '1')
        Invoke-Sample 't1-endgame-party2-final' @('--stages', 'week', '--targets', 'endgame', '--runs', '5', '--composition', 'knight,priest')
        Invoke-Sample 't1-endgame-party3-final' @('--stages', 'week', '--targets', 'endgame', '--runs', '5', '--composition', 'knight,priest,elementalist', '--mode', 'manual')
        Invoke-Sample 't1-endgame-party4-final' @('--stages', 'week', '--targets', 'endgame', '--runs', '10', '--composition', 'knight,priest,elementalist,marksman', '--mode', 'manual')
        Invoke-Sample 't1-endgame-party-final' @('--stages', 'week', '--targets', 'endgame', '--runs', '10', '--composition', $standard, '--mode', 'manual')
        Invoke-Sample 't1-endgame-auto-entry' @('--stages', 'week', '--targets', 'endgame', '--runs', '10', '--composition', $standard)
        Invoke-Sample 't1-endgame-auto-stable' @('--stages', 'raid', '--targets', 'endgame', '--runs', '10', '--composition', $standard)
        Invoke-Sample 't1-endgame-role-coverage' @('--stages', 'week', '--targets', 'endgame', '--runs', '3', '--composition', $replacements, '--mode', 'manual')
    }
}
finally { Pop-Location }
