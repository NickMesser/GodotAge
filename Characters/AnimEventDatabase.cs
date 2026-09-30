#nullable enable
using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Godot;

namespace AAEmu.GodotViewer;

/// <summary>
/// One event of an animation clip in a character's .animevents database (game/animations/&lt;race&gt;/&lt;gender&gt;/event_database.animevents,
/// named by the .cal's $AnimEventDatabase): "effect"/"@effect" start a particle effect at <see cref="Time"/> on <see cref="Bone"/>,
/// "!skill_effect" name a skill fx phase (form, throw, launch, hit, end) whose items the skill's fx group then plays.
/// </summary>
internal sealed record AnimEvent(string Name, float Time, string Parameter, string Bone, Vector3 Offset, Vector3 Direction)
{
    public bool IsEffect => Name is "effect" or "@effect" && Parameter.Length > 0;
    public bool IsSkillEffect => Name == "!skill_effect";
}

/// <summary>Effect events of animation clips, looked up by the clip's alias in the character's .cal. Parsing runs on a worker.</summary>
internal static class AnimEventDatabase
{
    private sealed record EventSet(CalFile Cal, Dictionary<string, List<AnimEvent>> Events);

    private static readonly ConcurrentDictionary<string, Task<EventSet?>> Sets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts reading the event database of a character's animation list (no-op when already started).</summary>
    public static void Prepare(string calPath)
    {
        if (calPath.Length > 0) Sets.GetOrAdd(calPath, path => Task.Run(() => Load(path)));
    }

    public static bool IsReady(string calPath) => Sets.TryGetValue(calPath, out var task) && task.IsCompleted;

    /// <summary>Events of a clip alias (empty until the database is ready, or when the clip has none), ordered by time.</summary>
    public static IReadOnlyList<AnimEvent> For(string calPath, string alias)
    {
        if (calPath.Length == 0 || alias.Length == 0) return [];
        if (!Sets.TryGetValue(calPath, out var task)) { Prepare(calPath); return []; }
        if (!task.IsCompletedSuccessfully || task.Result is not { } set) return [];
        var path = set.Cal.Find(alias);
        if (path == null && alias.EndsWith("_ub", StringComparison.OrdinalIgnoreCase)) path = set.Cal.Find(alias[..^3] + "_mub");
        return path != null && set.Events.TryGetValue(path, out var list) ? list : [];
    }

    private static EventSet? Load(string calPath)
    {
        try
        {
            var cal = CalReader.Load(calPath, PakFiles.Read);
            var events = new Dictionary<string, List<AnimEvent>>(StringComparer.OrdinalIgnoreCase);
            if (cal.Directives.TryGetValue("$AnimEventDatabase", out var databasePath) &&
                PakFiles.ReadText(CdfReader.PakPath(databasePath)) is { } text)
            {
                foreach (var animation in XDocument.Parse(text.TrimStart('﻿')).Descendants("animation"))
                {
                    var name = (string?)animation.Attribute("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    var list = new List<AnimEvent>();
                    foreach (var e in animation.Elements("event"))
                    {
                        var time = float.TryParse((string?)e.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0f;
                        list.Add(new AnimEvent(((string?)e.Attribute("name")) ?? "", time, (((string?)e.Attribute("parameter")) ?? "").Trim(),
                            (((string?)e.Attribute("bone")) ?? "").Trim(), Vec((string?)e.Attribute("offset")), Vec((string?)e.Attribute("dir"))));
                    }
                    if (list.Count > 0) events[CdfReader.PakPath(name)] = [.. list.OrderBy(e => e.Time)];
                }
            }
            return new EventSet(cal, events);
        }
        catch (Exception e)
        {
            GD.PrintErr($"Animation events of {calPath}: {e.Message}");
            return null;
        }
    }

    private static Vector3 Vec(string? text)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries);
        float V(int i) => i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
        return new Vector3(V(0), V(1), V(2));
    }
}
