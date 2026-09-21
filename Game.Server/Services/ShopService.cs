using Game.Server.Data;
using Game.Shared.Dtos.Shop;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class ShopService(GameDbContext dbContext, UserService userService, ShopCatalog shopCatalog,
    ConsumableCatalog consumables, WeaponCatalog weapons)
{
    public async Task<(ShopResponse? Response, string? Error)> GetAsync(string? token)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        return error is null ? (await BuildResponseAsync(user!, character!), null) : (null, error);
    }

    public async Task<(ShopResponse? Response, string? Error)> PurchaseAsync(string? token, PurchaseShopItemRequest request)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (character!.Id != request.CharacterId) return (null, "ActiveCharacterChanged");
        var product = shopCatalog.Find(request.Code);
        if (product is null) return (null, "ProductNotFound");
        if (request.Quantity is < 1 or > 99 || product.Kind == "Weapon" && request.Quantity != 1)
            return (null, "InvalidQuantity");

        var cost = checked(product.Price * request.Quantity);
        if (user!.Gold < cost) return (null, "InsufficientGold");
        CharacterItemStack? stack = null;
        if (product.Kind == "Consumable")
        {
            stack = await dbContext.CharacterItemStacks.SingleOrDefaultAsync(item =>
                item.CharacterId == character.Id && item.ItemCode == product.Code);
            if (stack is not null && stack.Quantity > int.MaxValue - request.Quantity)
                return (null, "InventoryLimitReached");
        }
        user.Gold -= cost;
        user.Version++;
        character.Version++;

        if (product.Kind == "Consumable")
        {
            if (stack is null)
            {
                dbContext.CharacterItemStacks.Add(new CharacterItemStack
                {
                    CharacterId = character.Id, ItemCode = product.Code, Quantity = request.Quantity
                });
            }
            else
            {
                stack.Quantity += request.Quantity;
                stack.Version++;
            }
        }
        else
        {
            dbContext.CharacterWeapons.Add(weapons.CreateRewardSnapshot(product.Code).ToCharacterWeapon(character.Id));
        }

        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }
        return (await BuildResponseAsync(user, character), null);
    }

    private async Task<ShopResponse> BuildResponseAsync(User user, Character character)
    {
        var stocks = await dbContext.CharacterItemStacks.Where(item => item.CharacterId == character.Id).ToListAsync();
        var ownedWeapons = await dbContext.CharacterWeapons.Where(item => item.CharacterId == character.Id)
            .GroupBy(item => item.WeaponCode)
            .Select(group => new { Code = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Code, item => item.Count);

        return new ShopResponse
        {
            CharacterId = character.Id, CharacterName = character.Name, Gold = user.Gold,
            Items = shopCatalog.Items.Select(product =>
            {
                var consumable = product.Kind == "Consumable" ? consumables.FindItem(product.Code) : null;
                var weapon = product.Kind == "Weapon" ? weapons.FindItem(product.Code) : null;
                return new ShopItemResponse
                {
                    Code = product.Code, Kind = product.Kind, Price = product.Price,
                    Name = consumable?.Name ?? weapon?.Name ?? product.Code,
                    OwnedQuantity = consumable is not null
                        ? stocks.FirstOrDefault(item => item.ItemCode == product.Code)?.Quantity ?? 0
                        : ownedWeapons.GetValueOrDefault(product.Code),
                    HealAmount = consumable?.HealAmount, CooldownRounds = consumable?.CooldownRounds,
                    Element = weapon?.Element, Attack = weapon?.Attack, MaxHp = weapon?.MaxHp,
                    WeaponSkills = weapon?.Skills.Select(skill =>
                    {
                        var definition = weapons.FindSkill(skill.Code)!;
                        return new ShopWeaponSkillResponse
                        {
                            Name = definition.Name, Level = skill.Level,
                            Percent = definition.PercentPerLevel * skill.Level
                        };
                    }).ToList() ?? []
                };
            }).ToList()
        };
    }
}
