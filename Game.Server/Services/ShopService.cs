using Game.Server.Data;
using Game.Shared.Dtos.Shop;
using Game.Shared.Models;
using Game.Shared.Enums;
using Game.Shared;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class ShopService(GameDbContext dbContext, UserService userService, ShopCatalog shopCatalog,
    ConsumableCatalog consumables, WeaponCatalog weapons, MaterialCatalog materials,
    DungeonExchangeCatalog dungeonExchanges, CharacterSlotCatalog? characterSlotCatalog = null)
{
    private CharacterSlotCatalog CharacterSlots => characterSlotCatalog ?? CharacterSlotCatalog.Default;

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
        if (character.Gold < cost) return (null, "InsufficientGold");
        CharacterItemStack? stack = null;
        if (product.Kind == "Consumable")
        {
            stack = await dbContext.CharacterItemStacks.SingleOrDefaultAsync(item =>
                item.CharacterId == character.Id && item.ItemCode == product.Code);
            if (stack is not null && stack.Quantity > int.MaxValue - request.Quantity)
                return (null, "InventoryLimitReached");
        }
        character.Gold -= cost;
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
            dbContext.CharacterWeapons.Add((weapons.CreateRewardSnapshot(product.Code) with { Origin = WeaponOrigin.Shop }).ToCharacterWeapon(character.Id));
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

    public async Task<(DungeonExchangeResultResponse? Response, string? Error)> ExchangeAsync(
        string? token, ExchangeDungeonWeaponRequest request)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (character!.Id != request.CharacterId) return (null, "ActiveCharacterChanged");
        var offer = dungeonExchanges.Find(request.OfferCode);
        if (offer is null) return (null, "ExchangeOfferNotFound");
        var currency = await dbContext.CharacterItemStacks.SingleOrDefaultAsync(stack =>
            stack.CharacterId == character.Id && stack.ItemCode == offer.CurrencyCode);
        if (currency is null || currency.Quantity < offer.Cost) return (null, "InsufficientDungeonCurrency");

        var snapshot = weapons.CreateDropSnapshot(offer.WeaponCode) with { Origin = WeaponOrigin.Exchange };
        currency.Quantity -= offer.Cost;
        currency.Version++;
        character.Version++;
        dbContext.CharacterWeapons.Add(snapshot.ToCharacterWeapon(character.Id));
        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }

        return (new DungeonExchangeResultResponse
        {
            Shop = await BuildResponseAsync(user!, character),
            WeaponDisplayName = snapshot.DisplayName
        }, null);
    }

    public async Task<(ShopResponse? Response, string? Error)> PurchaseCharacterSlotAsync(string? token)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);

        var cost = CharacterSlots.GetNextUnlockCost(user!.CharacterSlotLimit);
        if (cost is null) return (null, "MaximumCharacterSlotsReached");
        if (character!.Gold < cost.Value) return (null, "InsufficientGold");

        character.Gold -= cost.Value;
        character.Version++;
        user.CharacterSlotLimit++;
        user.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }

        return (await BuildResponseAsync(user, character!), null);
    }

    private async Task<ShopResponse> BuildResponseAsync(User user, Character character)
    {
        var stocks = await dbContext.CharacterItemStacks.Where(item => item.CharacterId == character.Id).ToListAsync();
        var ownedWeapons = await dbContext.CharacterWeapons.Where(item => item.CharacterId == character.Id)
            .GroupBy(item => item.WeaponCode)
            .Select(group => new { Code = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Code, item => item.Count);
        var characterCount = await dbContext.Characters.CountAsync(item => item.UserId == user.Id);

        return new ShopResponse
        {
            CharacterId = character.Id, CharacterName = character.Name, Gold = character.Gold,
            CharacterCount = characterCount,
            CharacterSlotLimit = user.CharacterSlotLimit,
            MaximumCharacterSlots = CharacterSlots.MaximumSlots,
            NextCharacterSlotCost = CharacterSlots.GetNextUnlockCost(user.CharacterSlotLimit),
            Materials = materials.Items.Select(material => new ShopMaterialResponse
            {
                Code = material.Code, Name = material.Name, Description = material.Description,
                Quantity = stocks.FirstOrDefault(stack => stack.ItemCode == material.Code)?.Quantity ?? 0
            }).ToList(),
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
                    HealAmount = consumable is null ? null : ConsumableCatalog.HealAmountFor(consumable, TalentRules.EffectiveMaxHp(character)), CooldownRounds = consumable?.CooldownRounds,
                    Element = weapon?.Element, Attack = weapon?.Attack, MaxHp = weapon?.MaxHp,
                    WeaponSkills = BuildWeaponSkills(weapon)
                };
            }).ToList(),
            DungeonExchangeOffers = dungeonExchanges.Offers.Select(offer =>
            {
                var weapon = weapons.FindItem(offer.WeaponCode)!;
                var currency = materials.FindItem(offer.CurrencyCode)!;
                return new DungeonExchangeOfferResponse
                {
                    Code = offer.Code, DungeonCode = offer.DungeonCode, DungeonName = offer.DungeonName,
                    CurrencyCode = offer.CurrencyCode, CurrencyName = currency.Name, Cost = offer.Cost,
                    WeaponCode = weapon.Code, WeaponName = weapon.Name, Element = weapon.Element,
                    Attack = weapon.Attack, MaxHp = weapon.MaxHp,
                    OwnedQuantity = ownedWeapons.GetValueOrDefault(weapon.Code),
                    WeaponSkills = BuildWeaponSkills(weapon)
                };
            }).ToList()
        };
    }

    private List<ShopWeaponSkillResponse> BuildWeaponSkills(Game.Server.Configuration.WeaponTemplateOptions? weapon) =>
        weapon?.Skills.Select(skill =>
        {
            var definition = weapons.FindSkill(skill.Code)!;
            return new ShopWeaponSkillResponse
            {
                Name = definition.Name, Level = skill.Level,
                Percent = weapons.CalculateSkillPercent(definition, skill.Level),
                Description = weapons.DescribeSkill(definition, skill.Level)
            };
        }).ToList() ?? [];
}
