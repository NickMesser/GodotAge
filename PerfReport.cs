using Godot;

namespace AAEmu.GodotViewer;

/// <summary>Periodically prints low-overhead engine and renderer counters for a viewport.</summary>
public sealed class PerfReport : IDisposable
{
    private readonly Rid _viewport;
    private readonly double _intervalSeconds;
    private double _lastTickSeconds;
    private double _intervalSecondsAccumulated;
    private long _intervalFrames;
    private double _gpuMillisecondsSum;
    private double _cpuMillisecondsSum;
    private int _gpuSamples;
    private int _cpuSamples;
    private double _drawCallsSum;
    private double _objectsSum;
    private double _primitivesSum;
    private double _nodesSum;
    private int _rendererSamples;
    private bool _hasTick;
    private bool _hasState;
    private bool _intervalSettled;
    private double _settledSeconds;
    private long _settledFrames;
    private double _minimumIntervalFps = double.PositiveInfinity;
    private bool _finished;

    public PerfReport(Viewport viewport, double intervalSeconds = 2.0)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        _viewport = viewport.GetViewportRid();
        _intervalSeconds = Math.Max(0.25, intervalSeconds);
        RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);
        GD.Print($"[perf] enabled; reporting every {_intervalSeconds:F1} s (GPU/CPU timings may be unavailable on some renderers)");
    }

    /// <summary>Records a frame interval. Scene transitions are excluded from the settled summary.</summary>
    public void Tick(bool settled)
    {
        if (_finished)
            return;

        var now = Time.GetTicksUsec() / 1_000_000.0;
        var elapsed = _hasTick ? now - _lastTickSeconds : 0.0;
        _lastTickSeconds = now;
        _hasTick = true;

        if (!_hasState || settled != _intervalSettled)
        {
            if (_hasState && _intervalSettled && _intervalFrames > 0 && _intervalSecondsAccumulated > 0)
                _minimumIntervalFps = Math.Min(_minimumIntervalFps, _intervalFrames / _intervalSecondsAccumulated);
            _hasState = true;
            _intervalSettled = settled;
            _intervalSecondsAccumulated = 0;
            _intervalFrames = 0;
            ResetMetrics();
            GD.Print(settled ? "[perf] scene settled; summary sampling started" : "[perf] scene no longer settled; summary sampling paused");
            return;
        }

        if (elapsed <= 0)
            return;

        _intervalSecondsAccumulated += elapsed;
        _intervalFrames++;
        if (settled)
        {
            _settledSeconds += elapsed;
            _settledFrames++;
        }
        SampleMetrics();

        if (_intervalSecondsAccumulated < _intervalSeconds)
            return;

        PrintInterval(_intervalSecondsAccumulated, _intervalFrames, settled);
        _intervalSecondsAccumulated = 0;
        _intervalFrames = 0;
        ResetMetrics();
    }

    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;

        if (_intervalSettled && _intervalFrames > 0 && _intervalSecondsAccumulated > 0)
            _minimumIntervalFps = Math.Min(_minimumIntervalFps, _intervalFrames / _intervalSecondsAccumulated);

        if (_settledFrames > 0 && _settledSeconds > 0)
        {
            var averageFps = _settledFrames / _settledSeconds;
            var minimumFps = double.IsFinite(_minimumIntervalFps) ? _minimumIntervalFps : averageFps;
            GD.Print($"[perf] summary settled {_settledSeconds:F1} s: average {averageFps:F1} fps, minimum interval {minimumFps:F1} fps");
        }
        else
        {
            GD.Print("[perf] summary unavailable: scene did not reach the settled state");
        }

        RenderingServer.ViewportSetMeasureRenderTime(_viewport, false);
    }

    public void Dispose() => Finish();

    private void PrintInterval(double seconds, long frames, bool settled)
    {
        var fps = frames / seconds;
        var frameMilliseconds = seconds * 1000.0 / Math.Max(1, frames);
        if (settled)
            _minimumIntervalFps = Math.Min(_minimumIntervalFps, fps);

        var gpuMilliseconds = _gpuSamples > 0 ? _gpuMillisecondsSum / _gpuSamples : 0;
        var cpuMilliseconds = _cpuSamples > 0 ? _cpuMillisecondsSum / _cpuSamples : 0;
        var divisor = Math.Max(1, _rendererSamples);
        var drawCalls = _drawCallsSum / divisor;
        var objects = _objectsSum / divisor;
        var primitives = _primitivesSum / divisor;
        var nodes = _nodesSum / divisor;

        GD.Print($"[perf] {(settled ? "settled" : "loading")}: {fps:F1} fps, {frameMilliseconds:F2} ms/frame, " +
                 $"GPU {FormatMilliseconds(gpuMilliseconds)}, CPU render {FormatMilliseconds(cpuMilliseconds)}, " +
                 $"{drawCalls:F0} draw calls, {objects:F0} objects, {primitives:F0} triangles/primitives, {nodes:F0} nodes");
    }

    private static string FormatMilliseconds(double milliseconds) =>
        milliseconds > 0 && double.IsFinite(milliseconds) ? $"{milliseconds:F2} ms" : "unavailable";

    private void SampleMetrics()
    {
        var gpuMilliseconds = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport);
        if (gpuMilliseconds > 0 && double.IsFinite(gpuMilliseconds))
        {
            _gpuMillisecondsSum += gpuMilliseconds;
            _gpuSamples++;
        }
        var viewportCpuMilliseconds = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport);
        if (viewportCpuMilliseconds > 0 && double.IsFinite(viewportCpuMilliseconds))
        {
            _cpuMillisecondsSum += viewportCpuMilliseconds + RenderingServer.GetFrameSetupTimeCpu();
            _cpuSamples++;
        }
        _drawCallsSum += Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        _objectsSum += Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        _primitivesSum += Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        _nodesSum += Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        _rendererSamples++;
    }

    private void ResetMetrics()
    {
        _gpuMillisecondsSum = 0;
        _cpuMillisecondsSum = 0;
        _gpuSamples = 0;
        _cpuSamples = 0;
        _drawCallsSum = 0;
        _objectsSum = 0;
        _primitivesSum = 0;
        _nodesSum = 0;
        _rendererSamples = 0;
    }
}
