using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Shop;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public class ShopServiceTests
{
    [Fact]
    public async Task ConsumablePurchaseUsesServerPriceAndDeliversOnlyToActiveCharacter()
    {
        await using var test = await ShopTestContext.CreateAsync();
        var other = new Character { UserId = 1, Name = "Cleric", Hp = 100, MaxHp = 100, Attack = 10};
        test.Db.Characters.Add(other);
        await test.Db.SaveChangesAsync();

        var (response, error) = await test.Service.PurchaseAsync(test.Token, new PurchaseShopItemRequest
        {
            CharacterId = test.Character.Id, Code = "minor-healing-potion", Quantity = 5
        });

        Assert.Null(error);
        Assert.Equal(50, response!.Gold);
        Assert.Equal(5, response.Items.Single(item => item.Code == "minor-healing-potion").OwnedQuantity);
        var stack = Assert.Single(await test.Db.CharacterItemStacks.ToListAsync());
        Assert.Equal(test.Character.Id, stack.CharacterId);
        Assert.Equal(5, stack.Quantity);
        Assert.Empty(await test.Db.CharacterItemStacks.Where(item => item.CharacterId == other.Id).ToListAsync());
        Assert.Equal(0, test.User.Version);
        Assert.Equal(1, test.Character.Version);
    }

    [Fact]
    public async Task WeaponPurchaseCreatesUnequippedTemplateWeaponAndChargesOnce()
    {
        await using var test = await ShopTestContext.CreateAsync();

        var (response, error) = await test.Service.PurchaseAsync(test.Token, new PurchaseShopItemRequest
        {
            CharacterId = test.Character.Id, Code = "cinder-knife", Quantity = 1
        });

        Assert.Null(error);
        Assert.Equal(55, response!.Gold);
        var product = response.Items.Single(item => item.Code == "cinder-knife");
        Assert.Equal(1, product.OwnedQuantity);
        Assert.Equal(ElementType.Fire, product.Element);
        var weapon = Assert.Single(await test.Db.CharacterWeapons.Include(item => item.Skills).ToListAsync());
        Assert.Equal(test.Character.Id, weapon.CharacterId);
        Assert.Null(weapon.EquippedSlotIndex);
        Assert.Equal((6, 18), (weapon.Attack, weapon.MaxHp));
        Assert.Equal(2, Assert.Single(weapon.Skills).Level);
    }

    [Fact]
    public async Task InvalidPurchasesDoNotSpendGoldOrCreateItems()
    {
        await using var test = await ShopTestContext.CreateAsync();
        var requests = new[]
        {
            (new PurchaseShopItemRequest { CharacterId = 2, Code = "minor-healing-potion", Quantity = 1 }, "ActiveCharacterChanged"),
            (new PurchaseShopItemRequest { CharacterId = 1, Code = "unknown", Quantity = 1 }, "ProductNotFound"),
            (new PurchaseShopItemRequest { CharacterId = 1, Code = "minor-healing-potion", Quantity = 0 }, "InvalidQuantity"),
            (new PurchaseShopItemRequest { CharacterId = 1, Code = "minor-healing-potion", Quantity = 100 }, "InvalidQuantity"),
            (new PurchaseShopItemRequest { CharacterId = 1, Code = "cinder-knife", Quantity = 2 }, "InvalidQuantity"),
            (new PurchaseShopItemRequest { CharacterId = 1, Code = "minor-healing-potion", Quantity = 11 }, "InsufficientGold")
        };
        foreach (var (request, expectedError) in requests)
        {
            var (response, error) = await test.Service.PurchaseAsync(test.Token, request);
            Assert.Null(response);
            Assert.Equal(expectedError, error);
        }
        Assert.Equal(100, test.Character.Gold);
        Assert.Empty(await test.Db.CharacterItemStacks.ToListAsync());
        Assert.Empty(await test.Db.CharacterWeapons.ToListAsync());

        test.Db.CharacterItemStacks.Add(new CharacterItemStack
        {
            CharacterId = test.Character.Id, ItemCode = "minor-healing-potion", Quantity = int.MaxValue
        });
        await test.Db.SaveChangesAsync();
        var (limitResponse, limitError) = await test.Service.PurchaseAsync(test.Token, new PurchaseShopItemRequest
        {
            CharacterId = test.Character.Id, Code = "minor-healing-potion", Quantity = 1
        });
        Assert.Null(limitResponse);
        Assert.Equal("InventoryLimitReached", limitError);
        Assert.Equal(100, test.Character.Gold);
    }

    [Fact]
    public async Task StaleWalletPurchaseRollsBackDelivery()
    {
        await using var test = await ShopTestContext.CreateAsync();
        test.Character.Gold = 45;
        await test.Db.SaveChangesAsync();
        await using var firstDb = test.CreateDbContext();
        await using var secondDb = test.CreateDbContext();
        var first = ShopTestContext.CreateService(firstDb);
        var second = ShopTestContext.CreateService(secondDb);
        await first.GetAsync(test.Token);
        await second.GetAsync(test.Token);
        var request = new PurchaseShopItemRequest { CharacterId = 1, Code = "cinder-knife", Quantity = 1 };

        var (firstResponse, firstError) = await first.PurchaseAsync(test.Token, request);
        var (secondResponse, secondError) = await second.PurchaseAsync(test.Token, request);

        Assert.Null(firstError);
        Assert.Equal(0, firstResponse!.Gold);
        Assert.Null(secondResponse);
        Assert.Equal("ConcurrencyConflict", secondError);
        await using var verification = test.CreateDbContext();
        Assert.Equal(0, (await verification.Characters.SingleAsync()).Gold);
        Assert.Single(await verification.CharacterWeapons.ToListAsync());
    }

    [Fact]
    public async Task ShopViewFollowsSelectedCharacterAndKeepsGoldSeparate()
    {
        await using var test = await ShopTestContext.CreateAsync();
        var second = new Character { UserId = 1, Name = "Mage", Hp = 100, MaxHp = 100, Attack = 8};
        test.Db.Characters.Add(second);
        await test.Db.SaveChangesAsync();
        await test.Service.PurchaseAsync(test.Token, new PurchaseShopItemRequest
        {
            CharacterId = test.Character.Id, Code = "minor-healing-potion", Quantity = 1
        });
        test.User.ActiveCharacterId = second.Id;
        await test.Db.SaveChangesAsync();

        var (view, error) = await test.Service.GetAsync(test.Token);

        Assert.Null(error);
        Assert.Equal(second.Id, view!.CharacterId);
        Assert.Equal(0, view.Gold);
        Assert.Equal(90, test.Character.Gold);
        Assert.Equal(0, view.Items.Single(item => item.Code == "minor-healing-potion").OwnedQuantity);
    }

    [Fact]
    public async Task DungeonExchangeConsumesCharacterMaterialAndCreatesQualityWeapon()
    {
        await using var test = await ShopTestContext.CreateAsync();
        test.Db.CharacterItemStacks.Add(new CharacterItemStack
        {
            CharacterId = test.Character.Id, ItemCode = "kobold-mine-token", Quantity = 8
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.ExchangeAsync(test.Token, new ExchangeDungeonWeaponRequest
        {
            CharacterId = test.Character.Id, OfferCode = "kobold-fire"
        });
        var (rejected, rejectedError) = await test.Service.ExchangeAsync(test.Token, new ExchangeDungeonWeaponRequest
        {
            CharacterId = test.Character.Id, OfferCode = "kobold-fire"
        });

        Assert.Null(error);
        Assert.Equal(2, result!.Shop.Materials.Single(material => material.Code == "kobold-mine-token").Quantity);
        Assert.Contains("余烬短剑", result.WeaponDisplayName);
        Assert.Equal(100, result.Shop.Gold);
        Assert.Single(await test.Db.CharacterWeapons.ToListAsync());
        Assert.Null(rejected);
        Assert.Equal("InsufficientDungeonCurrency", rejectedError);
    }

    [Fact]
    public async Task DungeonExchangeConsumesOneTokenAndAddsConfiguredFragments()
    {
        await using var test = await ShopTestContext.CreateAsync();
        test.Db.CharacterItemStacks.Add(new CharacterItemStack
        {
            CharacterId = test.Character.Id, ItemCode = "kobold-mine-token", Quantity = 2
        });
        await test.Db.SaveChangesAsync();

        var (first, error) = await test.Service.ExchangeAsync(test.Token, new ExchangeDungeonWeaponRequest
        {
            CharacterId = test.Character.Id, OfferCode = "kobold-fragments"
        });
        var (second, secondError) = await test.Service.ExchangeAsync(test.Token, new ExchangeDungeonWeaponRequest
        {
            CharacterId = test.Character.Id, OfferCode = "kobold-fragments"
        });

        Assert.Null(error);
        Assert.Null(secondError);
        Assert.Equal("T1 武器碎片", first!.RewardDisplayName);
        Assert.Equal(3, first.RewardQuantity);
        Assert.Equal(6, second!.Shop.Materials.Single(material => material.Code == "weapon-fragment-t1").Quantity);
        Assert.Equal(0, second.Shop.Materials.Single(material => material.Code == "kobold-mine-token").Quantity);
        Assert.Empty(await test.Db.CharacterWeapons.ToListAsync());
    }

    [Fact]
    public async Task DungeonExchangeConsumesOneHundredTokensAndCreatesSoulImprint()
    {
        await using var test = await ShopTestContext.CreateAsync();
        test.Db.CharacterItemStacks.Add(new CharacterItemStack
        {
            CharacterId = test.Character.Id, ItemCode = "kobold-mine-token", Quantity = 100
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.ExchangeAsync(test.Token, new ExchangeDungeonWeaponRequest
        {
            CharacterId = test.Character.Id, OfferCode = "kobold-soul"
        });

        Assert.Null(error);
        Assert.Equal("深岩震核", result!.RewardDisplayName);
        Assert.Equal(0, result.Shop.Materials.Single(item => item.Code == "kobold-mine-token").Quantity);
        Assert.Equal("deep-core", (await test.Db.CharacterSoulImprints.SingleAsync()).SoulImprintCode);
    }

    private sealed class ShopTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private readonly DbContextOptions<GameDbContext> _options;

        private ShopTestContext(string path, DbContextOptions<GameDbContext> options, GameDbContext db,
            User user, Character character)
        {
            _path = path;
            _options = options;
            Db = db;
            User = user;
            Character = character;
            Service = CreateService(db);
        }

        public string Token => "shop-token";
        public GameDbContext Db { get; }
        public User User { get; }
        public Character Character { get; }
        public ShopService Service { get; }

        public static async Task<ShopTestContext> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-shop-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var db = new GameDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var user = new User { Id = 1, UserName = "shopper", PasswordHash = "x", ActiveCharacterId = 1 };
            var character = new Character { Id = 1, UserId = 1, Name = "Knight", Hp = 100, MaxHp = 100, Attack = 20, Gold = 100};
            db.AddRange(user, character, new UserLoginSession
            {
                UserId = 1, Token = "shop-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync();
            return new ShopTestContext(path, options, db, user, character);
        }

        public static ShopService CreateService(GameDbContext db)
        {
            var consumables = ConsumableTestFactory.Create();
            var weapons = new WeaponCatalog(Options.Create(new WeaponOptions
            {
                Skills = [new WeaponSkillDefinitionOptions
                {
                    Code = "weapon-attack", Name = "攻击", EffectType = WeaponSkillEffectType.AttackPercent,
                    PercentPerLevel = 2
                }],
                Items = [new WeaponTemplateOptions
                {
                    Code = "cinder-knife", Name = "余烬短剑", Element = ElementType.Fire,
                    Attack = 6, MaxHp = 18,
                    Skills = [new WeaponSkillGrantOptions { Code = "weapon-attack", Level = 2 }]
                }],
                StarterPacks = new Dictionary<string, List<string>> { ["knight"] = ["cinder-knife"] }
            }));
            var catalog = new ShopCatalog(Options.Create(new ShopOptions
            {
                Items =
                [
                    new ShopItemOptions { Kind = "Consumable", Code = "minor-healing-potion", Price = 10 },
                    new ShopItemOptions { Kind = "Weapon", Code = "cinder-knife", Price = 45 }
                ]
            }), consumables, weapons);
            var materials = new MaterialCatalog(Options.Create(new MaterialOptions
            {
                Items =
                [
                    new MaterialItemOptions
                    {
                        Code = "kobold-mine-token", Name = "矿洞徽记", Description = "测试副本材料。"
                    },
                    new MaterialItemOptions
                    {
                        Code = "weapon-fragment-t1", Name = "T1 武器碎片", Description = "测试强化材料。"
                    }
                ]
            }));
            var exchanges = new DungeonExchangeCatalog(Options.Create(new DungeonExchangeOptions
            {
                Offers = [new DungeonExchangeOfferOptions
                {
                    Code = "kobold-fire", DungeonCode = "kobold-mine", DungeonName = "狗头人矿洞",
                    CurrencyCode = "kobold-mine-token", Cost = 6, WeaponCode = "cinder-knife"
                }, new DungeonExchangeOfferOptions
                {
                    Code = "kobold-fragments", DungeonCode = "kobold-mine", DungeonName = "狗头人矿洞",
                    CurrencyCode = "kobold-mine-token", Cost = 1, RewardKind = "Material",
                    RewardCode = "weapon-fragment-t1", RewardQuantity = 3
                }, new DungeonExchangeOfferOptions
                {
                    Code = "kobold-soul", DungeonCode = "kobold-mine", DungeonName = "狗头人矿洞",
                    CurrencyCode = "kobold-mine-token", Cost = 100, RewardKind = "SoulImprint",
                    RewardCode = "deep-core"
                }]
            }), materials, weapons, CreateSoulImprints());
            return new ShopService(db, new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create()),
                catalog, consumables, weapons, materials, exchanges, CreateSoulImprints());
        }

        private static SoulImprintCatalog CreateSoulImprints() => new(Options.Create(new SoulImprintOptions
        {
            Items = [new SoulImprintDefinitionOptions
            {
                Code = "deep-core", Name = "深岩震核", Description = "测试魂印。",
                DungeonCode = "kobold-mine", Tier = 1, Element = ElementType.Earth,
                EffectType = SoulImprintEffectType.DamageArmorBreak, PowerPercent = 180,
                SecondaryPowerPercent = 20, DurationRounds = 3, InitialCooldownRounds = 3,
                CooldownRounds = 8, DismantleFragments = 25
            }]
        }));

        public GameDbContext CreateDbContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
