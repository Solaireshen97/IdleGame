using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class SoulImprintServiceTests
{
    [Fact]
    public async Task AutoUseCanBeConfiguredPerOwnedSoulImprint()
    {
        await using var test = await SoulImprintTestContext.CreateAsync();
        var imprint = new CharacterSoulImprint { CharacterId = 1, SoulImprintCode = "deep-core" };
        test.Db.CharacterSoulImprints.Add(imprint);
        await test.Db.SaveChangesAsync();

        var (response, error) = await test.Service.SetAutoAsync(test.Token, 1, imprint.Id,
            new SetSoulImprintAutoRequest { AutoUseEnabled = true });

        Assert.Null(error);
        Assert.True(Assert.Single(response!.SoulImprints).AutoUseEnabled);
        Assert.True((await test.Db.CharacterSoulImprints.SingleAsync()).AutoUseEnabled);
    }

    [Fact]
    public async Task EquipReplacesCurrentSoulImprintAndOnlyKeepsOneSlot()
    {
        await using var test = await SoulImprintTestContext.CreateAsync();
        var first = new CharacterSoulImprint { CharacterId = 1, SoulImprintCode = "deep-core" };
        var second = new CharacterSoulImprint { CharacterId = 1, SoulImprintCode = "deep-core" };
        test.Db.AddRange(first, second);
        await test.Db.SaveChangesAsync();

        Assert.Null((await test.Service.SetEquippedAsync(test.Token, 1,
            new SetSoulImprintRequest { SoulImprintId = first.Id })).Error);
        var (response, error) = await test.Service.SetEquippedAsync(test.Token, 1,
            new SetSoulImprintRequest { SoulImprintId = second.Id });

        Assert.Null(error);
        Assert.True(response!.SoulImprints.Single(item => item.Id == second.Id).IsEquipped);
        Assert.False(response.SoulImprints.Single(item => item.Id == first.Id).IsEquipped);
        Assert.Single(await test.Db.CharacterSoulImprints.Where(item => item.EquippedSlotIndex == 1).ToListAsync());
    }

    [Fact]
    public async Task DismantleRejectsEquippedOrLockedAndCreditsTierFragments()
    {
        await using var test = await SoulImprintTestContext.CreateAsync();
        var equipped = new CharacterSoulImprint
        {
            CharacterId = 1, SoulImprintCode = "deep-core", EquippedSlotIndex = 1
        };
        var locked = new CharacterSoulImprint
        {
            CharacterId = 1, SoulImprintCode = "deep-core", IsLocked = true
        };
        var spare = new CharacterSoulImprint { CharacterId = 1, SoulImprintCode = "deep-core" };
        test.Db.AddRange(equipped, locked, spare);
        await test.Db.SaveChangesAsync();

        Assert.Equal("SoulImprintEquipped", (await test.Service.DismantleAsync(test.Token, 1,
            new SoulImprintBatchRequest { SoulImprintIds = [equipped.Id] })).Error);
        Assert.Equal("SoulImprintLocked", (await test.Service.DismantleAsync(test.Token, 1,
            new SoulImprintBatchRequest { SoulImprintIds = [locked.Id] })).Error);
        var (response, error) = await test.Service.DismantleAsync(test.Token, 1,
            new SoulImprintBatchRequest { SoulImprintIds = [spare.Id] });

        Assert.Null(error);
        Assert.Equal(25, response!.Fragments.Single(item => item.Tier == 1).Quantity);
        Assert.Equal(2, await test.Db.CharacterSoulImprints.CountAsync());
    }

    private sealed class SoulImprintTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private SoulImprintTestContext(string path, GameDbContext db, SoulImprintService service)
        {
            _path = path;
            Db = db;
            Service = service;
        }

        public string Token => "soul-token";
        public GameDbContext Db { get; }
        public SoulImprintService Service { get; }

        public static async Task<SoulImprintTestContext> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-soul-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            db.AddRange(
                new User { Id = 1, UserName = "soul-owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new Character { Id = 1, UserId = 1, Name = "剑士", Hp = 100, MaxHp = 100, Attack = 20 },
                new UserLoginSession
                {
                    UserId = 1, Token = "soul-token", CreatedAt = DateTime.UtcNow,
                    ExpireAt = DateTime.UtcNow.AddDays(1)
                });
            await db.SaveChangesAsync();
            var catalog = new SoulImprintCatalog(Options.Create(new SoulImprintOptions
            {
                Items = [new SoulImprintDefinitionOptions
                {
                    Code = "deep-core", Name = "深岩震核", Description = "测试魂印。",
                    DungeonCode = "deep-mine", Tier = 1, Element = ElementType.Earth,
                    EffectType = SoulImprintEffectType.DamageArmorBreak, PowerPercent = 180,
                    SecondaryPowerPercent = 20, DurationRounds = 3, InitialCooldownRounds = 3,
                    CooldownRounds = 8, DismantleFragments = 25
                }]
            }));
            var users = new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create());
            return new SoulImprintTestContext(path, db, new SoulImprintService(db, users, catalog));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
