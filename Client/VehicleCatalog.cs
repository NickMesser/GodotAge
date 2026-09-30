#nullable enable

using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Client;

/// <summary>Kinematic tuning of a client-driven land vehicle (<c>vehicle_models</c>).</summary>
/// <param name="LinearInertia">Seconds to reach cruise speed per m/s: acceleration is 1 / value (0.4 gives the 2.5 m/s²
/// the real client's farm wagon showed, capture 2026-09-29).</param>
/// <param name="LinearDecelInertia">Same for slowing down after the throttle key is released (0.4 → 2.5 m/s², captured).</param>
/// <param name="RotationInertia">Yaw-rate ramp: the rate reaches <see cref="AngularVelocity"/> at angVel / value per second
/// (0.4 / 0.2 = 2 rad/s², captured as -0.07, -0.28, -0.40 rad/s over 0.2 s).</param>
/// <param name="AngularVelocity">Full-lock yaw rate in rad/s (0.4 for the farm wagon, captured angVel.z = ±0.40).</param>
public sealed record LandVehiclePhysics(float LinearInertia, float LinearDecelInertia, float RotationInertia,
    float RotationDecelInertia, float AngularVelocity, bool WheeledSimulation);

/// <summary>Helm tuning of a ship (<c>ship_models</c>); the zone simulates the hull, the client only requests.</summary>
public sealed record ShipPhysics(float Velocity, float ReverseVelocity, float SteerVelocity);

/// <summary>Content facts about one slave template that the client needs to summon, board and drive it.</summary>
public sealed record VehicleTemplate(
    uint SlaveId,
    string Name,
    int KindId,
    string KindKey,
    uint ModelId,
    float SpawnOffsetX,
    float SpawnOffsetY,
    float LengthY,
    float WidthX,
    bool Customizable,
    LandVehiclePhysics? Land,
    ShipPhysics? Ship,
    IReadOnlyList<uint> MountSkillIds,
    uint InteractionSkillId,
    IReadOnlyList<uint> MountSkillRowIds)
{
    /// <summary>AAEmu SlaveTemplate.IsABoat: the hull is simulated by the zone and driven with CSMoveUnit type 5.</summary>
    public bool IsBoat => KindId is 1 or 2 or 3 or 4 or 9 or 10;

    /// <summary>AAEmu SlaveTemplate.IsClientDrivenLandVehicle: the driver's client simulates it and sends type 2.</summary>
    public bool IsClientDrivenLand => KindId is 5 or 6 or 7;
}

/// <summary>
/// Read-only content lookups for slaves (vehicles, ships). Every value comes from the decrypted content database; the
/// class makes no gameplay decision.
/// </summary>
public sealed class VehicleCatalog
{
    private readonly string _connectionString;
    private readonly Dictionary<uint, VehicleTemplate?> _templates = [];
    private readonly Dictionary<uint, uint> _summonItems = [];
    private readonly Dictionary<uint, float> _skillRanges = [];

    public VehicleCatalog(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    /// <summary>The slave template an item summons (<c>item_summon_slaves</c>), or zero.</summary>
    public uint SlaveForSummonItem(uint itemTemplateId)
    {
        if (_summonItems.TryGetValue(itemTemplateId, out var cached))
            return cached;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT slave_id FROM item_summon_slaves WHERE item_id = $item LIMIT 1";
        command.Parameters.AddWithValue("$item", itemTemplateId);
        var value = command.ExecuteScalar();
        return _summonItems[itemTemplateId] = value is null or DBNull ? 0 : Convert.ToUInt32(value);
    }

    /// <summary><c>skills.target_type_id</c> (0 self, 5 any unit, ...), -1 when unknown.</summary>
    public int SkillTargetType(uint skillId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT target_type_id FROM skills WHERE id = $id";
        command.Parameters.AddWithValue("$id", skillId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? -1 : Convert.ToInt32(value);
    }

    /// <summary><c>skills.max_range</c> in metres (0 when unknown).</summary>
    public float SkillMaxRange(uint skillId)
    {
        if (_skillRanges.TryGetValue(skillId, out var cached))
            return cached;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT max_range FROM skills WHERE id = $id";
        command.Parameters.AddWithValue("$id", skillId);
        var value = command.ExecuteScalar();
        return _skillRanges[skillId] = value is null or DBNull ? 0 : Convert.ToSingle(value);
    }

    public VehicleTemplate? Get(uint slaveId)
    {
        if (_templates.TryGetValue(slaveId, out var cached))
            return cached;
        try
        {
            return _templates[slaveId] = Load(slaveId);
        }
        catch (SqliteException)
        {
            return _templates[slaveId] = null;
        }
    }

    private VehicleTemplate? Load(uint slaveId)
    {
        using var connection = Open();
        string name;
        int kind;
        uint modelId;
        float spawnX, spawnY, sizeX, sizeY;
        bool customizable;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name, slave_kind_id, model_id, spawn_x_offset, spawn_y_offset, obb_size_x, obb_size_y,
                       customizable, slave_customizing_id
                  FROM slaves WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", slaveId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            name = reader.IsDBNull(0) ? "" : reader.GetString(0);
            kind = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            modelId = reader.IsDBNull(2) ? 0 : (uint)reader.GetInt64(2);
            spawnX = reader.IsDBNull(3) ? 0 : reader.GetFloat(3);
            spawnY = reader.IsDBNull(4) ? 0 : reader.GetFloat(4);
            sizeX = reader.IsDBNull(5) ? 0 : reader.GetFloat(5);
            sizeY = reader.IsDBNull(6) ? 0 : reader.GetFloat(6);
            customizable = !reader.IsDBNull(7) && reader.GetString(7) == "t" && !reader.IsDBNull(8);
        }

        LandVehiclePhysics? land = null;
        ShipPhysics? ship = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT m.sub_type,
                       vm.lin_inertia, vm.lin_deaccel_inertia, vm.rot_inertia, vm.rot_deaccel_inertia, vm.angVel,
                       vm.use_wheeled_vehicle_simulation,
                       sm.velocity, sm.reverse_velocity, sm.steer_vel
                  FROM models m
                  LEFT JOIN vehicle_models vm ON m.sub_type = 'VehicleModel' AND vm.id = m.sub_id
                  LEFT JOIN ship_models sm ON m.sub_type = 'ShipModel' AND sm.id = m.sub_id
                 WHERE m.id = $model
                """;
            command.Parameters.AddWithValue("$model", modelId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var subType = reader.IsDBNull(0) ? "" : reader.GetString(0);
                float F(int i) => reader.IsDBNull(i) ? 0 : reader.GetFloat(i);
                if (subType == "VehicleModel")
                    land = new LandVehiclePhysics(F(1), F(2), F(3), F(4), F(5),
                        !reader.IsDBNull(6) && reader.GetString(6) == "t");
                else if (subType == "ShipModel")
                    ship = new ShipPhysics(F(7), F(8), F(9));
            }
        }

        var mountSkills = new List<uint>();
        var mountSkillRows = new List<uint>();
        using (var command = connection.CreateCommand())
        {
            // The original mode bar lists these in slave_mount_skills row order (captured slots 1-6 for the farm wagon:
            // 20704, 20291, 15622, 15450, 15546, 35166 = rows 181..185, 1477).
            command.CommandText = """
                SELECT ms.skill_id, ms.id FROM slave_mount_skills sms
                  JOIN mount_skills ms ON ms.id = sms.mount_skill_id
                 WHERE sms.slave_id = $id ORDER BY sms.id
                """;
            command.Parameters.AddWithValue("$id", slaveId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0))
                {
                    mountSkills.Add((uint)reader.GetInt64(0));
                    mountSkillRows.Add((uint)reader.GetInt64(1));
                }
        }

        uint interactionSkill = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT skill_id FROM slave_interaction_skills
                 WHERE slave_id = $id AND enable = 't' AND require_slot_id = 0 ORDER BY id LIMIT 1
                """;
            command.Parameters.AddWithValue("$id", slaveId);
            var value = command.ExecuteScalar();
            if (value is not null and not DBNull)
                interactionSkill = Convert.ToUInt32(value);
        }

        return new VehicleTemplate(slaveId, name, kind, KindKey(kind), modelId, spawnX, spawnY, sizeY, sizeX,
            customizable, land, ship, mountSkills, interactionSkill, mountSkillRows);
    }

    private static string KindKey(int kind) => kind switch
    {
        1 => "big_sailing_ship",
        2 => "small_sailing_ship",
        3 => "speedboat",
        4 => "boat",
        5 => "tank",
        6 => "machine",
        7 => "siege_weapon",
        8 => "slave_equipment",
        9 => "fishboat",
        10 => "merchant_ship",
        11 => "leviathan",
        _ => "",
    };

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
