using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Warehouse;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class WarehouseServiceTests
{
    [Fact]
    public async Task TwoCharactersShareWarehouseWithoutSharingTheirBags()
    {
        await using var test = await WarehouseTestContext.CreateAsync();
        var deposit = new WarehouseTransferRequest
        {
            CharacterId = test.First.Id, ItemCode = "minor-healing-potion",
            Direction = "Deposit", Quantity = 6, RequestId = Guid.NewGuid()
        };
        var (afterDeposit, depositError) = await test.Service.TransferAsync(test.Token, deposit);
        Assert.Null(depositError);
        Assert.Equal((4, 6), Quantities(afterDeposit!, "minor-healing-potion"));

        test.Owner.ActiveCharacterId = test.Second.Id;
        test.Owner.Version++;
        await test.Db.SaveChangesAsync();
        var withdrawal = new WarehouseTransferRequest
        {
            CharacterId = test.Second.Id, ItemCode = "minor-healing-potion",
            Direction = "Withdraw", Quantity = 2, RequestId = Guid.NewGuid()
        };
        var (afterWithdrawal, withdrawalError) = await test.Service.TransferAsync(test.Token, withdrawal);
        Assert.Null(withdrawalError);
        Assert.Equal((2, 4), Quantities(afterWithdrawal!, "minor-healing-potion"));
        Assert.Equal(4, (await test.Db.CharacterItemStacks.SingleAsync(stack =>
            stack.CharacterId == test.First.Id && stack.ItemCode == "minor-healing-potion")).Quantity);

        var (replay, replayError) = await test.Service.TransferAsync(test.Token, withdrawal);
        Assert.Null(replayError);
        Assert.Equal((2, 4), Quantities(replay!, "minor-healing-potion"));
        Assert.Equal(2, await test.Db.WarehouseTransferRecords.CountAsync());
        withdrawal.Quantity = 3;
        var (_, reusedError) = await test.Service.TransferAsync(test.Token, withdrawal);
        Assert.Equal("RequestIdReused", reusedError);
    }

    [Fact]
    public async Task BoundItemsAndInvalidAmountsAreRejectedByServer()
    {
        await using var test = await WarehouseTestContext.CreateAsync();
        var (view, viewError) = await test.Service.GetAsync(test.Token);
        Assert.Null(viewError);
        Assert.False(view!.Items.Single(item => item.Code == "kobold-mine-token").CanTransfer);
        var (_, boundError) = await test.Service.TransferAsync(test.Token, new WarehouseTransferRequest
        {
            CharacterId = test.First.Id, ItemCode = "kobold-mine-token",
            Direction = "Deposit", Quantity = 1, RequestId = Guid.NewGuid()
        });
        Assert.Equal("ItemBound", boundError);
        var (_, invalidError) = await test.Service.TransferAsync(test.Token, new WarehouseTransferRequest
        {
            CharacterId = test.First.Id, ItemCode = "minor-healing-potion",
            Direction = "Deposit", Quantity = -1, RequestId = Guid.NewGuid()
        });
        Assert.Equal("InvalidQuantity", invalidError);
        var (_, shortageError) = await test.Service.TransferAsync(test.Token, new WarehouseTransferRequest
        {
            CharacterId = test.First.Id, ItemCode = "minor-healing-potion",
            Direction = "Deposit", Quantity = 11, RequestId = Guid.NewGuid()
        });
        Assert.Equal("InsufficientCharacterItems", shortageError);
        Assert.False(await test.Db.UserWarehouseStacks.AnyAsync());
        Assert.False(await test.Db.WarehouseTransferRecords.AnyAsync());
    }

    [Fact]
    public async Task DifferentAccountCannotReadOrWithdrawWarehouseStock()
    {
        await using var test = await WarehouseTestContext.CreateAsync();
        await test.Service.TransferAsync(test.Token, new WarehouseTransferRequest
        {
            CharacterId = test.First.Id, ItemCode = "minor-healing-potion",
            Direction = "Deposit", Quantity = 5, RequestId = Guid.NewGuid()
        });
        var stranger = new User { Id = 2, UserName = "stranger", PasswordHash = "x", ActiveCharacterId = 3 };
        var strangerCharacter = new Character { Id = 3, UserId = 2, Name = "Other", Hp = 100, MaxHp = 100, Attack = 20 };
        test.Db.AddRange(stranger, strangerCharacter, new UserLoginSession
        {
            UserId = 2, Token = "stranger-token", CreatedAt = DateTime.UtcNow,
            ExpireAt = DateTime.UtcNow.AddDays(1)
        });
        await test.Db.SaveChangesAsync();
        var (view, error) = await test.Service.GetAsync("stranger-token");
        Assert.Null(error);
        Assert.Equal(0, view!.Items.Single(item => item.Code == "minor-healing-potion").WarehouseQuantity);
        var (_, withdrawError) = await test.Service.TransferAsync("stranger-token", new WarehouseTransferRequest
        {
            CharacterId = 3, ItemCode = "minor-healing-potion", Direction = "Withdraw",
            Quantity = 1, RequestId = Guid.NewGuid()
        });
        Assert.Equal("InsufficientWarehouseItems", withdrawError);
        var (_, wrongCharacterError) = await test.Service.TransferAsync("stranger-token", new WarehouseTransferRequest
        {
            CharacterId = test.First.Id, ItemCode = "minor-healing-potion", Direction = "Withdraw",
            Quantity = 1, RequestId = Guid.NewGuid()
        });
        Assert.Equal("ActiveCharacterChanged", wrongCharacterError);
    }

    private static (int Character, int Warehouse) Quantities(WarehouseResponse response, string code)
    {
        var item = response.Items.Single(item => item.Code == code);
        return (item.CharacterQuantity, item.WarehouseQuantity);
    }

    private sealed class WarehouseTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private WarehouseTestContext(string path, GameDbContext db, User owner, Character first, Character second)
        {
            _path = path;
            Db = db;
            Owner = owner;
            First = first;
            Second = second;
            var progression = ProgressionTestFactory.Create();
            Service = new WarehouseService(db,
                new UserService(db, progression, SkillTestFactory.Create()),
                new ConsumableCatalog(Options.Create(new ConsumableOptions
                {
                    Items = [new ConsumableItemOptions
                    {
                        Code = "minor-healing-potion", Name = "小型治疗药水",
                        HealAmount = 20, CooldownGroup = "healing", CooldownRounds = 3,
                        CanStoreInWarehouse = true
                    }]
                })),
                new MaterialCatalog(Options.Create(new MaterialOptions
                {
                    Items = [new MaterialItemOptions
                    {
                        Code = "kobold-mine-token", Name = "矿洞徽记",
                        Description = "角色绑定", CanStoreInWarehouse = false
                    }]
                })));
        }

        public string Token => "owner-token";
        public GameDbContext Db { get; }
        public User Owner { get; }
        public Character First { get; }
        public Character Second { get; }
        public WarehouseService Service { get; }

        public static async Task<WarehouseTestContext> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-warehouse-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var owner = new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 };
            var first = new Character { Id = 1, UserId = 1, Name = "Fighter", Hp = 100, MaxHp = 100, Attack = 20 };
            var second = new Character { Id = 2, UserId = 1, Name = "Healer", Hp = 100, MaxHp = 100, Attack = 20 };
            db.AddRange(owner, first, second,
                new CharacterItemStack { CharacterId = 1, ItemCode = "minor-healing-potion", Quantity = 10 },
                new CharacterItemStack { CharacterId = 1, ItemCode = "kobold-mine-token", Quantity = 5 },
                new UserLoginSession
                {
                    UserId = 1, Token = "owner-token", CreatedAt = DateTime.UtcNow,
                    ExpireAt = DateTime.UtcNow.AddDays(1)
                });
            await db.SaveChangesAsync();
            return new WarehouseTestContext(path, db, owner, first, second);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
