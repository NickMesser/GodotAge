#nullable enable

using AAEmu.GodotViewer.Net;

namespace AAEmu.GodotViewer.Client;

public partial class OnlineSession
{
    private float _timeOfDayHour;
    private float _timeOfDaySpeed = EnvironmentPacketParserFamily.DefaultGameHourSpeed;
    private float _timeOfDayStart;
    private float _timeOfDayEnd = 24f;

    /// <summary>True after the first server TimeOfDay or DetailedTimeOfDay update.</summary>
    public bool HasTimeOfDay { get; private set; }

    /// <summary>Continuously advanced server game hour, wrapped through the detailed packet's bounds.</summary>
    public float TimeOfDayHour => _timeOfDayHour;

    /// <summary>Raised after a server clock update has been applied on the main thread.</summary>
    public event Action<TimeOfDayEvent>? TimeOfDayChanged;
    public event Action<bool>? SnowingChanged;
    public bool SnowingEverywhere { get; private set; }

    private void AdvanceTimeOfDay(double delta)
    {
        if (!HasTimeOfDay || _timeOfDaySpeed <= 0f)
            return;
        _timeOfDayHour += (float)delta * _timeOfDaySpeed;
        if (_timeOfDayHour > _timeOfDayEnd)
            _timeOfDayHour = _timeOfDayStart;
        if (_timeOfDayHour < _timeOfDayStart)
            _timeOfDayHour = _timeOfDayEnd;
    }

    /// <summary>
    /// Viewer smoothing policy: the first update is forced, small backward corrections wait, and sub-hour corrections
    /// blend five percent of the current error instead of producing a visible jump between periodic server updates.
    /// </summary>
    private void ApplyTimeOfDay(TimeOfDayEvent value)
    {
        if (value.Detailed)
        {
            _timeOfDayStart = value.Start;
            _timeOfDayEnd = value.End;
            _timeOfDaySpeed = value.Speed;
            _timeOfDayHour = value.Hour;
        }
        else if (!HasTimeOfDay)
            _timeOfDayHour = value.Hour;
        else
        {
            var current = _timeOfDayHour;
            var reported = value.Hour;
            if (current < 2f && reported > 22f) current += 24f;
            else if (reported < 2f && current > 22f) reported += 24f;
            var difference = reported - current;
            var magnitude = MathF.Abs(difference);
            if (difference < 0f && magnitude < 0.1f)
                return;
            _timeOfDayHour = magnitude < 1f ? reported + (current - reported) * 0.05f : reported;
            if (_timeOfDayHour > 24f)
                _timeOfDayHour -= 24f;
        }

        HasTimeOfDay = true;
        TimeOfDayChanged?.Invoke(value);
    }

    private void ApplySnowingEverywhere(bool enabled)
    {
        SnowingEverywhere = enabled;
        SnowingChanged?.Invoke(enabled);
    }
}
