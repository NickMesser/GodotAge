#nullable enable
using System.Text.RegularExpressions;

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Reads the dev account the local client launcher uses (the .bat named by the launcher setting, see <see cref="ClientPaths.Launcher"/>) so no credential has
/// to be copied into code or config. <see cref="ToString"/> never includes the token.
/// </summary>
public sealed partial class LauncherConfig
{
    public static string DefaultPath => ClientPaths.Launcher;

    public string UserName { get; private init; } = "";
    public string UserToken { get; private init; } = "";
    public string LoginHost { get; private init; } = ClientPaths.LoginHost;
    public int LoginPort { get; private init; } = ClientPaths.LoginPort;
    public int GameId { get; private init; } = 1;

    public static LauncherConfig? TryRead(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path);
        string? Var(string name)
        {
            var m = Regex.Match(text, $@"^\s*set\s+""?{name}=([^""\r\n]*)""?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        string? Arg(string name)
        {
            var m = Regex.Match(text, $@"-{name}=(\S+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
        string Expand(string? v) => v is null ? "" : VarRef().Replace(v, m => Var(m.Groups[1].Value) ?? m.Value);

        var user = Expand(Arg("StrUserName"));
        var token = Expand(Arg("strUserToken"));
        if (user.Length == 0 || token.Length == 0)
            return null;
        return new LauncherConfig
        {
            UserName = user,
            UserToken = token,
            LoginHost = Expand(Arg("sIp")) is { Length: > 0 } ip ? ip : ClientPaths.LoginHost,
            LoginPort = int.TryParse(Expand(Arg("sPort")), out var port) ? port : ClientPaths.LoginPort,
            GameId = int.TryParse(Expand(Arg("gameId")), out var g) ? g : 1,
        };
    }

    public GameClientOptions ToOptions(Action<string>? log = null) => new()
    {
        LoginHost = LoginHost,
        LoginPort = LoginPort,
        UserName = UserName,
        UserToken = UserToken,
        GameId = GameId,
        Log = log,
    };

    public override string ToString() => $"{UserName}@{LoginHost}:{LoginPort} (gameId {GameId}, token hidden)";

    [GeneratedRegex(@"%([A-Za-z0-9_]+)%")]
    private static partial Regex VarRef();
}
