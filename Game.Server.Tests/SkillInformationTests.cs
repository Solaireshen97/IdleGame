using Game.Server.Configuration;
using Game.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task SharedKnightBarrierDoesNotGrantAnotherProfessionCounterPermission()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Character.ProfessionCode = "mage";
        test.Character.Level = 30;
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsters = new MonsterCombatCatalog(Options.Create(configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var skillOptions = configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!;
        skillOptions.Professions.Single(profession => profession.Code == "swordsman").SharedSkillCode = "knight-faith-barrier";
        var skills = new SkillCatalog(Options.Create(skillOptions), monsters);
        var skill = skills.Resolve(test.Character, "knight-faith-barrier", new Dictionary<string, int> { ["swordsman"] = 30 })!;
        Assert.True(skill.IsShared);
        var statuses = new BattleStatusService(test.Db, monsters.Statuses);
        var executor = UnifiedExecutor(statuses, skills);
        var battle = new BattleExecutionContext(test.Room, test.Monster, await UnifiedPartyAsync(test.Db),
            new Dictionary<int, Game.Shared.Enums.ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []);

        await executor.ExecuteAsync(new(battle, skill, battle.Characters[0]), new ProfessionMechanicRegistry().For(skill));

        var guard = await executor.Guards.DefenseAsync(test.Room, test.Character.Id);
        Assert.True(guard.ReductionPercent > 0);
        Assert.False(guard.KnightCounterEligible);
        Assert.False(await statuses.HasAsync(test.Room, "Character", test.Character.Id, BattleGuardService.PermissionCode));
    }

    [Fact]
    public async Task LoadoutRoomAndMonsterIntentUseCompiledEffectDescriptions()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = 30;
        test.Monster.CombatProfileCode = "slime-acid";
        await test.AddSkillAsync(test.Character, 1, "rogue-adrenaline", false);
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsterOptions = configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!;
        monsterOptions.Profiles["slime-acid"].SkillUseChancePercent = 100;
        var monsters = new MonsterCombatCatalog(Options.Create(monsterOptions));
        var skills = CreateImmediateProductionSkills(configuration, monsters);
        var information = new SkillInformationService(monsters.Statuses);
        var combat = new MonsterCombatService(test.Db, monsters, skillInformation: information);
        var progression = ProgressionTestFactory.Create();
        var users = new UserService(test.Db, progression, skills);
        var (loadout, error) = await new SkillService(test.Db, users, skills, information).GetAsync(test.Token, test.Character.Id);
        var room = await new RoomService(test.Db, users, progression, ConsumableTestFactory.Create(), skills,
            RewardTestFactory.CreateService(test.Db, progression), monsterCombatService: combat, skillInformation: information)
            .GetRoomDetailAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        var source = Assert.Single(loadout!.LearnedSkills, skill => skill.Code == "rogue-adrenaline");
        var equipped = Assert.Single(room!.Slots.Single(slot => slot.CharacterId == test.Character.Id).Skills, skill => skill.SkillCode == source.Code);
        Assert.Equal(source.Effects.Select(effect => effect.Summary), equipped.Effects.Select(effect => effect.Summary));
        var status = Assert.Single(source.Effects, effect => effect.Type == "ApplyStatus");
        Assert.Equal(monsters.FindStatus(status.StatusCode)!.Name, status.StatusName);
        Assert.Contains(status.StatusName!, status.Summary);
        Assert.Contains("本回合及后续", status.Summary);
        Assert.Equal(monsters.FindStatus(status.StatusCode)!.Description, status.StatusDescription);
        Assert.True(status.StatusIsPositive);
        var intent = await combat.GetIntentResponseAsync(test.Room, test.Monster);
        Assert.Equal(information.Effects(monsters.ResolveSkill(intent!.SkillCode)!).Select(effect => effect.Summary),
            intent.Effects.Select(effect => effect.Summary));
    }

    [Fact]
    public void CommonExecutorAndMonsterServiceShareTheScopedStateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(test => Options.Create(new MonsterCombatOptions()));
        services.AddSingleton(test => Options.Create(new SkillOptions()));
        services.AddSingleton<BattleStatusCatalog>();
        services.AddSingleton<MonsterCombatCatalog>();
        services.AddSingleton<SkillCatalog>();
        services.AddSingleton<SkillInformationService>();
        services.AddDbContext<Game.Server.Data.GameDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddScoped<BattleStatusService>();
        services.AddScoped<BattleGuardService>();
        services.AddScoped<BattleDamageService>();
        services.AddScoped<BattleEffectExecutor>();
        services.AddScoped<MonsterCombatService>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        var executor = scope.ServiceProvider.GetRequiredService<BattleEffectExecutor>();
        var monster = scope.ServiceProvider.GetRequiredService<MonsterCombatService>();

        Assert.Same(scope.ServiceProvider.GetRequiredService<BattleStatusService>(), executor.Statuses);
        Assert.Same(executor.Statuses, monster.Statuses);
        Assert.Same(scope.ServiceProvider.GetRequiredService<BattleGuardService>(), executor.Guards);
    }
}
