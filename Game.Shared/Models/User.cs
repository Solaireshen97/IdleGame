namespace Game.Shared.Models;

public class User
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int? ActiveCharacterId { get; set; }
    public int Gold { get; set; }
    public int Version { get; set; }
}
