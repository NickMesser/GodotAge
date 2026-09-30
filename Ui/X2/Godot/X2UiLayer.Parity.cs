#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

// Parity capture for comparing the HUD with the original client: with X2_PARITY=<folder> set, once in the world the
// layer opens each reference screen in turn, saves a screenshot and a widget rectangle dump (the same Lua walker the
// original client runs), then quits. Screens: hud, bag, character, skill, quest, worldmap, esc, target.
public partial class X2UiLayer
{
    private const string ParityWalker = """
-- Parity walker: every visible widget/drawable reachable from UIC contents and global widgets, by field path.
return (function(onlyVisible)
  local out, seen = {}, {}
  local skip = { parent=true, owner=true, target=true, window=true, frame=true, root=true, wnd=true, mainWindow=true, parentWnd=true }
  local function isw(v) local t = type(v); return (t=="table" or t=="userdata") and type(v.GetOffset)=="function" and type(v.GetExtent)=="function" end
  -- widgets are tables in the client; the Godot client keeps their Lua fields in the userdata environment
  local function fields(w) if type(w)=="table" then return w end; local ok, e = pcall(debug.getfenv, w); return ok and type(e)=="table" and e or {} end
  local queue, qi = {}, 1
  local names = {}
  for k,v in pairs(_G) do if type(k)=="string" and k:find("^UIC_") and type(v)=="number" then names[#names+1]=k end end
  table.sort(names)
  for _,k in ipairs(names) do
    local ok, w = pcall(function() return ADDON:GetContent(_G[k]) end)
    if ok and isw(w) and not seen[w] then seen[w]=true; queue[#queue+1] = {w, k, 0} end
  end
  local globals = {}
  for k,v in pairs(_G) do if type(k)=="string" and k ~= "UIParent" and isw(v) then globals[#globals+1]=k end end
  table.sort(globals)
  for _,k in ipairs(globals) do local w=_G[k]; if not seen[w] then seen[w]=true; queue[#queue+1] = {w, "G."..k, 0} end end
  while qi <= #queue and #out < 8000 do
    local w, path, d = queue[qi][1], queue[qi][2], queue[qi][3]; qi = qi + 1
    local vis = true
    local okv, v = pcall(w.IsVisible, w); if okv then vis = v end
    if vis or not onlyVisible then
      local ok1, x, y = pcall(w.GetOffset, w)
      local ok2, ww, hh = pcall(w.GetExtent, w)
      out[#out+1] = string.format("%s|%s|%.1f|%.1f|%.1f|%.1f", path, tostring(vis), ok1 and x or -1, ok1 and y or -1, ok2 and ww or -1, ok2 and hh or -1)
      if d < 10 then
        local keys = {}
        for k in pairs(fields(w)) do if type(k)=="string" and not skip[k] then keys[#keys+1]=k end end
        table.sort(keys)
        for _,k in ipairs(keys) do
          local c = fields(w)[k]
          if isw(c) then if not seen[c] then seen[c]=true; queue[#queue+1] = {c, path.."."..k, d+1} end
          elseif type(c)=="table" then
            for i=1,#c do local e=c[i]; if isw(e) and not seen[e] then seen[e]=true; queue[#queue+1] = {e, path.."."..k.."["..i.."]", d+1} end end
          end
        end
      end
    end
  end
  return table.concat(out, "\n")
end)(true)
""";

    private static readonly (string Name, string Open, string Close)[] ParitySteps =
    [
        // the daily schedule pops up once a day at login; the reference captures were taken with it closed
        ("hud", "ADDON:ShowContent(UIC_EVENT_CENTER, false); local w = ADDON:GetContent(UIC_EVENT_CENTER); if w then w:Show(false) end", ""),
        ("bag", "ADDON:ShowContent(UIC_BAG, true)", "ADDON:ShowContent(UIC_BAG, false)"),
        ("character", "ADDON:ShowContent(UIC_CHARACTER_INFO, true)", "ADDON:ShowContent(UIC_CHARACTER_INFO, false)"),
        ("skill", "ADDON:ShowContent(UIC_SKILL, true)", "ADDON:ShowContent(UIC_SKILL, false)"),
        ("quest", "ADDON:ShowContent(UIC_QUEST_LIST, true)", "ADDON:ShowContent(UIC_QUEST_LIST, false)"),
        ("worldmap", "ADDON:ShowContent(UIC_WORLDMAP, true)", "ADDON:ShowContent(UIC_WORLDMAP, false)"),
        ("esc", "ADDON:ShowContent(UIC_SYSTEM_CONFIG_FRAME, true)", "ADDON:ShowContent(UIC_SYSTEM_CONFIG_FRAME, false)"),
        ("target", "@target", ""),
    ];

    private readonly string? _parityDir = System.Environment.GetEnvironmentVariable("X2_PARITY");
    private double _parityClock = -1;
    private int _parityStep;

    private void ParityTick(double delta)
    {
        if (_parityDir == null || !InWorld || _session.Host is not { } host) return;
        if (_parityClock < 0)
        {
            _parityClock = 0;
            DirAccess.MakeDirRecursiveAbsolute(_parityDir);
        }
        _parityClock += delta;
        var settle = double.TryParse(System.Environment.GetEnvironmentVariable("X2_PARITY_DELAY"), out var d) ? d : 40;
        // every step: open at t, capture at t + 2 s, close at t + 2.5 s
        var t = _parityClock - settle - _parityStep * 3.0;
        if (t < 0) return;
        if (_parityStep >= ParitySteps.Length)
        {
            Emit("[x2] parity capture done");
            GetTree().Quit();
            _parityStep = int.MaxValue / 8;
            return;
        }
        var (name, open, close) = ParitySteps[_parityStep];
        var phase = t < 2 ? 0 : t < 2.5 ? 1 : 2;
        if (phase == 0 && !_parityOpened)
        {
            _parityOpened = true;
            if (open == "@target") ParityTarget();
            else if (open.Length > 0) host.RunString(open, "=parity");
        }
        else if (phase >= 1 && !_parityCaptured)
        {
            _parityCaptured = true;
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_parityDir, name + ".png"));
            if (host.RunString("PARITY_OUT = (function()\n" + ParityWalker + "\nend)()", "=parity_walk") && host.Lua.GetGlobal("PARITY_OUT") is string dump)
                System.IO.File.WriteAllText(System.IO.Path.Combine(_parityDir, name + ".txt"), dump);
        }
        else if (phase == 2)
        {
            if (close.Length > 0) host.RunString(close, "=parity");
            if (open == "@target") Bridge?.SetTarget(0);
            _parityStep++;
            _parityOpened = _parityCaptured = false;
        }
    }

    private bool _parityOpened, _parityCaptured;

    private void ParityTarget()
    {
        if (Bridge is not { } bridge || bridge.Get(bridge.PlayerId) is not { } me) return;
        var nearest = bridge.All.Where(u => u.Id != me.Id && u.Type != "doodad")
            .OrderBy(u => (u.X - me.X) * (u.X - me.X) + (u.Y - me.Y) * (u.Y - me.Y)).FirstOrDefault();
        if (nearest != null) bridge.SetTarget(nearest.Id);
    }
}
