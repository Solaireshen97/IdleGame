using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public class SkillTalentTreeTests
{
    [Fact]
    public async Task BranchesRequireTheRootAndCapstoneRequiresBothBranches()
    {
        await using var test = await TreeTestContext.CreateAsync(points: 5);

        var (blocked, prerequisiteError) = await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-fortitude");
        Assert.Null(blocked);
        Assert.Equal("SkillTalentPrerequisiteRequired", prerequisiteError);
        Assert.Equal(5, test.Character.TalentPoints);

        var (root, rootError) = await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-vanguard");
        Assert.Null(rootError);
        Assert.Equal(4, root!.TalentPoints);
        Assert.Contains(root.LearnedSkills, skill => skill.Code == "knight-break");
        Assert.False(root.TalentNodes.Single(node => node.Code == "knight-oath").CanUnlock);

        await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-fortitude");
        var (stillBlocked, branchError) = await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-oath");
        Assert.Null(stillBlocked);
        Assert.Equal("SkillTalentPrerequisiteRequired", branchError);

        await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-offense");
        var (capstone, capstoneError) = await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-oath");
        Assert.Null(capstoneError);
        Assert.Equal(1, capstone!.TalentPoints);
        Assert.Contains(capstone.LearnedSkills, skill => skill.Code == "knight-verdict");
        Assert.Equal(4, await test.Db.CharacterSkillTalents.CountAsync());

        var (duplicate, duplicateError) = await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-oath");
        Assert.Null(duplicate);
        Assert.Equal("SkillTalentAlreadyUnlocked", duplicateError);
    }

    [Fact]
    public async Task SharedTalentPointsRestrictPurchasesAndForeignProfessionNodesAreRejected()
    {
        await using var test = await TreeTestContext.CreateAsync(points: 1);
        var (foreign, foreignError) = await test.Service.UnlockTalentNodeAsync(test.Token, 1, "cleric-light");
        Assert.Null(foreign);
        Assert.Equal("InvalidSkillTalent", foreignError);

        var stats = new TalentService(test.Db,
            new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create()));
        var (attack, attackError) = await stats.AllocateAsync(test.Token, 1, TalentType.Attack);
        Assert.Null(attackError);
        Assert.Equal(0, attack!.TalentPoints);
        var (blocked, pointsError) = await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-vanguard");
        Assert.Null(blocked);
        Assert.Equal("InsufficientTalentPoints", pointsError);

        await stats.ResetAsync(test.Token, 1);
        var (root, rootError) = await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-vanguard");
        Assert.Null(rootError);
        Assert.Equal(0, root!.TalentPoints);
        var (unaffordable, nextPointsError) = await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-offense");
        Assert.Null(unaffordable);
        Assert.Equal("InsufficientTalentPoints", nextPointsError);
    }

    [Fact]
    public async Task ResetRefundsActualCostAndUnequipsUnlockedSkills()
    {
        await using var test = await TreeTestContext.CreateAsync(points: 2);
        var (before, beforeError) = await test.Service.SetSlotAsync(test.Token, 1, 3,
            new SetSkillSlotRequest { SkillCode = "knight-break", AutoUseEnabled = true });
        Assert.Null(before);
        Assert.Equal("SkillNotLearned", beforeError);

        await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-vanguard");
        var (equipped, equipError) = await test.Service.SetSlotAsync(test.Token, 1, 3,
            new SetSkillSlotRequest { SkillCode = "knight-break", AutoUseEnabled = true });
        Assert.Null(equipError);
        Assert.Equal("knight-break", equipped!.Slots.Single(slot => slot.SlotIndex == 3).SkillCode);

        test.Db.Rooms.Add(new Room { Id = 1, OwnerUserId = 1, Status = RoomStatus.NotStarted });
        test.Db.RoomSlots.Add(new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, PendingSkillSlotMask = 4 });
        test.Db.BattleSkillCooldowns.Add(new BattleSkillCooldown
        {
            RoomId = 1, CharacterId = 1, SkillCode = "knight-break", ReadyAtRound = 4
        });
        await test.Db.SaveChangesAsync();
        var (reset, resetError) = await test.Service.ResetTalentTreeAsync(test.Token, 1);
        Assert.Null(resetError);
        Assert.Equal(2, reset!.TalentPoints);
        Assert.DoesNotContain(reset.LearnedSkills, skill => skill.Code == "knight-break");
        Assert.Null(reset.Slots.Single(slot => slot.SlotIndex == 3).SkillCode);
        Assert.Equal(0, (await test.Db.RoomSlots.SingleAsync()).PendingSkillSlotMask);
        Assert.Empty(await test.Db.CharacterSkillTalents.ToListAsync());
        Assert.Empty(await test.Db.BattleSkillCooldowns.ToListAsync());
        var (stale, staleError) = await test.Service.SetSlotAsync(test.Token, 1, 3,
            new SetSkillSlotRequest { SkillCode = "knight-break" });
        Assert.Null(stale);
        Assert.Equal("SkillNotLearned", staleError);
    }

    [Fact]
    public async Task CannotChangeTalentTreeDuringBattle()
    {
        await using var test = await TreeTestContext.CreateAsync(points: 2);
        test.Db.Rooms.Add(new Room { Id = 1, OwnerUserId = 1, Status = RoomStatus.Preparing });
        test.Db.RoomSlots.Add(new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1 });
        await test.Db.SaveChangesAsync();

        var (unlock, unlockError) = await test.Service.UnlockTalentNodeAsync(test.Token, 1, "knight-vanguard");
        var (reset, resetError) = await test.Service.ResetTalentTreeAsync(test.Token, 1);
        Assert.Null(unlock);
        Assert.Null(reset);
        Assert.Equal("LoadoutLocked", unlockError);
        Assert.Equal("LoadoutLocked", resetError);
        Assert.Equal(2, test.Character.TalentPoints);
    }

    [Fact]
    public async Task IncompleteTreeRowsDoNotGrantSkills()
    {
        await using var test = await TreeTestContext.CreateAsync(points: 2);
        test.Db.CharacterSkillTalents.Add(new CharacterSkillTalent
        {
            CharacterId = 1, NodeCode = "knight-oath", PointsSpent = 1
        });
        await test.Db.SaveChangesAsync();

        var (skills, error) = await test.Service.GetAsync(test.Token, 1);
        var (equipped, equipError) = await test.Service.SetSlotAsync(test.Token, 1, 3,
            new SetSkillSlotRequest { SkillCode = "knight-verdict" });

        Assert.Null(error);
        Assert.DoesNotContain(skills!.LearnedSkills, skill => skill.Code == "knight-verdict");
        Assert.Null(equipped);
        Assert.Equal("SkillNotLearned", equipError);
    }

    private sealed class TreeTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private TreeTestContext(string path, GameDbContext db, Character character)
        {
            _path = path;
            Db = db;
            Character = character;
            var catalog = SkillTestFactory.Create();
            Service = new SkillService(db, new UserService(db, ProgressionTestFactory.Create(), catalog), catalog);
        }

        public string Token => "token";
        public GameDbContext Db { get; }
        public Character Character { get; }
        public SkillService Service { get; }

        public static async Task<TreeTestContext> CreateAsync(int points)
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-skill-tree-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var db = new GameDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var character = new Character
            {
                Id = 1, UserId = 1, Name = "Knight", ProfessionCode = "knight",
                Level = points + 1, TalentPoints = points, Hp = 100, MaxHp = 100, Attack = 20, Defense = 5
            };
            db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                character,
                new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            return new TreeTestContext(path, db, character);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (File.Exists(_path)) File.Delete(_path);
        }
    }
}
