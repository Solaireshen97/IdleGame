using Game.Shared.Dtos.Characters;

namespace Game.Client.Services;

public sealed class ActiveCharacterState(ApiService apiService)
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public CurrentCharacterResponse? Current { get; private set; }
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            var updated = await apiService.GetCurrentCharacterAsync();
            if (SameCharacter(Current, updated)) return;
            Current = updated;
            Changed?.Invoke();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Clear()
    {
        if (Current is null) return;
        Current = null;
        Changed?.Invoke();
    }

    private static bool SameCharacter(CurrentCharacterResponse? left, CurrentCharacterResponse? right) =>
        left is null ? right is null : right is not null &&
        left.CharacterId == right.CharacterId && left.Name == right.Name &&
        left.Hp == right.Hp && left.MaxHp == right.MaxHp &&
        left.Attack == right.Attack && left.Defense == right.Defense &&
        left.Level == right.Level && left.Experience == right.Experience &&
        left.TalentPoints == right.TalentPoints;
}
