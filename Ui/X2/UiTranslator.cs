#nullable enable
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>
/// English for UI text that the client data only has in Chinese or Korean. Keys are the original strings
/// (Ui/X2/Translations/strings.json); lookups are exact first, then by replacing every known original inside a longer
/// text (strings with colour codes or substituted values). Anything still containing CJK/Hangul after translation is
/// recorded in <see cref="Missing"/>.
/// </summary>
public sealed class UiTranslator
{
    private static readonly Regex DoNotTranslate = new(@"\s*DO\s+NOT\s+TRANSLATE\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TestPrefix = new(@"^\s*TEST\s*:\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Foreign = new(@"[ᄀ-ᇿ぀-ヿ㄰-㆏㐀-鿿가-힯！-～]", RegexOptions.Compiled);
    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, string>> _bySize = [];
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);

    public static readonly UiTranslator None = new();

    /// <summary>The translator the client loaded (Ui/X2/Translations/*.json); usable by non-UI code such as nameplates.</summary>
    public static UiTranslator Shared { get; set; } = None;

    public int Count => _exact.Count;

    /// <summary>Adds exact translations (original → English) without overriding entries the JSON files already define.</summary>
    public void AddExact(IEnumerable<(string Original, string English)> entries)
    {
        foreach (var (original, english) in entries)
            if (original.Length > 0 && english.Length > 0) _exact.TryAdd(original, english);
        _cache.Clear();
    }
    public IReadOnlyCollection<string> Missing => _missing;

    /// <summary>Loads a JSON object { "original": "English", ... }; keys starting with "//" are comments.</summary>
    public static UiTranslator FromJson(string? json) => FromFiles([("strings.json", json)]);

    /// <summary>
    /// Loads several translation files (file name, JSON). Every file serves exact lookups; only strings.json (UI strings
    /// and script literals) also feeds the substring pass, since the db_*.json tables are large (names, descriptions).
    /// </summary>
    public static UiTranslator FromFiles(IEnumerable<(string Name, string? Json)> files)
    {
        var t = new UiTranslator();
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var substring = new List<KeyValuePair<string, string>>();
        foreach (var (name, json) in files)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            Dictionary<string, string>? map;
            try { map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, options); }
            catch (JsonException) { continue; }
            if (map == null) continue;
            var forSubstring = string.Equals(Path.GetFileName(name), "strings.json", StringComparison.OrdinalIgnoreCase);
            foreach (var (k, v) in map)
            {
                if (k.Length == 0 || k.StartsWith("//", StringComparison.Ordinal) || v == null) continue;
                t._exact[k] = v;
                if (forSubstring) substring.Add(new KeyValuePair<string, string>(k, v));
            }
        }
        t._bySize.AddRange(substring.OrderByDescending(kv => kv.Key.Length));
        return t;
    }

    public static bool IsForeign(string text) => Foreign.IsMatch(text);

    /// <summary>True when a localized database cell is an editor/export placeholder rather than player-facing text.</summary>
    public static bool IsDatabasePlaceholder(string? text) =>
        string.IsNullOrWhiteSpace(text) || DoNotTranslate.IsMatch(text) || TestPrefix.IsMatch(text);

    /// <summary>
    /// Chooses usable localized database text, otherwise translates the source-language cell through the db_strings
    /// dictionaries. Export markers are removed as a final guard so they can never leak into names or descriptions.
    /// </summary>
    public string TranslateDatabaseText(string? localized, string? source)
    {
        var placeholder = IsDatabasePlaceholder(localized);
        var result = CleanDatabaseText(Translate(placeholder ? source : localized));
        // Old content sometimes lacks a db_strings entry for its source cell. A TEST-prefixed English localization
        // is still preferable once its editor prefix has been removed.
        if (placeholder && (result.Length == 0 || IsForeign(result)))
            result = CleanDatabaseText(Translate(localized));
        return result;
    }

    /// <summary>Translates a source database cell and removes editor-only display markers.</summary>
    public string TranslateDatabaseText(string? text) => CleanDatabaseText(Translate(text));

    public static string CleanDatabaseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var result = DoNotTranslate.Replace(text, " ");
        result = TestPrefix.Replace(result, "");
        result = result.Trim(' ', '\t', '\r', '\n', '-', ':');
        return Regex.IsMatch(result, @"^\d+$", RegexOptions.CultureInvariant) ? "" : result;
    }

    public string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (_cache.TryGetValue(text, out var cached)) return cached;
        if (!_exact.TryGetValue(text, out var result) && IsForeign(text))
        {
            result = text;
            foreach (var (key, value) in _bySize)
                if (result.Contains(key, StringComparison.Ordinal)) result = result.Replace(key, value, StringComparison.Ordinal);
        }
        result ??= text;
        if (IsForeign(result)) _missing.Add(text);
        if (_cache.Count > 5000) _cache.Clear();
        _cache[text] = result;
        return result;
    }
}
