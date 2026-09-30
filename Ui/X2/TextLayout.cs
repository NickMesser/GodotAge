#nullable enable
using System.Globalization;
using System.Text;

namespace AAEmu.GodotViewer.Ui.X2;

public readonly record struct TextRun(string Text, UiColor? Color, string? Icon = null);

public sealed class TextLine(List<TextRun> runs, float width)
{
    public List<TextRun> Runs { get; } = runs;
    public float Width { get; } = width;
    public string Plain => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>Colour codes (|cAARRGGBB ... |r), line breaks and word wrapping for widget text.</summary>
public static class TextLayout
{
    public const float CurrencyIconSize = 15;
    public const string CurrencyTexture = "ui/common/money_window.dds";
    private readonly record struct Glyph(char Ch, UiColor? Color, string? Icon = null);

    public static string Strip(string text)
    {
        if (text.IndexOf('|') < 0) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var g in Parse(text)) if (g.Icon == null) sb.Append(g.Ch);
        return sb.ToString();
    }

    private static List<Glyph> Parse(string text)
    {
        var result = new List<Glyph>(text.Length);
        UiColor? color = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '|' && i + 1 < text.Length)
            {
                var code = text[i + 1];
                if ((code == 'c' || code == 'C') && i + 9 < text.Length
                    && uint.TryParse(text.AsSpan(i + 2, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
                {
                    color = new UiColor(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, ((argb >> 24) & 255) / 255f);
                    i += 9;
                    continue;
                }
                if (code == 'r' || code == 'R') { color = null; i += 1; continue; }
                var semi = text.IndexOf(';', i + 2);
                if (code == ',' && semi > i + 2 && semi - i <= 24 &&
                    long.TryParse(text.AsSpan(i + 2, semi - i - 2), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var groupedNumber))
                {
                    // ArcheAge's |,number; markup groups digits for labor and other counters.
                    foreach (var digit in groupedNumber.ToString("N0", CultureInfo.InvariantCulture))
                        result.Add(new Glyph(digit, color));
                    i = semi;
                    continue;
                }
                if (code == 'n' && semi > 0 && semi - i <= 4)
                {
                    // |n<key>; named colour (|nc; |nr; |ny; ...)
                    color = NamedColor(text.Substring(i + 2, semi - i - 2));
                    i = semi;
                    continue;
                }
                if (code == 'b' && i + 2 < text.Length && text[i + 2] == 'r') { i += 2; continue; } // |br bullet end
                if (code == 'b' && i + 2 < text.Length && text[i + 2] == 'u' && semi > 0 && semi - i <= 8)
                {
                    // |bu<symbol>; bullet
                    foreach (var c in text.Substring(i + 3, semi - i - 3) + " ") result.Add(new Glyph(c, color));
                    i = semi;
                    continue;
                }
                if (code == 'm' && semi > 0 && long.TryParse(text.AsSpan(i + 2, semi - i - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var copper))
                {
                    // |m<copper>; money: gold / silver / copper
                    var parts = new List<(string, string)>();
                    if (copper >= 10000) parts.Add(($"{copper / 10000}", "money_gold"));
                    if (copper >= 100) parts.Add(($"{copper / 100 % 100}", "money_silver"));
                    parts.Add(($"{copper % 100}", "money_copper"));
                    for (var p = 0; p < parts.Count; p++)
                    {
                        if (p > 0) result.Add(new Glyph(' ', color));
                        foreach (var c in parts[p].Item1) result.Add(new Glyph(c, color));
                        result.Add(new Glyph('\0', color, parts[p].Item2));
                    }
                    i = semi;
                    continue;
                }
                if (semi > 0 && semi - i <= 24 && ValueCode(text.AsSpan(i + 1, semi - i - 1)) is var (letters, value) && letters > 0)
                {
                    // Client currency escapes draw the numeric value followed by a 15px atlas icon.
                    foreach (var c in value) result.Add(new Glyph(c, color));
                    var icon = text.Substring(i + 1, letters) switch
                    {
                        "p" => "aa_point_copper",
                        "h" => "money_honor",
                        "j" => "point",
                        "bm" => "money_bmpoint",
                        _ => null,
                    };
                    if (icon != null && value.Length > 0) result.Add(new Glyph('\0', color, icon));
                    i = semi;
                    continue;
                }
            }
            if (ch == '\r') continue;
            if (ch == '\\' && i + 1 < text.Length && text[i + 1] == 'n')
            {
                // ui_texts store line breaks as the two characters \n
                result.Add(new Glyph('\n', color));
                i++;
                continue;
            }
            result.Add(new Glyph(ch, color));
        }
        return result;
    }

    /// <summary>Splits "bm120" into (2, "120"): 1-3 letters then an optional signed number; (0, "") when it is not such a code.</summary>
    private static (int Letters, string Value) ValueCode(ReadOnlySpan<char> code)
    {
        var n = 0;
        while (n < code.Length && n < 3 && char.IsAsciiLetter(code[n])) n++;
        if (n == 0) return (0, "");
        var rest = code[n..];
        if (rest.Length == 0) return (n, "");
        var start = rest[0] == '-' ? 1 : 0;
        if (start >= rest.Length) return (0, "");
        foreach (var c in rest[start..]) if (!char.IsAsciiDigit(c) && c != '.' && c != ',') return (0, "");
        return (n, rest.ToString());
    }

    private static UiColor? NamedColor(string key) => key switch
    {
        "y" => new UiColor(1f, 0.85f, 0.35f),
        "r" => new UiColor(0.9f, 0.35f, 0.3f),
        "c" => new UiColor(0.45f, 0.75f, 0.95f),
        "i" => new UiColor(0.55f, 0.85f, 0.45f),
        "d" => new UiColor(0.9f, 0.75f, 0.5f),
        "n" => new UiColor(0.95f, 0.95f, 0.8f),
        _ => null,
    };

    public static float PlainWidth(Widget w, string text)
    {
        var lines = Lines(w.Root, w.style.FontKey, w.style.FontSize, text, float.MaxValue);
        return lines.Count == 0 ? 0 : lines.Max(line => line.Width);
    }

    /// <summary>Splits a widget's text into drawn lines (wrapping at maxWidth when finite).</summary>
    public static List<TextLine> Lines(Widget w, float maxWidth) => Lines(w.Root, w.style.FontKey, w.style.FontSize, w.GetText(), maxWidth);

    public static List<TextLine> Lines(UiRoot root, string fontKey, float size, string text, float maxWidth)
    {
        var result = new List<TextLine>();
        if (string.IsNullOrEmpty(text)) return result;
        var glyphs = Parse(root.Translator.Translate(text));
        var start = 0;
        for (var i = 0; i <= glyphs.Count; i++)
        {
            if (i < glyphs.Count && glyphs[i].Ch != '\n') continue;
            Wrap(root, fontKey, size, glyphs.GetRange(start, i - start), maxWidth, result);
            start = i + 1;
        }
        return result;
    }

    private static void Wrap(UiRoot root, string fontKey, float size, List<Glyph> para, float maxWidth, List<TextLine> into)
    {
        float Measure(List<Glyph> g, int from, int count)
        {
            var sb = new StringBuilder(count);
            var width = 0f;
            UiColor? runColor = null;
            for (var k = from; k < from + count; k++)
            {
                if (g[k].Icon == null && (sb.Length == 0 || g[k].Color == runColor))
                {
                    sb.Append(g[k].Ch);
                    runColor = g[k].Color;
                    continue;
                }
                if (sb.Length > 0)
                {
                    width += root.MeasureText(fontKey, size, sb.ToString());
                    sb.Clear();
                }
                if (g[k].Icon != null) width += CurrencyIconSize;
                else { sb.Append(g[k].Ch); runColor = g[k].Color; }
            }
            if (sb.Length > 0) width += root.MeasureText(fontKey, size, sb.ToString());
            return width;
        }

        if (para.Count == 0) { into.Add(new TextLine([], 0)); return; }
        var lineStart = 0;
        while (lineStart < para.Count)
        {
            var total = Measure(para, lineStart, para.Count - lineStart);
            if (float.IsInfinity(maxWidth) || maxWidth >= float.MaxValue / 2 || total <= maxWidth)
            {
                into.Add(Build(para, lineStart, para.Count - lineStart, total));
                break;
            }
            // longest prefix that fits, preferring a break after a space
            int lo = 1, hi = para.Count - lineStart;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (Measure(para, lineStart, mid) <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            var fit = lo;
            var lastSpace = -1;
            for (var n = fit; n >= 1; n--)
                if (para[lineStart + n - 1].Ch == ' ') { lastSpace = n; break; }
            var take = lastSpace > 0 && fit < para.Count - lineStart ? lastSpace : fit;
            into.Add(Build(para, lineStart, take, Measure(para, lineStart, take)));
            lineStart += take;
            while (lineStart < para.Count && para[lineStart].Ch == ' ') lineStart++;
        }
    }

    private static TextLine Build(List<Glyph> g, int from, int count, float width)
    {
        var runs = new List<TextRun>();
        var sb = new StringBuilder();
        UiColor? color = count > 0 ? g[from].Color : null;
        for (var k = from; k < from + count; k++)
        {
            if (g[k].Icon is { } icon)
            {
                if (sb.Length > 0) runs.Add(new TextRun(sb.ToString(), color));
                sb.Clear();
                runs.Add(new TextRun("", g[k].Color, icon));
                color = g[k].Color;
                continue;
            }
            if (g[k].Color != color)
            {
                if (sb.Length > 0) runs.Add(new TextRun(sb.ToString(), color));
                sb.Clear();
                color = g[k].Color;
            }
            sb.Append(g[k].Ch);
        }
        if (sb.Length > 0) runs.Add(new TextRun(sb.ToString(), color));
        return new TextLine(runs, width);
    }
}
