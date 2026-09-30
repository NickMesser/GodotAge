#nullable enable
namespace AAEmu.GodotViewer.Lua;

public sealed class LuaException : Exception
{
    public LuaException(string luaMessage, string traceback, int statusCode)
        : base(luaMessage)
    {
        LuaMessage = luaMessage;
        Traceback = traceback;
        StatusCode = statusCode;
    }

    public string LuaMessage { get; }
    public string Traceback { get; }
    public int StatusCode { get; }

    public override string ToString() => $"{GetType().FullName}: {LuaMessage}{Environment.NewLine}{Traceback}";
}
