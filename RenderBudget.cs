namespace AAEmu.GodotViewer;

/// <summary>
/// Paces how fast worker threads create Godot resources (meshes, textures, materials, MultiMeshes). Resources made off
/// the main thread queue their GPU work for the main thread, which runs all of it in the next frame; without a limit the
/// loaders queue it faster than a frame can drain it and the window freezes until loading ends. The main thread hands
/// out an allowance every frame, larger while frames stay fast and smaller when they get slow.
/// </summary>
internal static class RenderBudget
{
    private const double TargetFrameMs = 33.0;
    private const int MinPerFrame = 4;
    private const int MaxPerFrame = 1024;

    private static readonly object Gate = new(); // Monitor.Wait needs a monitor lock
    private static int _perFrame = 48;
    private static int _available = 48;
    private static bool _unlimited;
    private static int _mainThreadId = -1;

    /// <summary>Main thread, once at startup.</summary>
    public static void SetMainThread() => _mainThreadId = Environment.CurrentManagedThreadId;

    /// <summary>
    /// Any worker thread, before creating resources worth <paramref name="cost"/> (about one per mesh surface, texture,
    /// material or MultiMesh). Waits for the next frame's allowance when this one is used up. Never waits on the main thread.
    /// </summary>
    public static void Acquire(int cost = 1)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId)
            return;
        lock (Gate)
        {
            while (_available <= 0 && !_unlimited)
                Monitor.Wait(Gate);
            _available -= cost;
        }
    }

    /// <summary>Main thread, every frame: adapts the allowance to the last frame's duration and releases waiting workers.</summary>
    public static void NewFrame(double lastFrameMs)
    {
        lock (Gate)
        {
            if (lastFrameMs < TargetFrameMs * 0.75)
                _perFrame = Math.Min(MaxPerFrame, _perFrame + Math.Max(4, _perFrame / 4));
            else if (lastFrameMs > TargetFrameMs * 1.5)
                _perFrame = Math.Max(MinPerFrame, _perFrame / 2);
            // Carry a debt over (a big mesh can overdraw), but never bank unused allowance.
            _available = Math.Min(_available, 0) + _perFrame;
            Monitor.PulseAll(Gate);
        }
    }

    /// <summary>Main thread, at shutdown: lets every waiting worker through.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            _unlimited = true;
            Monitor.PulseAll(Gate);
        }
    }
}
