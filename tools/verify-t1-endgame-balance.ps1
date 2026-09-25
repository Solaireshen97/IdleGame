param([switch]$RequireFresh)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceHashes = @{}
$expectedDungeons = @('kobold-mine-depths', 'plague-crypt-depths', 'ragefire-heart',
    'frostspring-throne', 'windfury-spire', 'dawn-core')

function Read-Report([string]$name) {
    $path = Join-Path $repositoryRoot "docs/$name"
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($null -eq $report.PSObject.Properties['SourceFiles']) {
        Assert-Balance (-not $RequireFresh) "$name has no source fingerprints; rerun it before claiming current balance"
        Write-Warning "$name is a legacy report without source fingerprints; its results are historical only."
        return $report
    }
    Assert-Balance ($report.SourcesUnchangedDuringRun -eq $true) "$name source files changed during simulation"
    foreach ($requiredSource in @('Game.Server/appsettings.json', 'Game.Server/world.json',
        'Game.Server/Services/BattleService.cs', 'Game.Server/Services/MonsterCombatService.cs',
        'tools/Game.BalanceSimulator/Program.cs')) {
        Assert-Balance ($null -ne $report.SourceFiles.PSObject.Properties[$requiredSource]) "$name is missing the $requiredSource fingerprint"
    }
    foreach ($source in $report.SourceFiles.PSObject.Properties) {
        if (-not $sourceHashes.ContainsKey($source.Name)) {
            $sourcePath = Join-Path $repositoryRoot $source.Name
            Assert-Balance (Test-Path -LiteralPath $sourcePath -PathType Leaf) "$name references missing source $($source.Name)"
            $sourceText = [System.IO.File]::ReadAllText($sourcePath).Replace("`r`n", "`n")
            $sourceHashes[$source.Name] = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData(
                [System.Text.Encoding]::UTF8.GetBytes($sourceText)))
        }
        Assert-Balance ($sourceHashes[$source.Name] -eq $source.Value) "$name is stale for $($source.Name); rerun the report"
    }
    return $report
}

function Assert-Balance([bool]$condition, [string]$message) {
    if (-not $condition) { throw "T1 endgame balance gate failed: $message" }
}

function Victory-Count($report) {
    return @($report.Samples | Where-Object Victory).Count
}

function Dungeon-AverageRounds($report) {
    return @($report.Samples | Group-Object Dungeon | ForEach-Object {
        [pscustomobject]@{
            Dungeon = $_.Name
            MeanRounds = ($_.Group | Measure-Object Rounds -Average).Average
        }
    })
}

function Pressure-Statistics($samples, [string]$name) {
    $samples = @($samples)
    Assert-Balance ($samples.Count -gt 0) "$name pressure statistics require samples"
    foreach ($sample in $samples) {
        foreach ($metric in @('Survivors', 'SoftEnrageCasts', 'HardEnrageCasts')) {
            Assert-Balance ($null -ne $sample.PSObject.Properties[$metric]) "$name is missing $metric; rerun with the current simulator"
        }
        Assert-Balance ($sample.Survivors -ge 0 -and $sample.Survivors -le $sample.PartySize -and
            $sample.SoftEnrageCasts -ge 0 -and $sample.HardEnrageCasts -ge 0) "$name contains invalid pressure metrics"
    }
    $rounds = @($samples.Rounds | Sort-Object)
    return [pscustomobject]@{
        Name = $name
        Samples = $samples.Count
        Victories = @($samples | Where-Object Victory).Count
        MeanRounds = ($samples | Measure-Object Rounds -Average).Average
        MaxRounds = $rounds[-1]
        P95Rounds = $rounds[[int][Math]::Ceiling($rounds.Count * 0.95) - 1]
        MeanPotions = ($samples | Measure-Object PotionsUsed -Average).Average
        SoftEnrageSamples = @($samples | Where-Object SoftEnrageCasts -gt 0).Count
        HardEnrageSamples = @($samples | Where-Object HardEnrageCasts -gt 0).Count
        CasualtySamples = @($samples | Where-Object { $_.Survivors -lt $_.PartySize }).Count
    }
}

function Assert-ReportShape($report, [string]$name, [int]$samplesPerDungeon,
    [int]$partySize, [string]$stage, [string]$mode) {
    $dungeons = @($report.Samples.Dungeon | Sort-Object -Unique)
    Assert-Balance ($dungeons.Count -eq 6 -and
        @($expectedDungeons | Where-Object { $_ -notin $dungeons }).Count -eq 0) "$name must cover all six endgame dungeons"
    foreach ($dungeon in @($report.Samples | Group-Object Dungeon)) {
        Assert-Balance ($dungeon.Count -eq $samplesPerDungeon) "$name/$($dungeon.Name) has the wrong sample count"
    }
    Assert-Balance (@($report.Samples | Where-Object {
        $_.PartySize -ne $partySize -or $_.Stage -ne $stage -or $_.ControlMode -ne $mode
    }).Count -eq 0) "$name contains the wrong party size, equipment stage or control mode"
    $seedStart = if ($null -ne $report.Assumptions.PSObject.Properties['SeedStart']) { [int]$report.Assumptions.SeedStart } else { 1 }
    $runs = [int]$report.Assumptions.RunsPerScenario
    Assert-Balance ($runs -gt 0) "$name must have at least one seed per scenario"
    foreach ($scenario in @($report.Samples | Group-Object Dungeon, Profession, SoulLoadout)) {
        $seeds = @($scenario.Group.Seed | Sort-Object -Unique)
        Assert-Balance ($scenario.Count -eq $runs -and $seeds.Count -eq $runs -and
            @($seeds | Where-Object { $_ -lt $seedStart -or $_ -ge $seedStart + $runs }).Count -eq 0) "$name/$($scenario.Name) has duplicate, missing or unexpected seeds"
    }
}

$solo = Read-Report 't1-endgame-solo-boundary.json'
$party2 = Read-Report 't1-endgame-party2-final.json'
$party3 = Read-Report 't1-endgame-party3-final.json'
$party4 = Read-Report 't1-endgame-party4-final.json'
$party5 = Read-Report 't1-endgame-party-final.json'
$autoEntry = Read-Report 't1-endgame-auto-entry.json'
$autoStable = Read-Report 't1-endgame-auto-stable.json'
$roleCoverage = Read-Report 't1-endgame-role-coverage.json'
$soulBalance = Read-Report 't1-soul-imprint-balance.json'
$soulSpecialists = Read-Report 't1-soul-imprint-specialists.json'
$soulRoleCoverage = Read-Report 't1-soul-imprint-role-coverage.json'

Assert-ReportShape $solo 'solo' 10 1 'week' 'auto'
Assert-ReportShape $party2 'party2' 5 2 'week' 'auto'
Assert-ReportShape $party3 'party3' 5 3 'week' 'manual'
Assert-ReportShape $party4 'party4' 10 4 'week' 'manual'
Assert-ReportShape $party5 'party5' 10 5 'week' 'manual'
Assert-ReportShape $autoEntry 'autoEntry' 10 5 'week' 'auto'
Assert-ReportShape $autoStable 'autoStable' 10 5 'raid' 'auto'
Assert-ReportShape $roleCoverage 'roleCoverage' 15 5 'week' 'manual'
Assert-ReportShape $soulBalance 'soulBalance' 45 5 'raid' 'auto'
Assert-ReportShape $soulSpecialists 'soulSpecialists' 21 5 'raid' 'auto'
Assert-ReportShape $soulRoleCoverage 'soulRoleCoverage' 15 5 'raid' 'auto'

Assert-Balance (@($solo.Samples).Count -eq 60 -and (Victory-Count $solo) -eq 0) 'solo boundary must remain 0/60'
Assert-Balance (@($party2.Samples).Count -eq 30 -and (Victory-Count $party2) -eq 0) 'two-player boundary must remain 0/30'
$party3Wins = Victory-Count $party3
Assert-Balance (@($party3.Samples).Count -eq 30 -and $party3Wins -ge 1 -and $party3Wins -le 6) 'three-player manual must remain a rare 1-20% challenge'

$party4Wins = Victory-Count $party4
Assert-Balance (@($party4.Samples).Count -eq 60 -and $party4Wins -ge 42) 'four-player manual must achieve at least 70% success'
$party4Pressure = Pressure-Statistics $party4.Samples 'four-player manual'
$party5Pressure = Pressure-Statistics $party5.Samples 'five-player manual'
Assert-Balance ($party4Pressure.SoftEnrageSamples -ge 30) 'four-player manual must experience soft enrage in at least half of its samples'
Assert-Balance ($party4Pressure.MeanRounds -ge $party5Pressure.MeanRounds * 1.15) 'four-player manual must take at least 15% more rounds than five-player manual'
foreach ($dungeon in @($party4.Samples | Group-Object Dungeon)) {
    Assert-Balance (@($dungeon.Group).Count -eq 10) "$($dungeon.Name) must have ten four-player samples"
    Assert-Balance (@($dungeon.Group | Where-Object Victory).Count -ge 5) "$($dungeon.Name) must have at least 50% four-player success"
    $fivePlayerSamples = @($party5.Samples | Where-Object Dungeon -eq $dungeon.Name)
    Assert-Balance ((($dungeon.Group.Seed | Sort-Object) -join ',') -eq
        (($fivePlayerSamples.Seed | Sort-Object) -join ',')) "$($dungeon.Name) four- and five-player comparisons must use the same seeds"
    Assert-Balance (($dungeon.Group | Measure-Object Rounds -Average).Average -gt
        ($fivePlayerSamples | Measure-Object Rounds -Average).Average) "$($dungeon.Name) four-player manual must take more rounds than five-player manual"
}

Assert-Balance (@($party5.Samples).Count -eq 60 -and (Victory-Count $party5) -eq 60) 'five-player manual must remain 60/60'
Assert-Balance (@($autoEntry.Samples).Count -eq 60 -and (Victory-Count $autoEntry) -eq 60) 'normal T1 Auto must clear the fixed samples'
$entryRounds = Dungeon-AverageRounds $autoEntry
Assert-Balance (@($entryRounds | Where-Object MeanRounds -ge 22).Count -ge 1) 'normal T1 Auto must still enter soft enrage in at least one dungeon'

Assert-Balance (@($autoStable.Samples).Count -eq 60 -and (Victory-Count $autoStable) -eq 60) 'elite-prepared Auto must remain 60/60'
foreach ($dungeon in Dungeon-AverageRounds $autoStable) {
    Assert-Balance ($dungeon.MeanRounds -lt 22) "$($dungeon.Dungeon) elite-prepared Auto must finish before soft enrage on average"
}

Assert-Balance (@($roleCoverage.Samples).Count -eq 90 -and (Victory-Count $roleCoverage) -eq 90) 'promotion replacement coverage must remain 90/90'
$coveredRoles = @($roleCoverage.Samples.Profession | ForEach-Object { $_ -split '\+' } | Sort-Object -Unique)
$expectedRoles = @('knight', 'warrior', 'priest', 'inquisitor', 'elementalist', 'arcanist', 'marksman', 'beastmaster', 'assassin', 'trickster')
Assert-Balance ($coveredRoles.Count -eq 10 -and @($expectedRoles | Where-Object { $_ -notin $coveredRoles }).Count -eq 0) 'all ten promotions must appear in replacement coverage'

Assert-Balance (@($soulBalance.Samples).Count -eq 270 -and (Victory-Count $soulBalance) -eq 270) 'soul imprint duplicate and mixed loadout matrix must remain 270/270'
$soulGroups = @($soulBalance.Samples | Group-Object SoulLoadout)
Assert-Balance ($soulGroups.Count -eq 9) 'soul imprint pressure matrix must contain nine loadouts'
foreach ($group in $soulGroups) {
    Assert-Balance (@($group.Group).Count -eq 30) "$($group.Name) must have thirty cross-dungeon pressure samples"
}
$fastestSoul = $soulGroups | Sort-Object { ($_.Group | Measure-Object Rounds -Average).Average } | Select-Object -First 1
$safestSoul = $soulGroups | Sort-Object { ($_.Group | Measure-Object PotionsUsed -Average).Average } | Select-Object -First 1
Assert-Balance ($fastestSoul.Name -ne $safestSoul.Name) 'one soul loadout must not minimize both clear time and potion use'
$mixedSoulLoadouts = @(
    'frost-king-heart+dawn-core-prism+storm-matriarch-plume+plague-widow-essence+deep-overseer-core',
    'frost-king-heart+dawn-core-prism+storm-matriarch-plume+plague-widow-essence+molten-warlord-brand'
)
foreach ($loadout in $mixedSoulLoadouts) {
    $samples = @($soulBalance.Samples | Where-Object SoulLoadout -eq $loadout)
    Assert-Balance ($samples.Count -eq 30 -and @($samples | Where-Object Victory).Count -eq 30) "$loadout must remain 30/30"
    foreach ($dungeon in @($samples | Group-Object Dungeon)) {
        Assert-Balance ((($dungeon.Group | Measure-Object Rounds -Average).Average) -lt 22) "$loadout must finish $($dungeon.Name) before soft enrage on average"
    }
}

Assert-Balance (@($soulSpecialists.Samples).Count -eq 126 -and (Victory-Count $soulSpecialists) -eq 126) 'role-fitted single-soul matrix must remain 126/126'
$darkSpecialist = 'none+none+none+plague-widow-essence+none'
$utilitySpecialists = @(
    'none+none+none+none+deep-overseer-core',
    'none+none+none+none+molten-warlord-brand',
    'frost-king-heart+none+none+none+none',
    'none+none+storm-matriarch-plume+none+none',
    'none+dawn-core-prism+none+none+none'
)
foreach ($loadout in $utilitySpecialists) {
    $hasNiche = $false
    foreach ($dungeon in @($soulSpecialists.Samples.Dungeon | Sort-Object -Unique)) {
        $candidate = @($soulSpecialists.Samples | Where-Object { $_.SoulLoadout -eq $loadout -and $_.Dungeon -eq $dungeon })
        $dark = @($soulSpecialists.Samples | Where-Object { $_.SoulLoadout -eq $darkSpecialist -and $_.Dungeon -eq $dungeon })
        $baseline = @($soulSpecialists.Samples | Where-Object { $_.SoulLoadout -eq 'none' -and $_.Dungeon -eq $dungeon })
        Assert-Balance ($candidate.Count -eq $soulSpecialists.Assumptions.RunsPerScenario -and
            $dark.Count -eq $candidate.Count -and $baseline.Count -eq $candidate.Count) "$loadout/$dungeon must have matching utility, pure-damage and no-soul samples"
        $candidateRounds = ($candidate | Measure-Object Rounds -Average).Average
        $candidatePotions = ($candidate | Measure-Object PotionsUsed -Average).Average
        $darkRounds = ($dark | Measure-Object Rounds -Average).Average
        $darkPotions = ($dark | Measure-Object PotionsUsed -Average).Average
        $baselineRounds = ($baseline | Measure-Object Rounds -Average).Average
        $baselinePotions = ($baseline | Measure-Object PotionsUsed -Average).Average
        # A utility soul can trade clear speed for economy. A strict advantage in
        # either dimension prevents pure damage from dominating it; it need not
        # dominate pure damage itself. Also require a real gain over no soul.
        $hasTradeoff = $candidateRounds -lt $darkRounds -or $candidatePotions -lt $darkPotions
        $improvesBaseline = $candidateRounds -lt $baselineRounds -or $candidatePotions -lt $baselinePotions
        if ($hasTradeoff -and $improvesBaseline) {
            $hasNiche = $true
            break
        }
    }
    Assert-Balance $hasNiche "$loadout must offer a speed/economy tradeoff not dominated by pure damage and improve at least one dimension over no soul in the same dungeon"
}

Assert-Balance (@($soulRoleCoverage.Samples).Count -eq 90 -and (Victory-Count $soulRoleCoverage) -eq 90) 'mixed-soul promotion replacement coverage must remain 90/90'
$soulCoveredRoles = @($soulRoleCoverage.Samples.Profession | ForEach-Object { $_ -split '\+' } | Sort-Object -Unique)
Assert-Balance ($soulCoveredRoles.Count -eq 10 -and @($expectedRoles | Where-Object { $_ -notin $soulCoveredRoles }).Count -eq 0) 'mixed-soul coverage must include all ten promotions'
foreach ($dungeon in Dungeon-AverageRounds $soulRoleCoverage) {
    Assert-Balance ($dungeon.MeanRounds -lt 22) "$($dungeon.Dungeon) mixed-soul promotion coverage must finish before soft enrage on average"
}

$requiredBossSkills = @{
    'kobold-mine-depths' = @('金牙碎岩击', '首领怒吼')
    'plague-crypt-depths' = @('死亡毒雾', '暗丝结茧')
    'ragefire-heart' = @('熔岩喷发', '熔核狂怒')
    'frostspring-throne' = @('寒泉暴雪', '冰川护甲')
    'windfury-spire' = @('旋羽风暴', '族母战歌')
    'dawn-core' = @('辉光新星', '守卫光盾')
}
foreach ($dungeonCode in $requiredBossSkills.Keys) {
    $samples = @($autoEntry.Samples | Where-Object Dungeon -eq $dungeonCode)
    foreach ($skillName in $requiredBossSkills[$dungeonCode]) {
        $uses = 0
        foreach ($sample in $samples) {
            $property = $sample.BossSkillUses.PSObject.Properties[$skillName]
            if ($null -ne $property) { $uses += [int]$property.Value }
        }
        Assert-Balance ($uses -gt 0) "$dungeonCode must exercise $skillName in the Auto pressure report"
    }
}

foreach ($pressure in @($party4Pressure, $party5Pressure,
    (Pressure-Statistics $autoEntry.Samples 'normal T1 Auto'),
    (Pressure-Statistics $autoStable.Samples 'elite-prepared Auto'))) {
    Write-Output ("{0}: {1}/{2} wins, mean {3:N2} / P95 {4} / max {5} rounds, {6} soft / {7} hard enrage samples, {8} casualty samples, {9:N2} mean potions." -f
        $pressure.Name, $pressure.Victories, $pressure.Samples, $pressure.MeanRounds,
        $pressure.P95Rounds, $pressure.MaxRounds, $pressure.SoftEnrageSamples,
        $pressure.HardEnrageSamples, $pressure.CasualtySamples, $pressure.MeanPotions)
}
Write-Output "T1 endgame balance gates passed: solo 0/60, duo 0/30, trio $party3Wins/30, four-player $party4Wins/60, five-player 60/60, soul matrix 270/270, mixed-soul role coverage 90/90."
