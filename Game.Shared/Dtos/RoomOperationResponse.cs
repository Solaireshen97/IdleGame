using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public class SubmitRoomOperationRequest
{
    public Game.Shared.Dtos.Formations.LoadoutSelection? LoadoutSelection { get; set; }
    public string? RequestId { get; set; }
    public RoomOperationKind Kind { get; set; }
    public int SlotIndex { get; set; }
    public int? CharacterId { get; set; }
}

public class RoomOperationResponse
{
    public string? SourceFormationName { get; set; }
    public int? SourceFormationVersion { get; set; }
    public int Id { get; set; }
    public RoomOperationKind Kind { get; set; }
    public int SlotIndex { get; set; }
    public string CharacterName { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public string Message { get; set; } = "";
}
