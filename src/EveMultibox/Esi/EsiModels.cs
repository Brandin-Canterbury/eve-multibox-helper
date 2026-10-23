namespace EveMultibox.Esi;

// ESI response shapes (snake_case JSON maps onto these via EsiClient.Json).

public sealed record EsiLocation(int SolarSystemId, long? StationId, long? StructureId);

public sealed record EsiShip(int ShipTypeId, long ShipItemId, string ShipName);

public sealed record EsiOnline(bool Online);

public sealed record EsiSkill(int SkillId, int TrainedSkillLevel, int ActiveSkillLevel);

public sealed record EsiSkills(List<EsiSkill> Skills, long TotalSp);

public sealed record EsiStanding(int FromId, string FromType, double Standing);

public sealed record EsiStation(long StationId, string Name, int SystemId);
