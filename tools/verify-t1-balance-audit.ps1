# PowerShell 7. This checks saved reports only; it never runs the simulator.
# The profession baseline uses seeds 1-3. Every additional audit uses seeds 1001+.
. (Join-Path $PSScriptRoot 'verify-t1-endgame-balance.ps1') -RequireFresh

$auditWorld = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'Game.Server/world.json') -Raw | ConvertFrom-Json).World
$auditRegionElements = @{}
foreach ($region in $auditWorld.Regions) { $auditRegionElements[$region.Code] = $region.FeaturedElement }
$auditDungeonElements = @{}
foreach ($dungeon in $auditWorld.Dungeons) {
    $auditDungeonElements[$dungeon.Code] = $auditRegionElements[$dungeon.RegionCode]
}
$auditSourcePaths = @(
    foreach ($directory in @('Game.Server/Services', 'Game.Server/Configuration', 'Game.Shared')) {
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot $directory) -Filter '*.cs' -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { [IO.Path]::GetRelativePath($repositoryRoot, $_.FullName).Replace('\', '/') }
    }
    'Game.Server/appsettings.json'
    'Game.Server/world.json'
    'tools/Game.BalanceSimulator/Program.cs'
) | Sort-Object

function Assert-AuditSet($actual, $expected, [string]$name) {
    $actual = @($actual)
    $expected = @($expected)
    Assert-Balance ($actual.Count -eq $expected.Count -and
        @($actual | Sort-Object -Unique).Count -eq $expected.Count -and
        @($expected | Where-Object { $_ -notin $actual }).Count -eq 0) "$name has missing, repeated or unexpected values"
}

function Assert-AuditProvenance($report, [string]$name) {
    Assert-Balance ($report.SourcesUnchangedDuringRun -eq $true) "$name changed source files during its run"
    Assert-AuditSet @($report.SourceFiles.PSObject.Properties.Name) $auditSourcePaths "$name source inventory"
    Assert-Balance (-not [string]::IsNullOrWhiteSpace($report.GeneratedAtUtc)) "$name has no generation timestamp"
}

function Assert-AuditGrid($report, [string]$name, [string[]]$dungeons,
    [string[]]$professions, [string[]]$loadouts, [int]$party, [string]$stage,
    [string]$mode, [int]$runs, [int]$seedStart = 1001,
    [string]$weaponElement = 'SameAsDungeonRegion') {
    Assert-AuditProvenance $report $name
    $assumptions = $report.Assumptions
    Assert-Balance ($assumptions.RunsPerScenario -eq $runs -and $assumptions.SeedStart -eq $seedStart -and
        $assumptions.PartySize -eq $party -and $assumptions.ControlMode -eq $mode -and
        $assumptions.WeaponElement -eq $weaponElement -and $assumptions.MaxRounds -eq 250) "$name has unexpected simulation assumptions"
    $recordedLoadouts = @($assumptions.SoulLoadouts | ForEach-Object { $_ -join '+' })
    Assert-AuditSet $recordedLoadouts $loadouts "$name declared soul loadouts"
    if ($party -gt 1) {
        $recordedCompositions = @($assumptions.Compositions | ForEach-Object { $_ -join '+' })
        Assert-AuditSet $recordedCompositions $professions "$name declared parties"
    }

    $expectedCount = $dungeons.Count * $professions.Count * $loadouts.Count * $runs
    $samples = @($report.Samples)
    Assert-Balance ($samples.Count -eq $expectedCount) "$name must contain $expectedCount samples"
    Assert-AuditSet @($samples.Dungeon | Sort-Object -Unique) $dungeons "$name dungeons"
    Assert-AuditSet @($samples.Profession | Sort-Object -Unique) $professions "$name professions"
    Assert-AuditSet @($samples.SoulLoadout | Sort-Object -Unique) $loadouts "$name soul loadouts"

    $cells = @{}
    foreach ($sample in $samples) {
        Assert-Balance ($sample.Stage -eq $stage -and $sample.ControlMode -eq $mode -and
            $sample.PartySize -eq $party) "$name contains the wrong stage, mode or party size"
        Assert-Balance ($sample.Element -eq $auditDungeonElements[$sample.Dungeon]) "$name/$($sample.Dungeon) has the wrong region element"
        Assert-Balance ($sample.Seed -is [long] -or $sample.Seed -is [int]) "$name contains a non-integer seed"
        Assert-Balance ($sample.Seed -ge $seedStart -and $sample.Seed -lt ($seedStart + $runs)) "$name contains an out-of-range seed"
        $key = @($sample.Dungeon, $sample.Profession, $sample.SoulLoadout, $sample.Seed) -join '|'
        Assert-Balance (-not $cells.ContainsKey($key)) "$name contains duplicate sample $key"
        $cells[$key] = $true

        $builds = @($sample.Builds | Sort-Object Slot)
        $members = @($sample.Profession -split '\+')
        Assert-Balance ($builds.Count -eq $party -and $members.Count -eq $party) "$name/$key has an incomplete build record"
        Assert-AuditSet @($builds.Slot) @(1..$party) "$name/$key build slots"
        $expectedWeaponElement = if ($weaponElement -eq 'SameAsDungeonRegion') { $sample.Element } else { $weaponElement }
        for ($index = 0; $index -lt $party; $index++) {
            $build = $builds[$index]
            Assert-Balance ($build.Profession -eq $members[$index] -and
                $build.WeaponElement -eq $expectedWeaponElement) "$name/$key build profession or weapon element does not match its scenario"
            $skills = @($build.EquippedSkills)
            Assert-Balance ($skills.Count -ge 1 -and $skills.Count -le 5 -and
                @($skills | Sort-Object -Unique).Count -eq $skills.Count -and
                @($build.TalentRanks.PSObject.Properties).Count -gt 0) "$name/$key has missing or invalid equipped skills or talents"
        }
    }
    # Check every Cartesian-product cell, rather than accepting a matching total.
    foreach ($dungeon in $dungeons) {
        foreach ($profession in $professions) {
            foreach ($loadout in $loadouts) {
                foreach ($seed in $seedStart..($seedStart + $runs - 1)) {
                    $key = @($dungeon, $profession, $loadout, $seed) -join '|'
                    Assert-Balance ($cells.ContainsKey($key)) "$name is missing sample $key"
                }
            }
        }
    }
    $pressure = Pressure-Statistics $samples $name
    Write-Output ("{0}: {1}/{2} wins, mean {3:N2} / P95 {4} / max {5} rounds, {6} soft / {7} hard enrage samples, {8} casualty samples, {9:N2} mean potions." -f
        $pressure.Name, $pressure.Victories, $pressure.Samples, $pressure.MeanRounds,
        $pressure.P95Rounds, $pressure.MaxRounds, $pressure.SoftEnrageSamples,
        $pressure.HardEnrageSamples, $pressure.CasualtySamples, $pressure.MeanPotions)
}

$auditBaselineReports = @($solo, $party2, $party3, $party4, $party5, $autoEntry,
    $autoStable, $roleCoverage, $soulBalance, $soulSpecialists, $soulRoleCoverage)
$auditBaselineCount = 0
foreach ($baseline in $auditBaselineReports) {
    Assert-AuditProvenance $baseline 'endgame baseline'
    $auditBaselineCount += @($baseline.Samples).Count
}
Assert-Balance ($auditBaselineCount -eq 936) 'the endgame baseline must contain 936 samples'

$auditStandardParty = 'knight+priest+elementalist+marksman+trickster'
$auditFourParty = 'knight+priest+elementalist+marksman'
$auditMixedSouls = 'frost-king-heart+dawn-core-prism+storm-matriarch-plume+plague-widow-essence+deep-overseer-core'
$auditRoleParties = @(
    'warrior+priest+elementalist+marksman+trickster',
    'knight+inquisitor+elementalist+marksman+trickster',
    'knight+priest+arcanist+marksman+trickster',
    'knight+priest+elementalist+beastmaster+trickster',
    'knight+priest+elementalist+marksman+assassin'
)
$auditProfessionDungeons = @(
    foreach ($region in $auditWorld.Regions) {
        $normal = @($auditWorld.Dungeons | Where-Object {
            $_.RegionCode -eq $region.Code -and $_.DungeonKind -eq 'Hunt' -and $_.MinimumLevel -eq 7
        })
        $elite = @($auditWorld.Dungeons | Where-Object {
            $_.RegionCode -eq $region.Code -and $_.DungeonKind -eq 'Elite' -and $_.MinimumLevel -eq 10
        })
        Assert-Balance ($normal.Count -eq 1 -and $elite.Count -eq 1) "$($region.Code) has ambiguous profession-audit targets"
        $normal[0].Code
        $region.FeaturedDungeonCode
        $elite[0].Code
    }
)
Assert-Balance ($auditProfessionDungeons.Count -eq 18 -and
    @($auditProfessionDungeons | Sort-Object -Unique).Count -eq 18) 'profession audit must cover 18 distinct ordinary, regional and elite targets'
$auditProfession = Read-Report 't1-profession-balance-final.json'
Assert-AuditGrid $auditProfession 'profession baseline' $auditProfessionDungeons $expectedRoles @('native') 1 'week' 'auto' 3 1
Assert-Balance ((Victory-Count $auditProfession) -eq 540) 'all 540 ordinary, regional and elite profession samples must win'
foreach ($profession in $expectedRoles) {
    Assert-Balance (@($auditProfession.Samples | Where-Object Profession -eq $profession).Count -eq 54) "$profession must have 54 profession-baseline samples"
}
foreach ($kind in @('Hunt', 'Dungeon', 'Elite')) {
    $kindCodes = @($auditWorld.Dungeons | Where-Object DungeonKind -eq $kind | Select-Object -ExpandProperty Code)
    Assert-Balance (@($auditProfession.Samples | Where-Object { $_.Dungeon -in $kindCodes }).Count -eq 180) "profession baseline must have 180 $kind samples"
}

$auditAuto = Read-Report 't1-audit-auto-holdout.json'
Assert-AuditGrid $auditAuto 'prepared Auto holdout' $expectedDungeons @($auditStandardParty) @('none', 'native', $auditMixedSouls) 5 'raid' 'auto' 10
foreach ($loadout in @('native', $auditMixedSouls)) {
    $prepared = @($auditAuto.Samples | Where-Object SoulLoadout -eq $loadout)
    Assert-Balance (@($prepared | Where-Object { -not $_.Victory -or $_.HardEnrageCasts -gt 0 }).Count -eq 0) "prepared Auto/$loadout must win without hard enrage"
}

$auditRoles = Read-Report 't1-audit-roles-holdout.json'
Assert-AuditGrid $auditRoles 'promotion holdout' $expectedDungeons $auditRoleParties @($auditMixedSouls) 5 'raid' 'auto' 3
Assert-Balance (@($auditRoles.Samples | Where-Object { -not $_.Victory -or $_.HardEnrageCasts -gt 0 }).Count -eq 0) 'prepared promotion holdout must win without hard enrage'

$auditParty4Manual = Read-Report 't1-audit-party4-holdout.json'
Assert-AuditGrid $auditParty4Manual 'four-player manual holdout' $expectedDungeons @($auditFourParty) @('native') 4 'week' 'manual' 10
Assert-Balance ((Victory-Count $auditParty4Manual) -ge 42) 'four-player manual holdout must achieve at least 70% success'

$auditSingleSouls = @('none', 'deep-overseer-core', 'plague-widow-essence',
    'molten-warlord-brand', 'frost-king-heart', 'storm-matriarch-plume', 'dawn-core-prism')
$auditSingleLoadouts = @($auditSingleSouls | ForEach-Object { "none+none+none+$_+none" })
$auditSameBearer = Read-Report 't1-audit-same-bearer-holdout.json'
Assert-AuditGrid $auditSameBearer 'same-bearer soul holdout' $expectedDungeons @($auditStandardParty) $auditSingleLoadouts 5 'raid' 'auto' 3

$auditElementDungeons = @($expectedDungeons | Where-Object { $auditDungeonElements[$_] -in @('Fire', 'Water', 'Earth', 'Wind') })
Assert-Balance ($auditElementDungeons.Count -eq 4) 'element audit must cover Fire, Water, Earth and Wind regions'
$auditElements = Read-Report 't1-audit-element-matchups.json'
Assert-AuditGrid $auditElements 'Water weapon matchup audit' $auditElementDungeons @($auditStandardParty) @('none') 5 'raid' 'auto' 3 1001 'Water'

$auditDuo = Read-Report 't1-audit-duo-prepared.json'
Assert-AuditGrid $auditDuo 'prepared duo audit' $expectedDungeons @('knight+priest') @('none', 'frost-king-heart+dawn-core-prism') 2 'raid' 'manual' 3

$auditParty4Auto = Read-Report 't1-audit-party4-auto.json'
Assert-AuditGrid $auditParty4Auto 'four-player Auto audit' $expectedDungeons @($auditFourParty) @('native') 4 'week' 'auto' 5

$auditHoldoutCount = 0
foreach ($auditReport in @($auditAuto, $auditRoles, $auditParty4Manual, $auditSameBearer, $auditElements, $auditDuo)) {
    $auditHoldoutCount += @($auditReport.Samples).Count
}
Assert-Balance ($auditHoldoutCount -eq 504) 'the six holdout matrices must contain 504 samples'
$auditTotal = $auditBaselineCount + @($auditProfession.Samples).Count + $auditHoldoutCount + @($auditParty4Auto.Samples).Count
Assert-Balance ($auditTotal -eq 2010) 'the complete audit must contain 2010 samples'
Write-Output "T1 balance audit passed: 2010 samples = 936 endgame + 540 profession + 504 holdout + 30 four-player Auto; complete scenario grids and current source fingerprints verified."
