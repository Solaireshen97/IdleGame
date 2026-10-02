using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task SoulAutoApiPersistsRoomOverrideAndBattleUsesIt()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var imprint = new CharacterSoulImprint { CharacterId = test.Character.Id, SoulImprintCode = "deep-core",
            EquippedSlotIndex = 1, AutoUseEnabled = false };
        test.Db.CharacterSoulImprints.Add(imprint);
        await test.EnableAutoForCharacterAsync(test.Character);
        var skills = SkillTestFactory.Create();
        var service = new SoulImprintService(test.Db,
            new UserService(test.Db, ProgressionTestFactory.Create(), skills), SoulImprintTestFactory.Create());
        var (response, error) = await service.SetAutoAsync(test.Token, test.Character.Id, imprint.Id,
            new SetSoulImprintAutoRequest { AutoUseEnabled = true });
        Assert.Null(error);
        Assert.True(Assert.Single(response!.SoulImprints).AutoUseEnabled);
        Assert.False(imprint.AutoUseEnabled);
        await using var restored = test.CreateDbContext();
        var slot = await restored.RoomSlots.SingleAsync();
        Assert.True(BattleAutoPolicyResolver.SoulAuto(slot, false));
        // This imprint has a three-round initial cooldown.
        test.Room.RoundNumber = 3;
        await test.Db.SaveChangesAsync();
        var (battle, battleError) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(battleError);
        Assert.Contains(battle!.Logs, log => log.Contains("释放魂印"));
        Assert.False((await test.Db.CharacterSoulImprints.SingleAsync()).AutoUseEnabled);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RoomSkillOverrideControlsExecutionWithoutChangingEquippedDefaults(bool baseEnabled, bool roomEnabled)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: baseEnabled);
        await test.EnableAutoForCharacterAsync(test.Character);
        var slot = await test.Db.RoomSlots.SingleAsync();
        slot.AutoPolicyOverridesJson = BattleAutoPolicyResolver.WithSkill(slot, 1, roomEnabled, "Always", 70);
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(roomEnabled, result!.Logs.Any(log => log.Contains("使用 盾击")));
        Assert.Equal(baseEnabled, (await test.Db.CharacterSkillSlots.SingleAsync()).AutoUseEnabled);
    }
}
