using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Game.BalanceSimulator;

await BalanceSimulatorApplication.RunAsync(args);

namespace Game.BalanceSimulator
{
public static class BalanceSimulatorApplication
{
public static async Task<string> RunAsync(string[] args, bool writeReport = true)
{

// Run from the repository root. Uses only in-memory SQLite databases and production services.
// Equipment is supplied explicitly: this measures encounters, not acquisition time.
var arguments = SimulatorArguments.Parse(args);
string Option(string name, string fallback) => arguments.Value(name, fallback);
var dungeonCode = arguments.DungeonCode;
var depth = arguments.Depth;
var mastery = arguments.Mastery;
var startingPotions = arguments.StartingPotions;
var players = arguments.Players;
var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Option("--config", "Game.Server/appsettings.json"))).Build();
var boundSections = new Dictionary<(Type, string), object>();
var talentBuildPath = Option("--talent-builds", string.Empty);
var talentBuilds = talentBuildPath.Length == 0 ? new Dictionary<string, TalentBuildProfile>() :
    JsonSerializer.Deserialize<Dictionary<string, TalentBuildProfile>>(File.ReadAllText(talentBuildPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
foreach (var (name, profile) in talentBuilds)
{
    _ = StandardRole(profile.Role);
    if (profile.Nodes is { Count: > 0 })
        throw new ArgumentException($"Combat talent trees are no longer active; remove Nodes from build {name} and select native Skills instead.");
    if (string.IsNullOrWhiteSpace(name) ||
        profile.Skills.Count is < 1 or > 5 || profile.Skills.Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Skills.Count)
        throw new ArgumentException($"Invalid talent build: {name}");
}
var worldPath = Option("--world", "Game.Server/world.json");
var worldConfig = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(worldPath)).Build();
var partyScalingCatalog = new PartyScalingCatalog(Bind<PartyScalingOptions>(PartyScalingOptions.SectionName));
var worldCombat = new MonsterCombatCatalog(Bind<MonsterCombatOptions>(MonsterCombatOptions.SectionName));
var worldDepths = new DungeonDepthCatalog(Bind<DungeonDepthOptions>(DungeonDepthOptions.SectionName));
var worldEncounters = new DungeonEncounterCatalog(Bind<DungeonEncounterOptions>(DungeonEncounterOptions.SectionName), worldCombat, depthCatalog: worldDepths);
var world = new WorldCatalog(Options.Create(worldConfig.GetSection(WorldOptions.SectionName).Get<WorldOptions>()!), partyScalingCatalog, worldEncounters);
var runs = int.Parse(Option("--runs", "5"));
var seedStart = int.Parse(Option("--seed-start", "1"));
var includeTrace = args.Contains("--trace");
var sourceFiles = SourceHashes();
var elements = Option("--elements", "Fire,Water,Earth,Wind,Light,Dark").Split(',').Select(Enum.Parse<ElementType>).ToList();
if (dungeonCode is not null)
{
    var chosen = world.Dungeons.SingleOrDefault(dungeon => string.Equals(dungeon.Code, dungeonCode, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown dungeon code {dungeonCode}");
    var element = world.Regions.Single(region => region.Code == chosen.RegionCode).FeaturedElement;
    if (arguments.Contains("--elements") && (elements.Count != 1 || elements[0] != element))
        throw new ArgumentException("--elements must match the selected dungeon's region; use --weapon-element for a different weapon element.");
    elements = [element];
}
var weaponElementOption = Option("--weapon-element", string.Empty);
ElementType? weaponElement = weaponElementOption.Length == 0 ? null : Enum.Parse<ElementType>(weaponElementOption);
var roles = Option("--roles", string.Join(',', Bind<SkillOptions>(SkillOptions.SectionName).Value.Professions
        .Where(profession => !profession.IsPromotion).Select(profession => profession.Code)))
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var stages = Option("--stages", "starter,shop,field,week,graduate").Split(',');
var targets = dungeonCode is null ? Option("--targets", "normal,dungeon,elite,endgame").Split(',') : ["explicit"];
var compositions = Option("--composition", string.Empty)
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    .ToList();
if (compositions.Count > 0 && compositions.Any(members => members.Length != compositions[0].Length))
    throw new ArgumentException("Every --composition entry must have the same party size");
var partySize = compositions.Count > 0 ? compositions[0].Length : int.Parse(Option("--party", "1"));
var memberWeaponElements = arguments.Contains("--weapon-elements")
    ? Option("--weapon-elements", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Enum.Parse<ElementType>).ToArray()
    : Array.Empty<ElementType>();
if (arguments.Contains("--weapon-elements") && (arguments.Contains("--weapon-element") ||
    memberWeaponElements.Length != partySize || memberWeaponElements.Any(value => !Enum.IsDefined(value))))
    throw new ArgumentException("--weapon-elements requires one valid element per party member and cannot be combined with --weapon-element.");
var soulLoadouts = Option("--soul-loadouts", "native")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    .ToList();
var controlMode = Option("--mode", "auto").ToLowerInvariant();
if (runs is < 1 or > 100 || partySize is < 1 or > 5) throw new ArgumentOutOfRangeException("runs / party");
if (players > partySize) throw new ArgumentException("--players cannot exceed the scenario's party size");
if (seedStart < 1 || seedStart > int.MaxValue - runs) throw new ArgumentOutOfRangeException("seed-start");
if (compositions.Count == 0 && partySize > 1)
{
    if (args.Contains("--roles"))
        throw new ArgumentException("Use --composition for multi-character role comparisons");
    compositions.Add(Enumerable.Range(1, partySize).Select(BalancedPartyRole).ToArray());
}
if (controlMode is not ("auto" or "manual")) throw new ArgumentException("--mode must be auto or manual");
if (soulLoadouts.Count == 0 || soulLoadouts.Any(loadout => loadout.Length is not 1 && loadout.Length != partySize))
    throw new ArgumentException("Every --soul-loadouts entry must contain one value for the whole party or one value per party member");
foreach (var member in compositions.SelectMany(members => members)) _ = ResolveRole(member);
var scenarios = compositions.Count > 0
    ? compositions.Select(members => (Label: string.Join('+', members), Members: (IReadOnlyList<string>)members)).ToList()
    : roles.Select(role => (Label: role, Members: (IReadOnlyList<string>)Array.Empty<string>())).ToList();
var results = new List<Sample>();
foreach (var stage in stages)
foreach (var element in elements)
foreach (var scenario in scenarios)
foreach (var soulLoadout in soulLoadouts)
foreach (var target in targets)
{
    if (dungeonCode is null && (stage is "starter" or "shop") && target != "normal") continue;
    for (var seed = seedStart; seed < seedStart + runs; seed++) results.Add(await Simulate(stage, element, scenario.Label, target,
        partySize, seed, controlMode, scenario.Members, soulLoadout));
    var group = results.TakeLast(runs).ToList();
    Console.WriteLine($"{stage}/{element}/{scenario.Label}/{string.Join('+', soulLoadout)}/{target}/party{partySize}/players{players}: {group.Count(x => x.Victory)}/{runs}, {group.Average(x => x.Rounds):0.0} rounds, {group.Average(x => x.CycleSeconds)/60:0.0} min, {group.Average(x => x.PotionsUsed):0.0} potions");
}
var report = new
{
    GeneratedAtUtc = DateTime.UtcNow,
    SourceFiles = sourceFiles,
    SourcesUnchangedDuringRun = sourceFiles.SequenceEqual(SourceHashes()),
    Assumptions = new { RunsPerScenario = runs, SeedStart = seedStart, MaxRounds = 250, PartySize = partySize, Players = players, ControlMode = controlMode,
        DungeonCode = dungeonCode, Depth = depth, Mastery = mastery, StartingPotionsPerActor = startingPotions,
        DirectDamageVariancePercent = Bind<CombatDamageOptions>(CombatDamageOptions.SectionName).Value.VariancePercent,
        DirectDamageRounding = "Uniform symmetric multiplier after direct-damage formulas/amplification; fractional result rounds upward with probability equal to its fractional part, otherwise downward. Each actual target/segment rolls independently; derived echo damage does not reroll. Healing and damage-over-time do not vary.",
        WorldFile = worldPath, MetricsSchemaVersion = 2,
        BossSkillUsesKey = "SkillCode", BossSkillUsesBasis = "Distinct monster/run/round/skill with at least one committed Skill event; exclude status consumption/removal/expiry, intent and attempted casts",
        RewardBasis = "Gold/MeanGold remain whole-party totals; Rewards splits settled earnings by character/account/team. Ordinary items are Base-source Consumable/Material/Weapon/SoulImprint quantities from regular monster:/clear events; exclude mastery, challenge, seeds, tutorial and first-clear grants. Quantities are item counts, not economic value. Pending ledger entries are not earnings.",
        WeaponElement = weaponElement?.ToString() ?? "SameAsDungeonRegion",
        MemberWeaponElements = memberWeaponElements.Length == 0 ? null : memberWeaponElements,
        TalentBuildsFile = talentBuildPath.Length == 0 ? null : talentBuildPath,
        TraceSchemaVersion = BattleRoundTrace.SchemaVersion, IncludesRoundTrace = includeTrace,
        Compositions = compositions.Count > 0 ? compositions : null,
        SoulLoadouts = soulLoadouts,
        Description = "Production combat and reward services; deterministic seeds; preset equipment and level-unlocked native skills; inactive combat talent trees excluded; region weapon element unless overridden; Auto access and account admission pre-unlocked independently of starting character mastery; full health and explicitly reported starting potions; excludes acquisition time. Raid/native presets measure farming, not first-clear access. Auto uses production conditions. Manual queues equipped skills before each round, reserving Guard for Deadly intents; fixed policy, not optimal human play. Successful cycle time includes the next run's first-round interval; failures do not restart. Boss skill counts use committed effect facts, not localized logs. Failure categories describe observed terminal state, not inferred causes." },
    Summary = results.GroupBy(x => new { x.Stage, x.Element, x.Profession, x.SoulLoadout, x.Dungeon, x.PartySize, x.Players, x.Depth, x.Mastery, x.StartingPotions, x.DirectDamageVariancePercent }).Select(group => new
    {
        group.Key, Runs = group.Count(), Victories = group.Count(x => x.Victory),
        MeanRounds = group.Average(x => x.Rounds), MeanCycleSeconds = group.Average(x => x.CycleSeconds),
        MeanPotionsUsed = group.Average(x => x.PotionsUsed), MeanPotionDrops = group.Average(x => x.PotionDrops),
        MeanGold = group.Average(x => x.Gold), MeanRemainingHp = group.Average(x => x.RemainingHp),
        MeanGoldPerCharacter = group.Average(x => (double)x.Gold / x.PartySize),
        MeanOrdinaryItemQuantity = group.Average(x => x.Rewards.Team.OrdinaryItemQuantity),
        MeanOrdinaryItemQuantityPerCharacter = group.Average(x => (double)x.Rewards.Team.OrdinaryItemQuantity / x.PartySize),
        Accounts = group.SelectMany(x => x.Rewards.Accounts).GroupBy(x => x.UserId).OrderBy(x => x.Key).Select(account => new
        {
            UserId = account.Key, CharacterIds = account.First().CharacterIds,
            MeanGold = account.Average(x => x.Earned.Gold), MeanOrdinaryItemQuantity = account.Average(x => x.Earned.OrdinaryItemQuantity)
        }),
        MaxRounds = group.Max(x => x.Rounds),
        P95Rounds = group.Select(x => x.Rounds).Order().ElementAt((int)Math.Ceiling(group.Count() * .95) - 1),
        SoftEnrageSamples = group.Count(x => x.SoftEnrageCasts > 0),
        HardEnrageSamples = group.Count(x => x.HardEnrageCasts > 0),
        CasualtySamples = group.Count(x => x.Survivors < x.PartySize),
        FailureCategories = group.Where(x => !x.Victory).GroupBy(x => x.FailureCategory).ToDictionary(x => x.Key, x => x.Count()),
        Attack = group.First().Attack, MaxHp = group.First().MaxHp
    }),
    Samples = results
};
var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
if (writeReport)
{
    var output = Path.GetFullPath(Option("--output", "docs/t1-combat-results.json"));
    await File.WriteAllTextAsync(output, reportJson);
    Console.WriteLine($"Saved {results.Count} samples to {output}");
}
return reportJson;

Dictionary<string, string> SourceHashes()
{
    var paths = new[] { "Game.Server/Services", "Game.Server/Configuration", "Game.Shared" }
        .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        .Where(path => !path.Replace('\\', '/').Split('/').Any(part => part is "bin" or "obj"))
        .Concat([Option("--config", "Game.Server/appsettings.json"), worldPath])
        .Concat(Directory.EnumerateFiles("tools/Game.BalanceSimulator", "*.cs", SearchOption.TopDirectoryOnly))
        .Concat(talentBuildPath.Length == 0 ? [] : new[] { talentBuildPath });
    return paths.Select(path => path.Replace('\\', '/')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n")))));
}

IOptions<T> Bind<T>(string section) where T : class, new()
{
    // Each run shares immutable configuration, while characters, RNGs and databases remain isolated.
    var key = (typeof(T), section);
    if (!boundSections.TryGetValue(key, out var value))
        boundSections[key] = value = Options.Create(config.GetSection(section).Get<T>()!);
    return (IOptions<T>)value;
}

async Task<Sample> Simulate(string stage, ElementType element, string profession, string target, int party, int seed,
    string mode, IReadOnlyList<string> explicitComposition, IReadOnlyList<string> soulLoadout)
{
    var random = new Random(seed);
    var weapons = new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName));
    var combatCatalog = new MonsterCombatCatalog(Bind<MonsterCombatOptions>(MonsterCombatOptions.SectionName));
    var skills = new SkillCatalog(Bind<SkillOptions>(SkillOptions.SectionName), combatCatalog);
    var consumables = new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName));
    var materials = new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName));
    var soulImprints = new SoulImprintCatalog(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName));
    var rewards = new RewardCatalog(Bind<RewardOptions>(RewardOptions.SectionName), consumables, weapons,
        materials, soulImprints, random);
    ProfessionMechanicCatalog.Default.Validate(skills, combatCatalog.Statuses);
    var depthCatalog = new DungeonDepthCatalog(Bind<DungeonDepthOptions>(DungeonDepthOptions.SectionName));
    var encounters = new DungeonEncounterCatalog(Bind<DungeonEncounterOptions>(DungeonEncounterOptions.SectionName), combatCatalog, rewards, depthCatalog);
    var region = world.Regions.Single(region => region.FeaturedElement == element);
    var definition = dungeonCode is not null ? world.Dungeons.Single(d => string.Equals(d.Code, dungeonCode, StringComparison.OrdinalIgnoreCase)) : target switch
    {
        "normal" => world.Dungeons.Single(d => d.RegionCode == region.Code && d.DungeonKind == "Hunt" && d.MinimumLevel == (stage == "starter" ? 1 : stage == "shop" ? 4 : 7)),
        "dungeon" => world.Dungeons.Single(d => d.Code == region.FeaturedDungeonCode),
        "elite" => world.Dungeons.Single(d => d.RegionCode == region.Code && d.DungeonKind == "Elite" && d.MinimumLevel == 10),
        "endgame" => world.Dungeons.Single(d => d.RegionCode == region.Code && d.DungeonKind == "Dungeon" && d.MinimumLevel == 10),
        "depths" => world.Dungeons.Single(d => d.Code == "kobold-mine-depths"),
        _ => throw new ArgumentException($"Unknown target {target}")
    };
    if (!depthCatalog.ValidateDepth(definition.Code, depth)) throw new ArgumentException($"Depth {depth} is not configured for {definition.Code}");
    if (mastery > 0 && depthCatalog.Find(definition.Code) is null) throw new ArgumentException($"Mastery requires a depth-enabled dungeon: {definition.Code}");
    await using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();
    await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
    await db.Database.EnsureCreatedAsync();
    var dungeon = JsonSerializer.Deserialize<Dungeon>(JsonSerializer.Serialize(definition))!;
    var accounts = Enumerable.Range(1, players).Select(id => new User
    {
        Id = id, UserName = players == 1 ? "simulation" : $"simulation-{id}", PasswordHash = "unused", ActiveCharacterId = id
    }).ToList();
    db.Users.AddRange(accounts);
    db.Dungeons.Add(dungeon);
    await db.SaveChangesAsync();
    var room = new Room { DungeonId = dungeon.Id, OwnerUserId = 1, IsOwnerAutoEnabled = true, SlotCount = 5, Status = RoomStatus.NotStarted, IsPreparationTimeoutEnabled = false, TotalWaveCount = encounters.GetWaveCount(dungeon), DepthLevel = depth,
        DepthDefinitionJson = depthCatalog.Find(dungeon.Code) is { } depthDefinition ? JsonSerializer.Serialize(depthDefinition) : null };
    db.Rooms.Add(room);
    await db.SaveChangesAsync();
    var monsters = encounters.CreateMonsters(dungeon, depth).ToList();
    var initialMonsters = monsters.Select(monster => new MonsterBuild(monster.Name, monster.WaveNumber, monster.Position,
        monster.MaxHp, monster.Attack, monster.Defense, monster.CombatProfileCode)).ToList();
    foreach (var monster in monsters) monster.RoomId = room.Id;
    db.Monsters.AddRange(monsters);
    await db.SaveChangesAsync();
    room.MonsterId = monsters[0].Id;
    db.UserDungeonClears.AddRange(accounts.Select(account => new UserDungeonClear
        { UserId = account.Id, DungeonId = dungeon.Id, HighestDepth = depth, ClearedAtUtc = DateTime.UtcNow }));
    var actors = new List<Character>();
    var actorBuilds = new List<ActorBuild>();
    for (var index = 1; index <= party; index++)
    {
        var role = explicitComposition.Count > 0 ? explicitComposition[index - 1] :
            party == 1 ? profession : BalancedPartyRole(index);
        var build = ResolveRole(role);
        var baseProfession = skills.FindProfession(build.BaseProfession)?.Code ??
            throw new ArgumentException($"Role {role} has no configured native profession.");
        var userId = (index - 1) % players + 1;
        var character = new Character { Id = index, UserId = userId, Name = $"{role}-{index}", ProfessionCode = baseProfession,
            Level = stage == "entry" ? 8 : stage == "starter" ? 1 : stage == "shop" ? 5 : 10 };
        var memberWeaponElement = memberWeaponElements.Length == 0 ? weaponElement ?? element : memberWeaponElements[index - 1];
        var loadout = Loadout(weapons, stage, memberWeaponElement, index, baseProfession);
        character.Attack = loadout.Sum(item => item.Attack);
        character.MaxHp = loadout.Sum(item => item.MaxHp);
        var talentCodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        weapons.ApplyBonuses(character, loadout);
        character.Hp = TalentRules.EffectiveMaxHp(character);
        actors.Add(character);
        db.Characters.Add(character);
        if (depthCatalog.Find(dungeon.Code) is not null)
        {
            if (mastery > 0)
                db.CharacterDungeonProgress.Add(new CharacterDungeonProgress { CharacterId = index, DungeonId = dungeon.Id, HighestDepth = mastery });
            // Admission is pre-unlocked independently; freeze the requested mastery for this sample's run.
            db.DungeonRunParticipants.Add(new DungeonRunParticipant
            {
                RoomId = room.Id, RunSequence = room.RunSequence, CharacterId = index, MasteryLevel = mastery
            });
        }
        db.CharacterBattleMilestones.Add(new CharacterBattleMilestone
        {
            CharacterId = index,
            Kind = BattleMilestoneService.DungeonClearKind,
            TargetCode = dungeon.Code,
            Count = 1,
            FirstAtUtc = DateTime.UtcNow,
            LastAtUtc = DateTime.UtcNow
        });
        db.CharacterWeapons.AddRange(loadout);
        db.RoomSlots.Add(new RoomSlot { RoomId = room.Id, SlotIndex = index, UserId = userId, CharacterId = index, IsAutoEnabled = true });
        if (startingPotions > 0) db.CharacterItemStacks.Add(new CharacterItemStack { CharacterId = index, ItemCode = "minor-healing-potion", Quantity = startingPotions });
        db.CharacterConsumableSlots.Add(new CharacterConsumableSlot { CharacterId = index, SlotIndex = 1, ItemCode = "minor-healing-potion", AutoUseEnabled = true, AutoHpThresholdPercent = stage == "entry" ? 50 : 70 });
        foreach (var (code, rank) in talentCodes) db.CharacterSkillTalents.Add(new CharacterSkillTalent { CharacterId = index, NodeCode = code, PointsSpent = rank });
        var learned = skills.LearnedSkills(character, talentCodes).Select(skill => skill.Code).ToHashSet();
        var preferred = build.Profile?.Skills.ToArray() ?? skills.NativeSkills(character)
            .OrderBy(skill => skill.UnlockLevel).ThenBy(skill => skill.Code, StringComparer.Ordinal).Select(skill => skill.Code).ToArray();
        if (build.Profile is not null && preferred.Any(code => !learned.Contains(code)))
            throw new InvalidOperationException($"Talent build {role} equips an unlearned skill");
        var selected = preferred.Where(learned.Contains).Take(5).ToList();
        if (selected.Count == 0 || selected.Any(code => skills.Resolve(character, code) is null))
            throw new InvalidOperationException($"Role {role} has no valid equipped native skills at level {character.Level}.");
        actorBuilds.Add(new ActorBuild(index, baseProfession, character.Level, memberWeaponElement, selected, talentCodes,
            loadout.Select(weapon => new WeaponBuild(weapon.EquippedSlotIndex!.Value, weapon.WeaponCode,
                weapon.TemplateRevision, weapon.Element, weapon.ItemLevel, weapon.QualityRank, weapon.Attack, weapon.MaxHp,
                weapon.Skills.OrderBy(skill => skill.SlotIndex).Select(skill =>
                    new WeaponSkillBuild(skill.SlotIndex, skill.SkillCode, skill.Level, skill.EnhancementLevel)).ToList())).ToList(),
            new EquipmentBuildStats(character.Attack, character.MaxHp, TalentRules.EffectiveMaxHp(character),
                weapons.CalculateBonuses(loadout).Effects.ToDictionary(effect => effect.EffectType.ToString(), effect => effect.TotalPercent))));
        for (var slot = 0; slot < selected.Count; slot++) db.CharacterSkillSlots.Add(new CharacterSkillSlot
        { CharacterId = index, SlotIndex = slot + 1, SkillCode = selected[slot], AutoUseEnabled = true, AutoHpThresholdPercent = 75 });
        if (stage == "raid")
        {
            var selection = soulLoadout.Count == 1 ? soulLoadout[0] : soulLoadout[index - 1];
            if (!selection.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                var code = selection.Equals("native", StringComparison.OrdinalIgnoreCase)
                    ? soulImprints.Items.Single(item => item.DungeonCode == world.Dungeons.Single(d =>
                        d.RegionCode == region.Code && d.DungeonKind == "Dungeon" && d.MinimumLevel == 10).Code).Code
                    : soulImprints.Find(selection)?.Code ?? throw new ArgumentException($"Unknown soul imprint {selection}");
                var imprint = soulImprints.Materialize(code, index);
                imprint.EquippedSlotIndex = SoulImprintRules.SlotIndex;
                imprint.AutoUseEnabled = true;
                db.CharacterSoulImprints.Add(imprint);
            }
        }
    }
    await db.SaveChangesAsync();
    var progression = new ProgressionService(Bind<ProgressionOptions>(ProgressionOptions.SectionName));
    var users = new UserService(db, progression, skills, weapons);
    var runRules = new DungeonRunRulesService(db, combatCatalog, rewards, partyScalingCatalog, depthCatalog,
        encounters: encounters, damageOptions: Bind<CombatDamageOptions>(CombatDamageOptions.SectionName));
    var ruleDefinition = await runRules.EnsureAsync(room);
    await db.SaveChangesAsync();
    var depthProgress = new DungeonDepthProgressService(db, depthCatalog, runRules);
    var events = new BattleEventCollector();
    var statuses = new BattleStatusService(db, combatCatalog.Statuses, events, runRules: runRules);
    var phases = new MonsterPhaseService(db, combatCatalog, statuses, runRules);
    var guards = new BattleGuardService(statuses);
    var damage = new BattleDamageService(statuses, guards, random, events, runRules: runRules, phases: phases);
    var effects = new BattleEffectExecutor(skills, statuses, guards, damage);
    var rewardService = new RewardService(db, rewards, progression, depthProgress: depthProgress, runRules: runRules);
    var monsterCombat = new MonsterCombatService(db, combatCatalog, random, statuses, skills, effects, runRules: runRules, phases: phases);
    var runService = new DungeonRunService(db, rewardService, monsterCombat, depthProgress: depthProgress, runRules: runRules);
    var battle = new BattleService(db, users, consumables, skills, rewardService, runService, monsterCombat,
        random: random, weaponCatalog: weapons, soulImprintCatalog: soulImprints,
        partyScalingService: new PartyScalingService(db, partyScalingCatalog, runRules), battleEffects: effects, runRules: runRules);
    var seconds = 0d;
    var logs = new List<string>();
    var traces = new List<BattleRoundTrace>();
    var phaseRounds = new List<MonsterPhaseObservation>();
    var metrics = new SimulatorMetrics(monsters.Where(monster => monster.IsBoss).Select(monster => monster.Id));
    var roundCounts = monsters.Select(monster => monster.Name).Distinct().ToDictionary(name => name, _ => 0);
    bool victory = false;
    for (var step = 0; step < 300 && room.RoundNumber < 250; step++)
    {
        var previousRounds = room.RoundNumber;
        var active = monsters.Single(monster => monster.Id == room.MonsterId);
        if (mode == "manual")
        {
            var intent = await monsterCombat.EnsureIntentAsync(room, active);
            var incoming = combatCatalog.FindSkill(intent.SkillCode);
            var preemptiveGuard = incoming?.DangerLevel == "Deadly";
            foreach (var slot in await db.RoomSlots.Where(slot => slot.RoomId == room.Id && slot.CharacterId != null)
                         .ToListAsync())
            {
                var character = actors.Single(actor => actor.Id == slot.CharacterId);
                if (character.Hp <= 0) continue;
                slot.PendingSkillSlotMask = 0;
                foreach (var equipped in await db.CharacterSkillSlots.Where(equipped =>
                             equipped.CharacterId == character.Id && equipped.SkillCode != null).ToListAsync())
                {
                    var skill = skills.FindSkill(equipped.SkillCode);
                    var isGuard = skill is not null && SkillCatalog.EffectsFor(skill).Any(effect => effect.Type == "Guard");
                    if (!isGuard || preemptiveGuard)
                        slot.PendingSkillSlotMask |= SkillRules.SlotMask(equipped.SlotIndex);
                }
                slot.IsSoulImprintQueued = true;
            }
        }
        var (result, error) = await battle.SyncRoomAsync(room.Id);
        if (error is not null || result is null) throw new InvalidOperationException(error ?? "No result");
        roundCounts[active.Name] += room.RoundNumber - previousRounds;
        logs.AddRange(result.Logs);
        if (room.RoundNumber != previousRounds)
        {
            foreach (var state in await db.BattleMonsterPhaseStates.Where(state => state.RoomId == room.Id && state.RunSequence == room.RunSequence).ToListAsync())
            {
                var earthArmor = combatCatalog.FindProfile(monsters.Single(m => m.Id == state.MonsterId).CombatProfileCode)?.EarthArmor;
                phaseRounds.Add(new(state.MonsterId, previousRounds - state.EncounterStartRound + 1,
                    state.IsActive, earthArmor is null ? state.WaterDamage : 0, state.ActivationCount, state.BreakCount, state.ExpiryCount,
                    state.LinkedHitCount, state.LastActivationRound, state.LastBreakRound, state.LastExpiryRound,
                    earthArmor is null ? null : state.ElementDamage, earthArmor is null ? null : ElementType.Wind));
            }
            metrics.Observe(result.Events);
            var participants = (await db.RoomSlots.Where(slot => slot.RoomId == room.Id && slot.CharacterId != null).ToListAsync())
                .Select(slot => new BattleParticipant(slot, actors.Single(actor => actor.Id == slot.CharacterId))).ToList();
            traces.Add(BattleRoundTrace.Capture(room, result, participants, monsters,
                await db.BattleStatusEffects.Where(state => state.RoomId == room.Id).ToListAsync(),
                await db.BattleSkillCooldowns.Where(state => state.RoomId == room.Id).ToListAsync(),
                await db.BattleMonsterSkillCooldowns.Where(state => state.RoomId == room.Id).ToListAsync(),
                await db.BattleConsumableCooldowns.Where(state => state.RoomId == room.Id).ToListAsync(),
                await db.BattleHealingPotionStates.Where(state => state.RoomId == room.Id).ToListAsync(),
                await db.BattleOperationPotionStates.Where(state => state.RoomId == room.Id).ToListAsync(),
                await db.BattleConsumableBuffs.Where(state => state.RoomId == room.Id).ToListAsync()));
        }
        if (room.Status == RoomStatus.BattleOver)
        {
            victory = result.IsVictory;
            if (victory) seconds += mode == "auto" ? BattleRules.AutoRoundCooldownSeconds : BattleRules.RoundCooldownSeconds;
            break;
        }
        if (room.NextRoundAvailableAtUtc is DateTime next)
        {
            seconds += Math.Max(0, (next - result.ServerTimeUtc).TotalSeconds);
            room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        else if (room.RoundNumber == previousRounds) throw new InvalidOperationException($"Stalled in {room.Status}");
    }
    var potionDrops = await db.RewardEntries.Where(entry => entry.Kind == "Consumable" && entry.Code == "minor-healing-potion").SumAsync(entry => entry.Quantity);
    var stock = await db.CharacterItemStacks.Where(stack => stack.ItemCode == "minor-healing-potion").SumAsync(stack => stack.Quantity);
    var finalMonster = monsters.Single(monster => monster.Id == room.MonsterId);
    var bossSkillUses = metrics.BossSkillUses;
    var failureCategory = SimulatorMetrics.FailureCategory(victory, room.Status, actors.Count(actor => actor.Hp > 0), room.RoundNumber, 250);
    var potionUses = await db.BattleHealingPotionStates.Where(state => state.RoomId == room.Id && state.RunSequence == room.RunSequence).SumAsync(state => state.UsesUsed);
    var rewardEntries = await db.RewardEntries.Where(entry => entry.RoomId == room.Id && entry.Sequence == room.RunSequence).ToListAsync();
    var settlementStatus = (await db.RewardRuns.FindAsync(room.Id, room.RunSequence))?.Status ?? "NoRewards";
    var earnedRewards = SimulatorRewardSummary.Build(actors, rewardEntries, settlementStatus);
    var rewardEvents = (await db.RewardEvents.Where(entry => entry.RoomId == room.Id && entry.Sequence == room.RunSequence).ToListAsync())
        .OrderBy(entry => entry.EventKey, StringComparer.Ordinal).Select(entry => new SimulatorCoopRewardEvent(
            entry.EventKey, entry.CoopParticipantCount, entry.CoopDropBonusPercent)).ToList();
    return new Sample(stage, element, profession, string.Join('+', soulLoadout), dungeon.Code, party, mode, seed, victory, room.RoundNumber, seconds,
        potionUses, potionDrops, actors.Sum(actor => actor.Gold), actors.Sum(actor => actor.Hp), actors[0].Attack,
        TalentRules.EffectiveMaxHp(actors[0]), finalMonster.Hp, finalMonster.MaxHp, roundCounts, bossSkillUses,
        victory ? [] : logs.TakeLast(8).ToList(), actorBuilds, actors.Count(actor => actor.Hp > 0),
        bossSkillUses.GetValueOrDefault("endgame-soft-enrage"),
        bossSkillUses.GetValueOrDefault("endgame-hard-enrage"),
        BattleRoundTrace.EncounterFingerprint(traces), includeTrace ? traces : null,
        depth, mastery, startingPotions, failureCategory, party - actors.Count(actor => actor.Hp > 0), stock,
        metrics.CharacterDeaths, initialMonsters, ruleDefinition.Revision, players, earnedRewards, rewardEvents,
        ruleDefinition.DirectDamageVariancePercent, phaseRounds);
}

List<CharacterWeapon> Loadout(WeaponCatalog catalog, string stage, ElementType element, int characterId, string profession)
{
    if (stage is "deep-entry" or "deep-two" or "deep-three" or "deep-four")
    {
        var regionCode = world.Regions.Single(region => region.FeaturedElement == element).Code;
        var kills = Bind<RewardOptions>(RewardOptions.SectionName).Value.MonsterKills;
        var huntWeapons = new[] { 1, 3, 5 }.Select(level =>
        {
            var hunt = world.Dungeons.Single(dungeon => dungeon.IsVisible && dungeon.RegionCode == regionCode &&
                dungeon.DungeonKind == "Hunt" && dungeon.MinimumLevel == level);
            return kills[hunt.Code].Drops.Where(drop => drop.Kind == "Weapon" && catalog.FindItem(drop.Code)?.Element == element)
                .OrderByDescending(drop => drop.ChancePercent).ThenBy(drop => drop.Code, StringComparer.Ordinal).First().Code;
        }).ToArray();
        List<string> benchmarkCodes;
        if (stage is "deep-three" or "deep-four")
        {
            // LV3 uses one of each deep template; LV4 uses two of each.
            var deepWeapons = Bind<WeaponOptions>(WeaponOptions.SectionName).Value.Items
                .Where(item => item.Element == element && item.Code.StartsWith("t1-deep-", StringComparison.Ordinal))
                .OrderBy(item => item.Code, StringComparer.Ordinal).ToList();
            if (deepWeapons.Count != 2 || deepWeapons.Any(item => item.Skills.Count != 3))
                throw new InvalidOperationException($"{stage} requires the two three-skill deep templates for {element}.");
            benchmarkCodes = stage == "deep-four"
                ? deepWeapons.SelectMany(item => new[] { item.Code, item.Code })
                    .Concat(huntWeapons.SelectMany(code => new[] { code, code })).ToList()
                : deepWeapons.Select(item => item.Code)
                    .Concat(new[] { 0, 1, 2, 0, 1, 2, 1, 2 }.Select(index => huntWeapons[index])).ToList();
        }
        else if (stage == "deep-two")
        {
            // Tanks/healers select the regional survival template; DPS select the output template.
            var defensive = profession is "swordsman" or "acolyte";
            var deepCode = (element, defensive) switch
            {
                (ElementType.Fire, false) => "t1-deep-embermark-axe",
                (ElementType.Fire, true) => "t1-deep-molten-armor-maul",
                (ElementType.Water, false) => "t1-deep-mirror-tide-blade",
                (ElementType.Water, true) => "t1-deep-frostspring-ward-staff",
                (ElementType.Earth, false) => "t1-deep-riftstone-spear",
                (ElementType.Earth, true) => "t1-deep-stone-oath-maul",
                (ElementType.Wind, false) => "t1-deep-swiftfeather-bow",
                (ElementType.Wind, true) => "t1-deep-gale-mark-ward-staff",
                (ElementType.Light, false) => "t1-deep-radiant-prism-blade",
                (ElementType.Light, true) => "t1-deep-dawn-ward-staff",
                (ElementType.Dark, false) => "t1-deep-night-mark-twinblades",
                (ElementType.Dark, true) => "t1-deep-nether-oath-ward-staff",
                _ => throw new ArgumentOutOfRangeException(nameof(element))
            };
            if (catalog.FindItem(deepCode)?.Skills.Count != 3)
                throw new InvalidOperationException($"deep-two requires the three-skill template {deepCode}.");
            benchmarkCodes = new[] { deepCode }.Concat(Enumerable.Range(0, 9).Select(index => huntWeapons[index % 3])).ToList();
        }
        else
        {
            // Four Lv1, three Lv3, three Lv5 drops; five complete Lv10 weapons and five complete Lv7 weapons.
            benchmarkCodes = new[] { 0, 1, 2, 0, 1, 0, 1, 2, 0, 2 }.Select(index => huntWeapons[index]).ToList();
        }
        return benchmarkCodes.Select((code, slot) =>
        {
            var weapon = catalog.CreateRewardSnapshot(code).ToCharacterWeapon(characterId);
            weapon.EquippedSlotIndex = slot + 1;
            weapon.QualityRank = WeaponRules.MaxQualityBonusLevels;
            catalog.UnlockSkillsForQuality(weapon);
            var level = stage is "deep-three" or "deep-four" || slot < (stage == "deep-two" ? 6 : 5) ? 10 : 7;
            foreach (var skill in weapon.Skills)
            {
                if (skill.BaseLevel > level || level > WeaponRules.MaximumSkillLevel(weapon.QualityRank))
                    throw new InvalidOperationException($"Invalid {stage} skill level for {weapon.WeaponCode}.");
                skill.QualityBonusLevel = 0;
                skill.EnhancementLevel = level - skill.BaseLevel;
                skill.Level = level;
                skill.SpentFragments = Enumerable.Range(0, skill.EnhancementLevel).Sum(catalog.EnhancementCost);
            }
            return weapon;
        }).ToList();
    }
    if (stage == "entry")
    {
        // The first two regional hunts supply the reference entry equipment, in progression order.
        var regionCode = world.Regions.Single(region => region.FeaturedElement == element).Code;
        var kills = Bind<RewardOptions>(RewardOptions.SectionName).Value.MonsterKills;
        var huntWeapons = world.Dungeons.Where(dungeon => dungeon.IsVisible && dungeon.RegionCode == regionCode &&
                dungeon.DungeonKind == "Hunt").OrderBy(dungeon => dungeon.SortOrder).Take(2)
            .Select(dungeon => kills[dungeon.Code].Drops.First(drop => drop.Kind == "Weapon").Code).ToArray();
        if (huntWeapons.Length != 2) throw new InvalidOperationException($"Missing entry hunt equipment for {element}.");
        var loadout = new List<CharacterWeapon>();
        for (var slot = 0; slot < 10; slot++)
        {
            var weapon = catalog.CreateRewardSnapshot(slot < 2 ? huntWeapons[slot] :
                $"t1-shop-{element.ToString().ToLowerInvariant()}").ToCharacterWeapon(characterId);
            weapon.EquippedSlotIndex = slot + 1;
            if (slot == 0)
            {
                weapon.QualityRank = 3;
                catalog.UnlockSkillsForQuality(weapon);
            }
            var enhancement = slot == 0 ? 9 : slot == 1 ? 3 : 0;
            foreach (var skill in weapon.Skills)
            {
                skill.QualityBonusLevel = 0;
                skill.EnhancementLevel = enhancement;
                skill.Level = skill.BaseLevel + enhancement;
                skill.SpentFragments = Enumerable.Range(0, enhancement).Sum(catalog.EnhancementCost);
            }
            loadout.Add(weapon);
        }
        return loadout;
    }
    var all = Bind<WeaponOptions>(WeaponOptions.SectionName).Value.Items;
    var field = all.Where(item => item.Element == element && item.Code.StartsWith("t1-") && !item.Code.StartsWith("t1-shop-"))
        .OrderByDescending(item => item.Attack).ThenBy(item => item.Code, StringComparer.Ordinal).ToList();
    var bossCode = world.Regions.Single(region => region.FeaturedElement == element).FeaturedWeaponCode;
    var exchange = Bind<DungeonExchangeOptions>(DungeonExchangeOptions.SectionName).Value.Offers;
    var mine = exchange.Where(offer => offer.DungeonCode == "kobold-mine-depths" && offer.RewardKind == "Weapon" &&
        catalog.FindItem(offer.EffectiveRewardCode)!.Element == element)
        .OrderByDescending(offer => catalog.FindItem(offer.EffectiveRewardCode)!.Attack)
        .ThenBy(offer => offer.EffectiveRewardCode, StringComparer.Ordinal).First().EffectiveRewardCode;
    var endgameCode = world.Dungeons.Single(dungeon => dungeon.RegionCode ==
        world.Regions.Single(region => region.FeaturedElement == element).Code &&
        dungeon.DungeonKind == "Dungeon" && dungeon.MinimumLevel == 10).Code;
    var final = exchange.Where(offer => offer.DungeonCode == endgameCode && offer.RewardKind == "Weapon" &&
        catalog.FindItem(offer.EffectiveRewardCode)!.Element == element)
        .OrderByDescending(offer => catalog.FindItem(offer.EffectiveRewardCode)!.Attack)
        .ThenBy(offer => offer.EffectiveRewardCode, StringComparer.Ordinal).First().EffectiveRewardCode;
    var eliteNames = world.Dungeons.Where(d => d.DungeonKind == "Elite" && d.RegionCode == world.Regions.Single(r => r.FeaturedElement == element).Code).Select(d => d.Code).ToList();
    var rewards = Bind<RewardOptions>(RewardOptions.SectionName).Value;
    var elite = eliteNames.Select(code => rewards.MonsterKills[code].Drops
        .Where(drop => drop.Kind == "Weapon" && catalog.FindItem(drop.Code)?.Element == element)
        .OrderByDescending(drop => catalog.FindItem(drop.Code)!.Attack).ThenBy(drop => drop.Code, StringComparer.Ordinal)
        .First().Code).ToList();
    var shop = $"t1-shop-{element.ToString().ToLowerInvariant()}";
    List<string> codes = stage switch
    {
        "starter" => [shop, shop, shop],
        "shop" => Enumerable.Repeat(shop, 10).ToList(),
        "field" => [field[0].Code, field[0].Code, field[0].Code, field[0].Code, field[1].Code, field[1].Code, field[1].Code, field[2].Code, field[2].Code, field[3].Code],
        "week" => [field[0].Code, field[0].Code, field[0].Code, field[1].Code, field[1].Code, field[2].Code, field[2].Code, field[3].Code, bossCode, mine],
        "raid" => [field[0].Code, field[0].Code, field[1].Code, field[2].Code, field[3].Code, bossCode, bossCode, mine, elite[0], elite[0]],
        "graduate" => [field[0].Code, field[0].Code, field[1].Code, field[2].Code, field[3].Code, bossCode, bossCode, mine, elite[0], final],
        _ => throw new ArgumentException(stage)
    };
    var result = codes.Select((code, index) =>
    {
        var weapon = catalog.CreateRewardSnapshot(code).ToCharacterWeapon(characterId);
        weapon.EquippedSlotIndex = index + 1;
        weapon.QualityRank = stage is "week" or "raid" or "graduate" ? 1 : 0;
        foreach (var skill in weapon.Skills)
        {
            skill.QualityBonusLevel = 0;
            skill.EnhancementLevel = stage == "graduate" ? skill.SlotIndex == 1 ? 4 : 3 :
                stage == "raid" ? skill.SlotIndex == 1 ? 3 : 2 :
                stage == "week" ? skill.SlotIndex == 1 ? 3 : 1 : stage == "field" ? 1 : 0;
            skill.Level = skill.BaseLevel + skill.EnhancementLevel;
            skill.SpentFragments = Enumerable.Range(0, skill.EnhancementLevel).Sum(catalog.EnhancementCost);
        }
        return weapon;
    }).ToList();
    return result;
}

static string BalancedPartyRole(int slotIndex) => slotIndex switch
{
    1 => "knight",
    2 => "priest",
    3 => "elementalist",
    4 => "marksman",
    5 => "trickster",
    _ => throw new ArgumentOutOfRangeException(nameof(slotIndex))
};

RoleBuild ResolveRole(string role) => talentBuilds.TryGetValue(role, out var profile)
    ? StandardRole(profile.Role) with { Profile = profile } : StandardRole(role);

static RoleBuild StandardRole(string role) => role.ToLowerInvariant() switch
{
    "swordsman" or "knight" or "warrior" or "swordsman-guard" or "swordsman-assault" => new("swordsman"),
    "acolyte" or "cleric" or "priest" or "inquisitor" or "acolyte-mercy" or "acolyte-judgment" => new("acolyte"),
    "mage" or "elementalist" or "arcanist" => new("mage"),
    "hunter" or "marksman" or "beastmaster" => new("hunter"),
    "rogue" or "assassin" or "trickster" => new("rogue"),
    _ => throw new ArgumentException($"Unknown role {role}")
};

}
}

record Sample(string Stage, ElementType Element, string Profession, string SoulLoadout, string Dungeon, int PartySize, string ControlMode, int Seed,
    bool Victory, int Rounds, double CycleSeconds, int PotionsUsed, int PotionDrops, int Gold, int RemainingHp,
    int Attack, int MaxHp, int MonsterHp, int MonsterMaxHp, Dictionary<string, int> EnemyRounds,
    Dictionary<string, int> BossSkillUses, List<string> FailureLog, List<ActorBuild> Builds, int Survivors,
    int SoftEnrageCasts, int HardEnrageCasts, string BattleFingerprint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<BattleRoundTrace>? RoundTrace,
    int Depth, int Mastery, int StartingPotions, string FailureCategory, int Casualties, int RemainingPotions,
    int CharacterDeathEvents, List<MonsterBuild> InitialMonsters, string RuleRevision, int Players, SimulatorRewards Rewards,
    List<SimulatorCoopRewardEvent> CoopRewardEvents, decimal DirectDamageVariancePercent,
    List<MonsterPhaseObservation> MonsterPhaseRounds);

record MonsterPhaseObservation(int MonsterId, int LocalRound, bool IsActive, long WaterDamage, int Activations,
    int Breaks, int Expiries, int LinkedHits, int? LastActivationRound, int? LastBreakRound, int? LastExpiryRound,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ElementDamage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElementType? DamageElement = null);

record MonsterBuild(string Name, int Wave, int Position, int MaxHp, int Attack, int Defense, string CombatProfileCode);

record RoleBuild(string BaseProfession, TalentBuildProfile? Profile = null);
record TalentBuildProfile(string Role, Dictionary<string, int>? Nodes, List<string> Skills);
record ActorBuild(int Slot, string Profession, int Level, ElementType WeaponElement,
    List<string> EquippedSkills, Dictionary<string, int> TalentRanks, List<WeaponBuild> Weapons, EquipmentBuildStats Stats);
record EquipmentBuildStats(int BaseAttack, int BaseMaxHp, int MaxHp, Dictionary<string, decimal> Bonuses);
record WeaponBuild(int Slot, string Code, int TemplateRevision, ElementType Element, int ItemLevel, int QualityRank,
    int Attack, int MaxHp, List<WeaponSkillBuild> Skills);
record WeaponSkillBuild(int Slot, string Code, int Level, int EnhancementLevel);
}
