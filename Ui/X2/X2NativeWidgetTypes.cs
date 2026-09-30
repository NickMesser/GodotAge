#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>Registers the client-native widget names before the first UI script runs.</summary>
public static class X2NativeWidgetTypes
{
    private static bool _registered;
    public static IX2MapProvider? MapProvider { get; set; }
    public static AAEmu.GodotViewer.Maps.MapDataCatalog? MapCatalog { get; set; }

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        var types = UiRoot.WidgetTypes;
        types["chatwindow"] = (r, t, id, p) => new ChatWindowWidget(r, t, id, p);
        types["chatedit"] = (r, t, id, p) => new ChatEditWidget(r, t, id, p);
        types["chatmethodselector"] = (r, t, id, p) => new ChatMethodSelectorWidget(r, t, id, p);
        types["slot"] = (r, t, id, p) => new NativeSlotWidget(r, t, id, p);
        types["cooldownbutton"] = (r, t, id, p) => new NativeSlotWidget(r, t, id, p);
        types["folder"] = (r, t, id, p) => new FolderWidget(r, t, id, p);
        types["grid"] = (r, t, id, p) => new GridWidget(r, t, id, p);
        types["dynamiclist"] = (r, t, id, p) => new DynamicListWidget(r, t, id, p);
        types["line"] = (r, t, id, p) => new LineWidget(r, t, id, p);
        types["circlediagram"] = (r, t, id, p) => new CircleDiagramWidget(r, t, id, p);
        types["paintcolorpicker"] = (r, t, id, p) => new PaintColorPickerWidget(r, t, id, p);
        types["characternamelabel"] = (r, t, id, p) => new CharacterNameLabelWidget(r, t, id, p);
        types["megaphonechatedit"] = (r, t, id, p) => new MegaphoneChatEditWidget(r, t, id, p);
        types["unitframetooltip"] = (r, t, id, p) => new UnitFrameTooltipWidget(r, t, id, p);
        types["avi"] = (r, t, id, p) => new AviWidget(r, t, id, p);
        types["webview"] = (r, t, id, p) => new WebViewWidget(r, t, id, p);
        types["modelview"] = (r, t, id, p) => new ModelViewWidget(r, t, id, p);
        types["roadmap"] = NewMap;
        types["worldmap"] = NewMap;
    }

    private static Widget NewMap(UiRoot root, string type, string id, Widget? parent)
        => new MapWidget(root, type, id, parent) { Provider = MapProvider, Catalog = MapCatalog };
}
