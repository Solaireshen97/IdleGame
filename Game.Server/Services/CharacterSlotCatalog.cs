using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class CharacterSlotCatalog
{
    public static CharacterSlotCatalog Default { get; } = new(new CharacterSlotOptions
    {
        InitialSlots = 2,
        MaximumSlots = 5,
        UnlockCosts = [500, 1500, 4000]
    });

    public CharacterSlotCatalog(IOptions<CharacterSlotOptions> options) : this(options.Value)
    {
    }

    private CharacterSlotCatalog(CharacterSlotOptions settings)
    {
        if (settings.InitialSlots < 1 || settings.MaximumSlots < settings.InitialSlots ||
            settings.MaximumSlots > 5 ||
            settings.UnlockCosts.Count != settings.MaximumSlots - settings.InitialSlots ||
            settings.UnlockCosts.Any(cost => cost <= 0) ||
            settings.UnlockCosts.Zip(settings.UnlockCosts.Skip(1)).Any(pair => pair.Second <= pair.First))
            throw new InvalidOperationException(
                "Character slot settings must define 1-5 slots and a strictly increasing positive cost for every unlock.");

        InitialSlots = settings.InitialSlots;
        MaximumSlots = settings.MaximumSlots;
        UnlockCosts = settings.UnlockCosts.ToArray();
    }

    public int InitialSlots { get; }
    public int MaximumSlots { get; }
    public IReadOnlyList<int> UnlockCosts { get; }

    public int? GetNextUnlockCost(int unlockedSlots)
    {
        if (unlockedSlots >= MaximumSlots) return null;
        var normalizedSlots = Math.Max(InitialSlots, unlockedSlots);
        return UnlockCosts[normalizedSlots - InitialSlots];
    }
}
