namespace Game.Client.Services;

public sealed class AccountBalanceState : IDisposable
{
    private readonly ActiveCharacterState _characters;
    private readonly ApiService _api;
    public int? CharacterId { get; private set; }
    public int? Gold { get; private set; }
    public event Action? Changed;

    public AccountBalanceState(ActiveCharacterState characters, ApiService api)
    {
        _characters = characters;
        _api = api;
        characters.Changed += CharacterChanged;
        api.ContextChanged += Clear;
    }

    public void Update(int characterId, int gold)
    {
        if (_characters.Current?.CharacterId != characterId) return;
        if (CharacterId == characterId && Gold == gold) return;
        CharacterId = characterId;
        Gold = gold;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    private void CharacterChanged()
    {
        if (CharacterId != _characters.Current?.CharacterId) Clear();
    }
    private void Clear()
    {
        if (CharacterId is null && Gold is null) return;
        CharacterId = null;
        Gold = null;
        Changed?.Invoke();
    }
    public void Dispose()
    {
        _characters.Changed -= CharacterChanged;
        _api.ContextChanged -= Clear;
    }
}
