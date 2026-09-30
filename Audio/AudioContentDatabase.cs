using Microsoft.Data.Sqlite;
using Godot;
using System.Xml.Linq;

namespace AAEmu.GodotViewer;

using Audio;

public sealed record AudioEventDefinition(string EventPath, IReadOnlyList<SoundEventReference> Candidates);
public sealed record ZoneAudioDefinition(uint ZoneKey, uint SubZoneId, AudioEventDefinition? Music, AudioEventDefinition? Ambience);
public sealed record SubZoneArea(uint Id, double X, double Y, double Width, double Height);

/// <summary>
/// Read-only import of client sound metadata.  It translates DB sound paths,
/// named sound-pack items and FEV bank lists into loadable FSB candidates.
/// </summary>
public sealed class AudioContentDatabase
{
    private readonly Func<string, byte[]?> _readPak;
    private readonly Dictionary<string, string> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _ui = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _named = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _skillFx = [];
    private readonly Dictionary<string, string> _particleFx = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(uint Zone, uint SubZone), (long? Sound, long? Pack)> _zones = [];
    private readonly Dictionary<uint, List<SubZoneArea>> _subZoneAreas = [];
    private readonly Dictionary<long, List<(string Name, string Event)>> _packItems = [];
    private readonly Dictionary<string, FmodFevProject?> _fevs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioEventDefinition> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly FsbSoundIndex _bankIndex;

    private AudioContentDatabase(Func<string, byte[]?> readPak)
    {
        _readPak = readPak;
        _bankIndex = new FsbSoundIndex(readPak);
    }

    public static AudioContentDatabase Load(string databasePath, Func<string, byte[]?> readPak)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(readPak);
        var result = new AudioContentDatabase(readPak);
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly };
        using var db = new SqliteConnection(builder.ToString());
        db.Open();
        result.ReadSounds(db);
        result.ReadPacks(db);
        result.ReadZones(db);
        result.ReadSkillFx(db);
        result.ReadParticleSounds();
        return result;
    }

    public bool TryResolveEvent(string eventPath, out AudioEventDefinition? definition)
    {
        var normalized = NormalizeEvent(eventPath);
        if (_resolved.TryGetValue(normalized, out definition)) return true;
        if (!_events.TryGetValue(normalized, out var canonical))
            canonical = normalized;
        definition = BuildDefinition(canonical);
        if (definition.Candidates.Count == 0)
        {
            definition = null;
            return false;
        }
        _resolved[normalized] = definition;
        return true;
    }

    public bool TryResolveUi(string name, out AudioEventDefinition? definition) =>
        TryResolveNamed(_ui, name, out definition);

    public bool TryResolveNamedSound(string name, out AudioEventDefinition? definition) =>
        TryResolveNamed(_named, name, out definition);

    public bool TryResolveSkillFx(uint fxItemId, out AudioEventDefinition? definition)
    {
        definition = null;
        return _skillFx.TryGetValue(fxItemId, out var path) && TryResolveEvent(path, out definition);
    }

    /// <summary>Resolves a <c>Particles Name</c> from game/libs/particles/x2_sounds.xml.</summary>
    public bool TryResolveParticleFx(string particleName, out AudioEventDefinition? definition)
    {
        definition = null;
        var key = particleName.Trim();
        if (!_particleFx.TryGetValue(key, out var path) &&
            !_particleFx.TryGetValue(key.StartsWith("particles.", StringComparison.OrdinalIgnoreCase) ? key : "particles." + key, out path))
            return false;
        return TryResolveEvent(path, out definition);
    }

    public bool TryResolveZone(uint zoneKey, uint subZoneId, out ZoneAudioDefinition definition)
    {
        var key = (zoneKey, subZoneId);
        if (!_zones.TryGetValue(key, out var source) && !_zones.TryGetValue((zoneKey, 0), out source))
        {
            definition = default!;
            return false;
        }
        var entries = new List<(string Name, string Event)>();
        if (source.Sound is { } sound && _events.TryGetValue($"id:{sound}", out var direct))
            entries.Add(("music_01", direct));
        if (source.Pack is { } pack && _packItems.TryGetValue(pack, out var packEntries))
            entries.AddRange(packEntries);
        var music = ResolveFirst(entries, name => name.StartsWith("music", StringComparison.OrdinalIgnoreCase));
        // The content DB has no sound_pack_items called ambience/ambient (verified
        // against this client). SoundMoods controls ambience bus filters, rather
        // than identifying a zone ambience event, so do not invent one here.
        AudioEventDefinition? ambience = null;
        definition = new ZoneAudioDefinition(zoneKey, subZoneId, music, ambience);
        return music != null || ambience != null;
    }

    /// <summary>Finds the smallest database rectangle containing a Cry world position for this zone.</summary>
    public uint FindSubZone(uint zoneKey, double x, double y)
    {
        if (!_subZoneAreas.TryGetValue(zoneKey, out var areas)) return 0;
        foreach (var area in areas)
            if (x >= area.X && x <= area.X + area.Width && y >= area.Y && y <= area.Y + area.Height)
                return area.Id;
        return 0;
    }

    private void ReadSounds(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id, path FROM sounds WHERE path <> ''";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = NormalizeEvent(reader.GetString(1));
            _events[path] = path;
            _events[$"id:{reader.GetInt64(0)}"] = path;
        }
    }

    private void ReadPacks(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT spi.sound_pack_id, spi.name, s.path FROM sound_pack_items spi JOIN sounds s ON s.id = spi.sound_id WHERE s.path <> ''";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var pack = reader.GetInt64(0);
            var name = reader.GetString(1);
            var path = NormalizeEvent(reader.GetString(2));
            if (!_packItems.TryGetValue(pack, out var items)) _packItems[pack] = items = [];
            items.Add((name, path));
            if (!_named.TryGetValue(name, out var namedPaths)) _named[name] = namedPaths = [];
            if (!namedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) namedPaths.Add(path);
            if (path.Contains("/x2interface:", StringComparison.OrdinalIgnoreCase))
            {
                if (!_ui.TryGetValue(name, out var paths)) _ui[name] = paths = [];
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
            }
        }
    }

    private void ReadZones(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT z.zone_key, 0, zg.sound_id, zg.sound_pack_id
            FROM zones z JOIN zone_groups zg ON zg.id = z.group_id
            UNION ALL
            SELECT z.zone_key, sz.id, COALESCE(sz.sound_id, zg.sound_id), COALESCE(NULLIF(sz.sound_pack_id, 0), zg.sound_pack_id)
            FROM zones z JOIN zone_groups zg ON zg.id = z.group_id JOIN sub_zones sz ON sz.linked_zone_group_id = zg.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long? sound = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            long? pack = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            _zones[((uint)reader.GetInt64(0), (uint)reader.GetInt64(1))] = (sound, pack);
        }
        reader.Close();

        using var areas = db.CreateCommand();
        areas.CommandText = """
            SELECT z.zone_key, sz.id, sz.x, sz.y, sz.w, sz.h
            FROM zones z JOIN sub_zones sz ON sz.linked_zone_group_id = z.group_id
            WHERE sz.w > 0 AND sz.h > 0
            ORDER BY sz.w * sz.h
            """;
        using var areaReader = areas.ExecuteReader();
        while (areaReader.Read())
        {
            var zoneKey = (uint)areaReader.GetInt64(0);
            if (!_subZoneAreas.TryGetValue(zoneKey, out var list)) _subZoneAreas[zoneKey] = list = [];
            list.Add(new SubZoneArea((uint)areaReader.GetInt64(1), areaReader.GetDouble(2), areaReader.GetDouble(3),
                areaReader.GetDouble(4), areaReader.GetDouble(5)));
        }
    }

    private void ReadSkillFx(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT fi.id, COALESCE(NULLIF(fsnd.path, ''), NULLIF(fi.asset_name, ''))
            FROM fx_items fi
            LEFT JOIN fx_sounds fs ON fs.id = fi.fx_detail_id
            LEFT JOIN sounds fsnd ON fsnd.id = fs.sound_id
            WHERE fi.fx_detail_type = 'FxSound'
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(1) && NormalizeEvent(reader.GetString(1)).StartsWith("sounds/", StringComparison.Ordinal))
                _skillFx[(uint)reader.GetInt64(0)] = NormalizeEvent(reader.GetString(1));
    }

    private void ReadParticleSounds()
    {
        const string path = "game/libs/particles/x2_sounds.xml";
        try
        {
            var bytes = _readPak(path);
            if (bytes == null) return;
            var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\ufeff'));
            foreach (var particle in document.Descendants().Where(node => node.Name.LocalName == "Particles"))
            {
                var name = particle.Attribute("Name")?.Value;
                var sound = particle.Elements().FirstOrDefault(node => node.Name.LocalName == "Params")?.Attribute("Sound")?.Value;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(sound) &&
                    NormalizeEvent(sound).StartsWith("sounds/", StringComparison.Ordinal))
                    _particleFx[name] = NormalizeEvent(sound);
            }
        }
        catch (Exception error)
        {
            GD.PushWarning($"Could not parse particle sound bindings '{path}': {error.Message}");
        }
    }

    private bool TryResolveNamed(Dictionary<string, List<string>> names, string name, out AudioEventDefinition? definition)
    {
        definition = null;
        if (!names.TryGetValue(name, out var paths)) return false;
        foreach (var path in paths)
            if (TryResolveEvent(path, out definition)) return true;
        return false;
    }

    private AudioEventDefinition? ResolveFirst(IEnumerable<(string Name, string Event)> entries, Func<string, bool> predicate)
    {
        foreach (var (_, path) in entries.Where(item => predicate(item.Name)))
            if (TryResolveEvent(path, out var result)) return result;
        return null;
    }

    private AudioEventDefinition BuildDefinition(string eventPath)
    {
        var parts = eventPath.Split(':', 3);
        if (parts.Length < 3 || !parts[0].StartsWith("sounds/", StringComparison.OrdinalIgnoreCase))
            return new AudioEventDefinition(eventPath, []);
        var group = parts[0]["sounds/".Length..];
        var sample = parts[2].Trim().TrimEnd('/');
        var fevPath = $"game/sounds/{group}/{group}.fev";
        if (!_fevs.TryGetValue(fevPath, out var fev))
        {
            try { fev = _readPak(fevPath) is { } bytes ? FmodFevProject.Parse(bytes, fevPath) : null; }
            catch (Exception error) { GD.PushWarning($"Could not parse FEV '{fevPath}': {error.Message}"); fev = null; }
            _fevs[fevPath] = fev;
        }
        var aliases = BuildSampleAliases(parts[1], sample, fev?.Names ?? []);
        var candidates = _bankIndex.Resolve(group, fev?.Banks ?? [], aliases);
        return new AudioEventDefinition(eventPath, candidates);
    }

    private static IEnumerable<string> BuildSampleAliases(string category, string eventName, IReadOnlyList<string> fevNames)
    {
        yield return eventName;
        yield return category + "_" + eventName;
        // FMOD logical group names omit the zero used by the source/FBS samples:
        // `group_006:01` has source samples such as `group_06_a`.
        if (category.StartsWith("group_0", StringComparison.OrdinalIgnoreCase) && category.Length > 7)
            yield return category.Remove(6, 1);
        foreach (var wave in fevNames.Where(name => name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
        {
            var file = Path.GetFileNameWithoutExtension(wave).Replace('.', '_');
            if (file.Contains(eventName, StringComparison.OrdinalIgnoreCase) ||
                file.Contains(category, StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }

    private static string NormalizeEvent(string path) => path.Trim().TrimStart('/').Replace('\\', '/').ToLowerInvariant();
}
