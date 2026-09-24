using System.Text.Json;
using System.Text.Json.Serialization;
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

// Run from the repository root. Uses only in-memory SQLite databases and production services.
// Equipment is supplied explicitly: this measures encounters, not acquisition time.
string Option(string name, string fallback) => Array.IndexOf(args, name) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Option("--config", "Game.Server/appsettings.json"))).Build();
var worldConfig = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath("Game.Server/world.json")).Build();
var world = new WorldCatalog(Options.Create(worldConfig.GetSection(WorldOptions.SectionName).Get<WorldOptions>()!));
var runs = int.Parse(Option("--runs", "5"));
var elements = Option("--elements", "Fire,Water,Earth,Wind,Light,Dark").Split(',').Select(Enum.Parse<ElementType>).ToList();
var roles = Option("--roles", "knight,warrior,priest,inquisitor,elementalist,arcanist,marksman,beastmaster,assassin,trickster")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var stages = Option("--stages", "starter,shop,field,week,graduate").Split(',');
var targets = Option("--targets", "normal,dungeon,elite,endgame").Split(',');
var partySize = int.Parse(Option("--party", "1"));
var controlMode = Option("--mode", "auto").ToLowerInvariant();
if (runs is < 1 or > 100 || partySize is < 1 or > 5) throw new ArgumentOutOfRangeException("runs / party");
if (controlMode is not ("auto" or "manual")) throw new ArgumentException("--mode must be auto or manual");
var results = new List<Sample>();
foreach (var stage in stages)
foreach (var element in elements)
foreach (var role in roles)
foreach (var target in targets)
{
    if (stage is "starter" or "shop" && target != "normal") continue;
    for (var seed = 1; seed <= runs; seed++) results.Add(await Simulate(stage, element, role, target, partySize, seed, controlMode));
    var group = results.TakeLast(runs).ToList();
    Console.WriteLine($"{stage}/{element}/{role}/{target}/party{partySize}: {group.Count(x => x.Victory)}/{runs}, {group.Average(x => x.Rounds):0.0} rounds, {group.Average(x => x.CycleSeconds)/60:0.0} min, {group.Average(x => x.PotionsUsed):0.0} potions");
}
var report = new
{
    Assumptions = new { RunsPerScenario = runs, MaxRounds = 250, PartySize = partySize, ControlMode = controlMode,
        Description = "Real BattleService, MonsterCombatService, RewardService and configuration; deterministic seeds; preset equipment and legal talent budgets; 1000 starting potions per actor; full health on entry; includes wave/cooldown/repeat waits and excludes acquisition time. Auto uses production Auto conditions. Manual queues every currently applicable equipped skill and soul before each round, while retaining automatic room progression for deterministic simulation. Successful encounters include 30s repeat wait; failures have no automatic restart." },
    Summary = results.GroupBy(x => new { x.Stage, x.Element, x.Profession, x.Dungeon, x.PartySize }).Select(group => new
    {
        group.Key, Runs = group.Count(), Victories = group.Count(x => x.Victory),
        MeanRounds = group.Average(x => x.Rounds), MeanCycleSeconds = group.Average(x => x.CycleSeconds),
        MeanPotionsUsed = group.Average(x => x.PotionsUsed), MeanPotionDrops = group.Average(x => x.PotionDrops),
        MeanGold = group.Average(x => x.Gold), MeanRemainingHp = group.Average(x => x.RemainingHp),
        Attack = group.First().Attack, MaxHp = group.First().MaxHp
    }),
    Samples = results
};
var output = Path.GetFullPath(Option("--output", "docs/t1-combat-results.json"));
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
Console.WriteLine($"Saved {results.Count} samples to {output}");

IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(config.GetSection(section).Get<T>()!);

async Task<Sample> Simulate(string stage, ElementType element, string profession, string target, int party, int seed,
    string mode)
{
    var random = new Random(seed);
    var weapons = new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName));
    var skills = new SkillCatalog(Bind<SkillOptions>(SkillOptions.SectionName));
    var consumables = new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName));
    var materials = new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName));
    var soulImprints = new SoulImprintCatalog(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName));
    var rewards = new RewardCatalog(Bind<RewardOptions>(RewardOptions.SectionName), consumables, weapons,
        materials, soulImprints, random);
    var combatCatalog = new MonsterCombatCatalog(Bind<MonsterCombatOptions>(MonsterCombatOptions.SectionName));
    var encounters = new DungeonEncounterCatalog(Bind<DungeonEncounterOptions>(DungeonEncounterOptions.SectionName), combatCatalog, rewards);
    var region = world.Regions.Single(region => region.FeaturedElement == element);
    var definition = target switch
    {
        "normal" => world.Dungeons.Single(d => d.RegionCode == region.Code && d.DungeonKind == "Hunt" && d.MinimumLevel == (stage == "starter" ? 1 : stage == "shop" ? 4 : 7)),
        "dungeon" => world.Dungeons.Single(d => d.Code == region.FeaturedDungeonCode),
        "elite" => world.Dungeons.Single(d => d.RegionCode == region.Code && d.DungeonKind == "Elite" && d.MinimumLevel == 10),
        "endgame" => world.Dungeons.Single(d => d.RegionCode == region.Code && d.DungeonKind == "Dungeon" && d.MinimumLevel == 10),
        "depths" => world.Dungeons.Single(d => d.Code == "kobold-mine-depths"),
        _ => throw new ArgumentException($"Unknown target {target}")
    };
    await using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();
    await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
    await db.Database.EnsureCreatedAsync();
    var dungeon = JsonSerializer.Deserialize<Dungeon>(JsonSerializer.Serialize(definition))!;
    var user = new User { Id = 1, UserName = "simulation", PasswordHash = "unused", ActiveCharacterId = 1, StarterWeaponRewardClaimed = true };
    db.AddRange(user, dungeon);
    await db.SaveChangesAsync();
    var room = new Room { DungeonId = dungeon.Id, OwnerUserId = 1, SlotCount = 5, Status = RoomStatus.NotStarted, IsPreparationTimeoutEnabled = false, TotalWaveCount = encounters.GetWaveCount(dungeon) };
    db.Rooms.Add(room);
    await db.SaveChangesAsync();
    var monsters = encounters.CreateMonsters(dungeon).ToList();
    foreach (var monster in monsters) monster.RoomId = room.Id;
    db.Monsters.AddRange(monsters);
    await db.SaveChangesAsync();
    room.MonsterId = monsters[0].Id;
    db.UserDungeonClears.Add(new UserDungeonClear { UserId = 1, DungeonId = dungeon.Id, ClearedAtUtc = DateTime.UtcNow });
    var actors = new List<Character>();
    for (var index = 1; index <= party; index++)
    {
        var role = party == 1 ? profession : BalancedPartyRole(index);
        var build = ResolveRole(role);
        var baseProfession = build.BaseProfession;
        var character = new Character { Id = index, UserId = 1, Name = $"{role}-{index}", ProfessionCode = baseProfession,
            AdvancedProfessionCode = stage is "starter" or "shop" ? null : build.Promotion,
            Level = stage == "starter" ? 1 : stage == "shop" ? 5 : 10 };
        var loadout = Loadout(weapons, stage, element, index);
        character.Attack = loadout.Sum(item => item.Attack);
        character.MaxHp = loadout.Sum(item => item.MaxHp);
        var talentCodes = Talents(character, stage, build);
        weapons.ApplyBonuses(character, loadout);
        character.Hp = TalentRules.EffectiveMaxHp(character);
        actors.Add(character);
        db.Characters.Add(character);
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
        db.RoomSlots.Add(new RoomSlot { RoomId = room.Id, SlotIndex = index, UserId = 1, CharacterId = index, IsMainControl = index == 1, IsAutoEnabled = true });
        db.CharacterItemStacks.Add(new CharacterItemStack { CharacterId = index, ItemCode = "minor-healing-potion", Quantity = 1000 });
        db.CharacterConsumableSlots.Add(new CharacterConsumableSlot { CharacterId = index, SlotIndex = 1, ItemCode = "minor-healing-potion", AutoUseEnabled = true, AutoHpThresholdPercent = 70 });
        foreach (var (code, rank) in talentCodes) db.CharacterSkillTalents.Add(new CharacterSkillTalent { CharacterId = index, NodeCode = code, PointsSpent = rank });
        var learned = skills.LearnedSkills(character, talentCodes).Select(skill => skill.Code).ToHashSet();
        var preferred = PreferredSkills(build.Promotion);
        var selected = preferred.Where(learned.Contains).Take(5).ToList();
        for (var slot = 0; slot < selected.Count; slot++) db.CharacterSkillSlots.Add(new CharacterSkillSlot
        { CharacterId = index, SlotIndex = slot + 1, SkillCode = selected[slot], AutoUseEnabled = true, AutoHpThresholdPercent = 75 });
        if (stage == "raid")
        {
            var imprint = soulImprints.Materialize(soulImprints.Items.Single(item =>
                item.DungeonCode == world.Dungeons.Single(d => d.RegionCode == region.Code &&
                    d.DungeonKind == "Dungeon" && d.MinimumLevel == 10).Code).Code, index);
            imprint.EquippedSlotIndex = SoulImprintRules.SlotIndex;
            imprint.AutoUseEnabled = true;
            db.CharacterSoulImprints.Add(imprint);
        }
    }
    await db.SaveChangesAsync();
    var progression = new ProgressionService(Bind<ProgressionOptions>(ProgressionOptions.SectionName));
    var users = new UserService(db, progression, skills, weapons);
    var rewardService = new RewardService(db, rewards, progression);
    var monsterCombat = new MonsterCombatService(db, combatCatalog, random);
    var runService = new DungeonRunService(db, rewardService, monsterCombat);
    var battle = new BattleService(db, users, consumables, skills, rewardService, runService, monsterCombat,
        random: random, weaponCatalog: weapons, soulImprintCatalog: soulImprints);
    var seconds = 0d;
    var logs = new List<string>();
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
        if (room.Status == RoomStatus.BattleOver) { victory = result.IsVictory; if (victory) seconds += BattleRules.RepeatBattleDelaySeconds; break; }
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
    return new Sample(stage, element, profession, dungeon.Code, party, mode, seed, victory, room.RoundNumber, seconds,
        party * 1000 + potionDrops - stock, potionDrops, actors.Sum(actor => actor.Gold), actors.Sum(actor => actor.Hp), actors[0].Attack,
        TalentRules.EffectiveMaxHp(actors[0]), finalMonster.Hp, finalMonster.MaxHp, roundCounts,
        victory ? [] : logs.TakeLast(8).ToList());
}

List<CharacterWeapon> Loadout(WeaponCatalog catalog, string stage, ElementType element, int characterId)
{
    var all = config.GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!.Items;
    var field = all.Where(item => item.Element == element && item.Code.StartsWith("t1-") && !item.Code.StartsWith("t1-shop-")).OrderByDescending(item => item.Attack).ToList();
    var bossCode = world.Regions.Single(region => region.FeaturedElement == element).FeaturedWeaponCode;
    var exchange = config.GetSection(DungeonExchangeOptions.SectionName).Get<DungeonExchangeOptions>()!.Offers;
    var mine = exchange.Single(offer => offer.DungeonCode == "kobold-mine" && offer.RewardKind == "Weapon" &&
        catalog.FindItem(offer.EffectiveRewardCode)!.Element == element).EffectiveRewardCode;
    var endgameCode = world.Dungeons.Single(dungeon => dungeon.RegionCode ==
        world.Regions.Single(region => region.FeaturedElement == element).Code &&
        dungeon.DungeonKind == "Dungeon" && dungeon.MinimumLevel == 10).Code;
    var final = exchange.Single(offer => offer.DungeonCode == endgameCode && offer.RewardKind == "Weapon" &&
        catalog.FindItem(offer.EffectiveRewardCode)!.Element == element).EffectiveRewardCode;
    var eliteNames = world.Dungeons.Where(d => d.DungeonKind == "Elite" && d.RegionCode == world.Regions.Single(r => r.FeaturedElement == element).Code).Select(d => d.Code).ToList();
    var rewards = config.GetSection(RewardOptions.SectionName).Get<RewardOptions>()!;
    var elite = eliteNames.Select(code => rewards.MonsterKills[code].Drops.Single(drop => drop.Kind == "Weapon").Code).ToList();
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
        if (stage == "starter" && index == 0) { weapon.Attack = 16; weapon.MaxHp = 40; }
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

Dictionary<string, int> Talents(Character character, string stage, RoleBuild build)
{
    if (stage == "starter") return new(StringComparer.OrdinalIgnoreCase);
    var isShop = stage == "shop";
    Dictionary<string, int> nodes = character.ProfessionCode switch
    {
        "swordsman" when isShop => new() { ["sword-rhythm"] = 1, ["sword-edge"] = 1, ["sword-vitality"] = 1, ["sword-assault-stance"] = 1 },
        "swordsman" when build.Style == "guard" => new() { ["sword-rhythm"] = 1, ["sword-edge"] = 1, ["sword-vitality"] = 1, ["sword-guard-stance"] = 1,
            ["sword-recovery-training"] = 2, ["sword-counteroffense"] = 1, ["sword-protection"] = 2 },
        "swordsman" => new() { ["sword-rhythm"] = 1, ["sword-edge"] = 1, ["sword-vitality"] = 1, ["sword-assault-stance"] = 1,
            ["sword-combat-training"] = 2, ["sword-pursuit"] = 1, ["sword-precision"] = 2 },
        "acolyte" when isShop => new() { ["acolyte-echo"] = 1, ["acolyte-doctrine"] = 1, ["acolyte-prayer"] = 1, ["acolyte-judgment"] = 1 },
        "acolyte" when build.Style == "mercy" => new() { ["acolyte-echo"] = 1, ["acolyte-doctrine"] = 1, ["acolyte-prayer"] = 1, ["acolyte-mercy"] = 1,
            ["acolyte-heal-training"] = 2, ["acolyte-afterglow"] = 1, ["acolyte-devotion"] = 2 },
        "acolyte" => new() { ["acolyte-echo"] = 1, ["acolyte-doctrine"] = 1, ["acolyte-prayer"] = 1, ["acolyte-judgment"] = 1,
            ["acolyte-light-training"] = 2, ["acolyte-light-return"] = 1, ["acolyte-focus"] = 2 },
        "mage" when isShop => new() { ["mage-arcane-insight"] = 1, ["mage-flow"] = 1, ["mage-frost-discipline"] = 1, ["mage-arcane-training"] = 1 },
        "mage" when build.Style == "burst" => new() { ["mage-arcane-insight"] = 1, ["mage-flow"] = 1, ["mage-frost-discipline"] = 1,
            ["mage-arcane-training"] = 2, ["mage-barrage-talent"] = 1, ["mage-precision"] = 2, ["mage-arcane-mastery"] = 1 },
        "mage" => new() { ["mage-flow"] = 1, ["mage-frost-discipline"] = 1, ["mage-spellbreak-talent"] = 1,
            ["mage-suppression-talent"] = 1, ["mage-countermagic"] = 2, ["mage-stable-channeling"] = 1, ["mage-ward-training"] = 2 },
        "hunter" when isShop => new() { ["hunter-keen-eye"] = 1, ["hunter-steady-hand"] = 1, ["hunter-fieldcraft"] = 1, ["hunter-bow-training"] = 1 },
        "hunter" when build.Style == "precision" => new() { ["hunter-keen-eye"] = 1, ["hunter-steady-hand"] = 1, ["hunter-venom-talent"] = 1,
            ["hunter-bow-training"] = 2, ["hunter-mark-talent"] = 1, ["hunter-precision"] = 2, ["hunter-predator"] = 1 },
        "hunter" => new() { ["hunter-steady-hand"] = 1, ["hunter-fieldcraft"] = 1, ["hunter-mending-training"] = 2,
            ["hunter-venom-talent"] = 1, ["hunter-volley-talent"] = 1, ["hunter-toxin-training"] = 2, ["hunter-relentless"] = 1 },
        "rogue" when isShop => new() { ["rogue-killer-instinct"] = 1, ["rogue-light-fingers"] = 1, ["rogue-footwork"] = 1, ["rogue-blade-training"] = 1 },
        "rogue" when build.Style == "burst" => new() { ["rogue-killer-instinct"] = 1, ["rogue-light-fingers"] = 1, ["rogue-footwork"] = 1,
            ["rogue-blade-training"] = 2, ["rogue-flurry-talent"] = 1, ["rogue-deadly-precision"] = 2, ["rogue-relentless-assault"] = 1 },
        "rogue" => new() { ["rogue-light-fingers"] = 1, ["rogue-footwork"] = 1, ["rogue-recovery-training"] = 2,
            ["rogue-poison-talent"] = 1, ["rogue-gouge-talent"] = 1, ["rogue-dirty-fighting"] = 2, ["rogue-opportunist"] = 1 },
        _ => throw new InvalidOperationException($"Unsupported base profession: {character.ProfessionCode}")
    };
    var settings = config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!;
    foreach (var (code, rank) in nodes)
    {
        var node = settings.TalentNodes.Single(item => item.Code == code);
        var value = node.ValuePerRank * rank;
        switch (node.EffectCode)
        {
            case "MaxHpPercent": character.TalentMaxHpPercent += value; break;
            case "NormalAttackPercent": character.TalentNormalAttackPercent += value; break;
            case "SkillDamagePercent": character.TalentSkillDamagePercent += value; break;
            case "HealingDonePercent": character.TalentHealingDonePercent += value; break;
            case "HealingReceivedPercent": character.TalentHealingReceivedPercent += value; break;
            case "SkillCriticalChancePercent": character.TalentSkillCriticalChancePercent += value; break;
        }
    }
    var spent = nodes.Sum(item => item.Value);
    if (spent > character.Level - 1) throw new InvalidOperationException("Illegal talent budget");
    character.TalentPoints = character.Level - 1 - spent;
    return nodes;
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

static RoleBuild ResolveRole(string role) => role.ToLowerInvariant() switch
{
    "knight" or "swordsman-guard" => new("swordsman", "knight", "guard"),
    "warrior" or "swordsman-assault" => new("swordsman", "warrior", "assault"),
    "priest" or "acolyte-mercy" => new("acolyte", "priest", "mercy"),
    "inquisitor" or "acolyte-judgment" => new("acolyte", "inquisitor", "judgment"),
    "elementalist" => new("mage", "elementalist", "burst"),
    "arcanist" => new("mage", "arcanist", "control"),
    "marksman" => new("hunter", "marksman", "precision"),
    "beastmaster" => new("hunter", "beastmaster", "survival"),
    "assassin" => new("rogue", "assassin", "burst"),
    "trickster" => new("rogue", "trickster", "control"),
    _ => throw new ArgumentException($"Unknown role {role}")
};

static string[] PreferredSkills(string promotion) => promotion switch
{
    "knight" => ["knight-guard", "sword-parry", "sword-intercept", "sword-double-slash", "sword-slash"],
    "warrior" => ["warrior-fury", "sword-double-slash", "sword-intercept", "sword-parry", "sword-slash"],
    "priest" => ["priest-group-heal", "acolyte-heal", "acolyte-purify", "acolyte-radiant-flare", "acolyte-holy-bolt"],
    "inquisitor" => ["inquisitor-condemn", "acolyte-heal", "acolyte-purify", "acolyte-radiant-flare", "acolyte-holy-bolt"],
    "elementalist" => ["elementalist-pyroblast", "mage-arcane-barrage", "mage-frost-ward", "mage-arcane-bolt"],
    "arcanist" => ["arcanist-overcharge", "mage-arcane-suppression", "mage-spellbreak", "mage-frost-ward", "mage-arcane-bolt"],
    "marksman" => ["marksman-sniper-shot", "hunter-marked-shot", "hunter-venom-arrow", "hunter-field-mend", "hunter-quick-shot"],
    "beastmaster" => ["beastmaster-coordinated-assault", "hunter-field-mend", "hunter-venom-arrow", "hunter-rapid-volley", "hunter-quick-shot"],
    "assassin" => ["assassin-deathblow", "rogue-blade-flurry", "rogue-evasion", "rogue-shadow-strike"],
    "trickster" => ["trickster-smoke-bomb", "rogue-gouge", "rogue-poisoned-blade", "rogue-evasion", "rogue-shadow-strike"],
    _ => throw new ArgumentException($"Unknown promotion {promotion}")
};

record Sample(string Stage, ElementType Element, string Profession, string Dungeon, int PartySize, string ControlMode, int Seed,
    bool Victory, int Rounds, double CycleSeconds, int PotionsUsed, int PotionDrops, int Gold, int RemainingHp,
    int Attack, int MaxHp, int MonsterHp, int MonsterMaxHp, Dictionary<string, int> EnemyRounds, List<string> FailureLog);

record RoleBuild(string BaseProfession, string Promotion, string Style);
