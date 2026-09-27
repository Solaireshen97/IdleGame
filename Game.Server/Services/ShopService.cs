using Game.Server.Data;
using Game.Shared.Dtos.Shop;
using Game.Shared.Models;
using Game.Shared.Enums;
using Game.Shared;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class ShopService(GameDbContext dbContext, UserService userService, ShopCatalog shopCatalog,
    ConsumableCatalog consumables, WeaponCatalog weapons, MaterialCatalog materials,
    DungeonExchangeCatalog dungeonExchanges, SoulImprintCatalog? soulImprints = null,
    CharacterSlotCatalog? characterSlotCatalog = null, ProductionService? production = null,
    PlantingCatalog? plants = null, WorldCatalog? world = null)
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
        if (!Guid.TryParse(request.RequestId, out var requestGuid) || requestGuid == Guid.Empty)
            return (null, "InvalidRequestId");
        var requestId = requestGuid.ToString("N");
        var product = shopCatalog.Find(request.Code);
        if (product is null) return (null, "ProductNotFound");
        if (request.Quantity is < 1 or > 99 || product.Kind == "Weapon" && request.Quantity != 1)
            return (null, "InvalidQuantity");
        if (product.Kind == "Seed" && plants?.FindSeed(product.Code) is not { IsRare: false })
            return (null, "ProductNotFound");
        var fingerprint = $"{product.Code.ToLowerInvariant()}:{request.Quantity}";
        var previous = await dbContext.LogisticsRequests.AsNoTracking().SingleOrDefaultAsync(item =>
            item.CharacterId == character.Id && item.RequestId == requestId);
        if (previous is not null)
        {
            if (previous.Kind != "ShopPurchase" || previous.Fingerprint != fingerprint)
                return (null, "RequestIdReused");
            dbContext.ChangeTracker.Clear();
            (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
            return error is null ? (await BuildResponseAsync(user!, character!), null) : (null, error);
        }
        if (!await IsUnlockedAsync(character, product)) return (null, "ProductLocked");
        var cost = checked(product.Price * request.Quantity);
        if (character.Gold < cost) return (null, "InsufficientGold");
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            if (production is not null) await production.SettleCharacterTrackedAsync(character.Id, now);
            var stack = product.Kind == "Weapon" ? null :
                dbContext.CharacterItemStacks.Local.FirstOrDefault(item => item.CharacterId == character.Id && item.ItemCode == product.Code)
                ?? await dbContext.CharacterItemStacks.SingleOrDefaultAsync(item => item.CharacterId == character.Id && item.ItemCode == product.Code);
            if (stack is not null && stack.Quantity > int.MaxValue - request.Quantity)
            {
                await transaction.RollbackAsync();
                dbContext.ChangeTracker.Clear();
                return (null, "InventoryLimitReached");
            }
            character.Gold -= cost;
            character.Version++;
            if (product.Kind == "Weapon")
                dbContext.CharacterWeapons.Add((weapons.CreateRewardSnapshot(product.Code) with { Origin = WeaponOrigin.Shop }).ToCharacterWeapon(character.Id));
            else if (stack is null)
                dbContext.CharacterItemStacks.Add(new CharacterItemStack { CharacterId = character.Id, ItemCode = product.Code, Quantity = request.Quantity });
            else
            {
                stack.Quantity += request.Quantity;
                stack.Version++;
            }
            dbContext.LogisticsRequests.Add(new LogisticsRequest { CharacterId = character.Id, RequestId = requestId,
                Kind = "ShopPurchase", Fingerprint = fingerprint, CompletedAtUtc = now });
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
            return (null, "ConcurrencyConflict");
        }
        catch
        {
            await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
            throw;
        }
        return (await BuildResponseAsync(user!, character), null);
    }

    private async Task<bool> IsUnlockedAsync(Character character, Game.Server.Configuration.ShopItemOptions product)
    {
        if (character.Level < product.MinimumCharacterLevel) return false;
        if (product.UnlockKind is null) return true;
        var targets = product.AlternativeUnlockTargetCodes.Append(product.UnlockTargetCode).ToList();
        return await dbContext.CharacterBattleMilestones.AnyAsync(item => item.CharacterId == character.Id &&
            item.Kind == product.UnlockKind && targets.Contains(item.TargetCode) && item.Count >= product.RequiredCount);
    }

    private string UnlockDescription(Game.Server.Configuration.ShopItemOptions product) =>
        product.UnlockKind is null ? (product.MinimumCharacterLevel > 1 ? $"角色等级达到 {product.MinimumCharacterLevel}" : "初始开放") :
        $"角色等级达到 {product.MinimumCharacterLevel}，完成 {string.Join(" / ", product.AlternativeUnlockTargetCodes.Prepend(product.UnlockTargetCode).Select(code => world?.Dungeons.FirstOrDefault(item => item.Code == code)?.Name ?? code))} {(product.UnlockKind == "MonsterKill" ? "击杀" : "通关")} {product.RequiredCount} 次";

    public async Task<(DungeonExchangeResultResponse? Response, string? Error)> ExchangeAsync(
        string? token, ExchangeDungeonWeaponRequest request)
    {
        var (user, character, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (character!.Id != request.CharacterId) return (null, "ActiveCharacterChanged");
        var offer = dungeonExchanges.Find(request.OfferCode);
        if (offer is null) return (null, "ExchangeOfferNotFound");
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        if (production is not null) await production.SettleCharacterTrackedAsync(character.Id, DateTime.UtcNow);
        var currency = await dbContext.CharacterItemStacks.SingleOrDefaultAsync(stack =>
            stack.CharacterId == character.Id && stack.ItemCode == offer.CurrencyCode);
        if (currency is null || currency.Quantity < offer.Cost)
        {
            await transaction.RollbackAsync();
            dbContext.ChangeTracker.Clear();
            return (null, "InsufficientDungeonCurrency");
        }

        var rewardCode = offer.EffectiveRewardCode;
        WeaponRewardSnapshot? weaponSnapshot = null;
        CharacterItemStack? materialStack = null;
        CharacterSoulImprint? soulImprint = null;
        if (string.Equals(offer.RewardKind, "Weapon", StringComparison.OrdinalIgnoreCase))
        {
            weaponSnapshot = weapons.CreateDropSnapshot(rewardCode) with { Origin = WeaponOrigin.Exchange };
        }
        else if (string.Equals(offer.RewardKind, "Material", StringComparison.OrdinalIgnoreCase))
        {
            materialStack = await dbContext.CharacterItemStacks.SingleOrDefaultAsync(stack =>
                stack.CharacterId == character.Id && stack.ItemCode == rewardCode);
            if (materialStack is not null && materialStack.Quantity > int.MaxValue - offer.RewardQuantity)
            {
                await transaction.RollbackAsync();
                dbContext.ChangeTracker.Clear();
                return (null, "InventoryLimitReached");
            }
        }
        else
        {
            soulImprint = soulImprints!.Materialize(rewardCode, character.Id);
        }

        currency.Quantity -= offer.Cost;
        currency.Version++;
        character.Version++;
        if (weaponSnapshot is not null)
        {
            dbContext.CharacterWeapons.Add(weaponSnapshot.ToCharacterWeapon(character.Id));
        }
        else if (soulImprint is not null)
        {
            dbContext.CharacterSoulImprints.Add(soulImprint);
        }
        else if (materialStack is null)
        {
            dbContext.CharacterItemStacks.Add(new CharacterItemStack
            {
                CharacterId = character.Id, ItemCode = rewardCode, Quantity = offer.RewardQuantity
            });
        }
        else
        {
            materialStack.Quantity += offer.RewardQuantity;
            materialStack.Version++;
        }
        try
        {
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException)
        {
            return (null, "ConcurrencyConflict");
        }

        var rewardName = weaponSnapshot?.DisplayName ?? soulImprints?.Find(rewardCode)?.Name ??
            materials.FindItem(rewardCode)!.Name;
        return (new DungeonExchangeResultResponse
        {
            Shop = await BuildResponseAsync(user!, character),
            RewardDisplayName = rewardName,
            RewardQuantity = offer.RewardQuantity,
            WeaponDisplayName = weaponSnapshot?.DisplayName ?? string.Empty
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
        var ownedSoulImprints = await dbContext.CharacterSoulImprints.Where(item => item.CharacterId == character.Id)
            .GroupBy(item => item.SoulImprintCode)
            .Select(group => new { Code = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Code, item => item.Count);
        var characterCount = await dbContext.Characters.CountAsync(item => item.UserId == user.Id);

        var unlocked = new Dictionary<string, bool>();
        foreach (var product in shopCatalog.Items) unlocked[product.Code] = await IsUnlockedAsync(character, product);
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
                    Name = consumable?.Name ?? weapon?.Name ?? materials.FindItem(product.Code)?.Name ?? product.Code,
                    Description = consumable is not null ? ConsumableCatalog.Description(consumable, character.Level) : materials.FindItem(product.Code)?.Description ?? string.Empty,
                    IsUnlocked = unlocked[product.Code], UnlockDescription = UnlockDescription(product),
                    OwnedQuantity = product.Kind != "Weapon"
                        ? stocks.FirstOrDefault(item => item.ItemCode == product.Code)?.Quantity ?? 0
                        : ownedWeapons.GetValueOrDefault(product.Code),
                    HealAmount = consumable is { Kind: "Healing" } ? ConsumableCatalog.HealAmountFor(consumable, TalentRules.EffectiveMaxHp(character), character.Level) : null,
                    AttackPercent = consumable is { Kind: "OperationPotion" } ? consumable.AttackPercent : null,
                    CooldownRounds = consumable is { Kind: "Healing" } ? consumable.CooldownRounds : null,
                    Element = weapon?.Element, Attack = weapon?.Attack, MaxHp = weapon?.MaxHp,
                    WeaponSkills = BuildWeaponSkills(weapon)
                };
            }).ToList(),
            DungeonExchangeOffers = dungeonExchanges.Offers.Select(offer =>
            {
                var rewardCode = offer.EffectiveRewardCode;
                var isWeapon = string.Equals(offer.RewardKind, "Weapon", StringComparison.OrdinalIgnoreCase);
                var weapon = isWeapon ? weapons.FindItem(rewardCode) : null;
                var soulImprint = string.Equals(offer.RewardKind, "SoulImprint", StringComparison.OrdinalIgnoreCase)
                    ? soulImprints?.Find(rewardCode) : null;
                var material = isWeapon || soulImprint is not null ? null : materials.FindItem(rewardCode);
                var currency = materials.FindItem(offer.CurrencyCode)!;
                return new DungeonExchangeOfferResponse
                {
                    Code = offer.Code, DungeonCode = offer.DungeonCode, DungeonName = offer.DungeonName,
                    CurrencyCode = offer.CurrencyCode, CurrencyName = currency.Name, Cost = offer.Cost,
                    RewardKind = offer.RewardKind, RewardCode = rewardCode,
                    RewardName = weapon?.Name ?? soulImprint?.Name ?? material!.Name,
                    RewardDescription = soulImprint?.Description ?? material?.Description ?? string.Empty,
                    RewardQuantity = offer.RewardQuantity,
                    WeaponCode = weapon?.Code ?? string.Empty, WeaponName = weapon?.Name ?? string.Empty,
                    Element = weapon?.Element, Attack = weapon?.Attack, MaxHp = weapon?.MaxHp,
                    InitialCooldownRounds = soulImprint?.InitialCooldownRounds,
                    CooldownRounds = soulImprint?.CooldownRounds,
                    OwnedQuantity = weapon is not null
                        ? ownedWeapons.GetValueOrDefault(weapon.Code)
                        : soulImprint is not null
                            ? ownedSoulImprints.GetValueOrDefault(soulImprint.Code)
                        : stocks.FirstOrDefault(stack => stack.ItemCode == rewardCode)?.Quantity ?? 0,
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
