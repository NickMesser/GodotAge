#nullable enable
using System.Globalization;
using AAEmu.GodotViewer.Data;
using Godot;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Effects;

/// <summary>
/// Non-interactive particle capture scene. Run this scene with a rendering-capable Godot display driver;
/// it needs no input or game server and exits after writing every requested frame.
/// Usage: godot --path viewer_copy res://Effects/ParticleSkillPreview.tscn --
/// --particle-preview-skills=10748,10664 --particle-preview-times=.3,.8,1.5 --particle-preview-output=preview
/// </summary>
public sealed partial class ParticleSkillPreview : Node3D
{
    private static readonly int[] DefaultSkills = [10748, 10664, 10499, 108, 39036];
    private static readonly float[] DefaultTimes = [.3f, .8f, 1.5f];
    private readonly List<EffectPlayer> _active = [];
    private EffectPlayer _player = null!;
    private Node3D _stage = null!;
    private Node3D _source = null!;
    private Node3D _target = null!;

    public override async void _Ready()
    {
        var args = Args(OS.GetCmdlineUserArgs());
        var pak = ClientPaths.Pak;
        var output = Path.GetFullPath(args.GetValueOrDefault("particle-preview-output",
            Path.Combine("particle-preview", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))));
        var skills = ParseInts(args.GetValueOrDefault("particle-preview-skills", ""), DefaultSkills);
        var times = ParseFloats(args.GetValueOrDefault("particle-preview-times", ""), DefaultTimes)
            .Where(value => value >= 0).Distinct().Order().ToArray();

        GetWindow().Size = new Vector2I(1280, 768);
        BuildStage();
        if (!PakFiles.Open(pak))
        {
            GD.PrintErr($"Particle preview could not open game pak: {pak}");
            GetTree().Quit(1);
            return;
        }
        var database = ClientPaths.Database;
        if (!File.Exists(database))
        {
            GD.PrintErr($"Particle preview database is missing: {database}");
            GetTree().Quit(1);
            return;
        }

        Directory.CreateDirectory(output);
        using var gameData = new GameData(database);
        using var fxDatabase = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(database), Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        fxDatabase.Open();
        ClientEnums.Ensure(fxDatabase);
        _player = new EffectPlayer { Name = "ParticleSkillPreviewPlayer" };
        AddChild(_player);
        var failures = 0;
        var writtenFrames = 0;
        foreach (var skillId in skills)
        {
            ClearEffect();
            if (!LoadSkill(gameData, fxDatabase, skillId))
            {
                failures++;
                continue;
            }

            var elapsed = 0f;
            foreach (var time in times)
            {
                var wait = time - elapsed;
                if (wait > 0)
                    await ToSignal(GetTree().CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                elapsed = time;
                var file = Path.Combine(output, $"skill_{skillId}_t{time.ToString("0.0##", CultureInfo.InvariantCulture)}.png");
                var image = GetViewport().GetTexture().GetImage();
                var error = image.IsEmpty() ? Error.CantCreate : image.SavePng(file);
                GD.Print($"Particle preview skill={skillId} time={time:G3}s path={file} result={error}");
                if (error != Error.Ok) failures++;
                else writtenFrames++;
            }
        }
        ClearEffect();
        GD.Print($"Particle preview finished: skills={skills.Length}, frames={writtenFrames}, failures={failures}, output={output}");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    private bool LoadSkill(GameData data, SqliteConnection database, int skillId)
    {
        var skill = data.GetSkill(skillId);
        if (skill == null)
        {
            GD.PrintErr($"Particle preview skill {skillId} was not found.");
            return false;
        }
        var groups = ResolveVisualGroups(data, database, skillId);
        var played = 0;
        var seenItems = new HashSet<int>();
        foreach (var groupId in groups)
        {
        var particleItems = ParticleItemIds(database, groupId);
        foreach (var item in data.GetFxGroupItems(groupId))
        {
            if (!particleItems.Contains(item.Id) || !seenItems.Add(item.Id) || string.IsNullOrWhiteSpace(item.AssetName)) continue;
            var separator = item.AssetName.IndexOf('.');
            var library = (separator < 0 ? item.AssetName : item.AssetName[..separator]).ToLowerInvariant();
            var path = $"game/libs/particles/{library}.xml";
            if (!_player.Effects.ContainsKey(item.AssetName) && !_player.LoadLibrary(path))
            {
                GD.PrintErr($"Particle preview FX library missing for skill {skillId}, effect '{item.AssetName}': {path}");
                continue;
            }
            if (!_player.Effects.ContainsKey(item.AssetName))
            {
                GD.PrintErr($"Particle preview effect '{item.AssetName}' for skill {skillId} is absent from {path}.");
                continue;
            }

            var offset = CryAxes.Point(item.OffsetX, item.OffsetY, item.OffsetZ);
            var anchor = item.LocationId == 2 ? _target : _source;
            try
            {
                _active.Add(_player.PlayAuthored(item.AssetName, anchor, offset, keepAlive: true));
                played++;
            }
            catch (Exception error)
            {
                GD.PrintErr($"Particle preview could not play skill {skillId} effect '{item.AssetName}': {error.Message}");
            }
        }
        }
        if (played > 0) return true;
        GD.PrintErr($"Particle preview skill {skillId} has no playable direct or recursively referenced particle effects.");
        return false;
    }

    private static HashSet<int> ParticleItemIds(SqliteConnection database, int groupId)
    {
        using var command = database.CreateCommand();
        command.CommandText = @"
SELECT f.id
FROM fx_group_fx_items gi
JOIN fx_items f ON f.id=gi.fx_item_id
WHERE gi.fx_group_id=@group AND f.fx_detail_type='FxParticle'";
        command.Parameters.AddWithValue("@group", groupId);
        using var reader = command.ExecuteReader();
        var ids = new HashSet<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static HashSet<int> ResolveVisualGroups(GameData data, SqliteConnection database, int rootSkillId)
    {
        var groups = new HashSet<int>();
        var visitedSkills = new HashSet<int>();
        var visitedEffects = new HashSet<long>();

        void AddSkill(int skillId)
        {
            if (skillId <= 0 || !visitedSkills.Add(skillId)) return;
            var directGroup = data.GetSkill(skillId)?.FxGroupId;
            if (directGroup is > 0) groups.Add(directGroup.Value);

            using var command = database.CreateCommand();
            command.CommandText = @"
SELECT e.id, e.actual_type, e.actual_id
FROM skill_effects se
JOIN effects e ON e.id=se.effect_id
WHERE se.skill_id=@skill";
            command.Parameters.AddWithValue("@skill", skillId);
            using var reader = command.ExecuteReader();
            var effects = new List<(long Id, string Type, long ActualId)>();
            while (reader.Read())
                effects.Add((reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetInt64(2)));
            reader.Close();

            foreach (var effect in effects)
            {
                if (!visitedEffects.Add(effect.Id)) continue;
                if (effect.Type.Equals("BuffEffect", StringComparison.OrdinalIgnoreCase))
                {
                    using var buff = database.CreateCommand();
                    buff.CommandText = @"
SELECT b.fx_group_id
FROM buff_effects be
JOIN buffs b ON b.id=be.buff_id
WHERE be.id=@id AND b.fx_group_id>0";
                    buff.Parameters.AddWithValue("@id", effect.ActualId);
                    if (buff.ExecuteScalar() is { } value && value != DBNull.Value)
                        groups.Add(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                }
                else if (effect.Type.Equals("SpecialEffect", StringComparison.OrdinalIgnoreCase))
                {
                    using var special = database.CreateCommand();
                    special.CommandText = @"
SELECT t.name, s.value1
FROM special_effects s
LEFT JOIN enum_skill_effect_special_type t ON t.id=s.special_effect_type_id
WHERE s.id=@id";
                    special.Parameters.AddWithValue("@id", effect.ActualId);
                    using var specialReader = special.ExecuteReader();
                    if (!specialReader.Read() || specialReader.IsDBNull(1)) continue;
                    var kind = specialReader.IsDBNull(0) ? "" : specialReader.GetString(0);
                    var referencedId = Convert.ToInt32(specialReader.GetValue(1), CultureInfo.InvariantCulture);
                    specialReader.Close();
                    if (kind.Equals("skill", StringComparison.OrdinalIgnoreCase)) AddSkill(referencedId);
                    else if (kind.Equals("fx_group", StringComparison.OrdinalIgnoreCase) && referencedId > 0) groups.Add(referencedId);
                }
            }
        }

        AddSkill(rootSkillId);
        return groups;
    }

    private void ClearEffect()
    {
        foreach (var playback in _active)
        {
            if (!GodotObject.IsInstanceValid(playback)) continue;
            playback.Stop(removeEmitters: true);
            playback.QueueFree();
        }
        _active.Clear();
    }

    private void BuildStage()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(.055f, .065f, .085f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(.72f, .76f, .84f),
                AmbientLightEnergy = .7f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
                // Cry particle HDR colors bloom around self-lit effects such as meteor decals.
                GlowEnabled = true,
                GlowHdrThreshold = .25f,
                GlowIntensity = .8f,
                GlowStrength = .8f
            }
        });
        AddChild(new DirectionalLight3D
        {
            Name = "PreviewKeyLight", RotationDegrees = new Vector3(-38, -28, 0),
            LightColor = new Color(1f, .94f, .84f), LightEnergy = 1.2f, ShadowEnabled = true
        });
        _stage = new Node3D { Name = "EffectStage" };
        AddChild(_stage);
        _source = AddDummy("Source", new Vector3(-2.5f, 0, 0), new Color(.18f, .42f, .8f));
        _target = AddDummy("Target", new Vector3(2.5f, 0, 0), new Color(.8f, .28f, .16f));
        var camera = new Camera3D
        {
            Name = "PreviewCamera", Position = new Vector3(0, 2.2f, 14f), Current = true,
            Fov = 38f, Near = .05f, Far = 350f
        };
        AddChild(camera);
        camera.LookAt(new Vector3(0, 1.5f, 0), Vector3.Up);
    }

    private Node3D AddDummy(string name, Vector3 position, Color color)
    {
        var anchor = new Node3D { Name = name, Position = position };
        _stage.AddChild(anchor);
        anchor.AddChild(new MeshInstance3D
        {
            Name = name + " Marker", Position = new Vector3(0, .9f, 0),
            Mesh = new CapsuleMesh { Radius = .32f, Height = 1.8f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Roughness = .75f }
        });
        return anchor;
    }

    private static int[] ParseInts(string text, int[] fallback) => string.IsNullOrWhiteSpace(text) ? fallback :
        text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1)
            .Where(value => value >= 0).Distinct().ToArray();

    private static float[] ParseFloats(string text, float[] fallback) => string.IsNullOrWhiteSpace(text) ? fallback :
        text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1)
            .ToArray();

    private static Dictionary<string, string> Args(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            var pair = arg.TrimStart('-').Split('=', 2);
            if (pair.Length == 2) result[pair[0]] = pair[1];
        }
        return result;
    }
}
