using System;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Owns the simulated ET clock. UTC is used only for user entry and display;
/// the clock advances in ephemeris seconds, independently of Unity's frame rate.
/// </summary>
[DisallowMultipleComponent]
public sealed class SimulationTime : MonoBehaviour
{
    private enum TimeStepUnit
    {
        Hour,
        Day
    }

    [Header("Data")]
    [SerializeField] private EphemerisDataProvider dataProvider;

    [Header("Initial clock")]
    [SerializeField] private bool playOnStart;

    [Header("Playback")]
    [Tooltip("Simulated seconds advanced for each real second. The on-screen slider covers 1 minute/s through 30 days/s.")]
    [SerializeField] private double simulationSecondsPerRealSecond = 60.0;

    private const double SecondsPerDay = 86400.0;
    private const double SecondsPerHour = 3600.0;
    private const double MinimumPlaybackRate = 60.0;
    private const double MaximumPlaybackRate = 30.0 * SecondsPerDay;
    private static readonly float MinimumPlaybackLog10 = (float)Math.Log10(MinimumPlaybackRate);
    private static readonly float MaximumPlaybackLog10 = (float)Math.Log10(MaximumPlaybackRate);
    private static readonly TimeStepUnit[] TimeStepUnits =
    {
        TimeStepUnit.Hour,
        TimeStepUnit.Day
    };

    private double _currentEtSeconds;
    private EphemerisState _currentState;
    private bool _isReady;
    private bool _isPlaying;
    private string _timeInput = "";
    private string _stepCountInput = "1";
    private string _message = "Loading ephemeris cache...";
    private TimeStepUnit _selectedStepUnit = TimeStepUnit.Hour;
    private bool _showStepUnitMenu;

    public event Action<EphemerisState> StateUpdated;
    public event Action UtcTimeSet;

    public bool IsReady => _isReady;
    public bool IsPlaying => _isPlaying;
    public double CurrentEtSeconds => _currentEtSeconds;
    public DateTime CurrentUtc => _isReady ? _currentState.UtcDateTime : DateTime.MinValue;
    public double SimulationSecondsPerRealSecond => simulationSecondsPerRealSecond;
    public EphemerisState CurrentState => _currentState;
    public EphemerisDataProvider DataProvider => dataProvider;

    private void Awake()
    {
        SetPlaybackRate(simulationSecondsPerRealSecond);
        if (dataProvider == null)
            dataProvider = GetComponent<EphemerisDataProvider>();
        if (dataProvider == null)
            dataProvider = FindFirstObjectByType<EphemerisDataProvider>();
    }

    private void Start()
    {
        if (dataProvider == null)
        {
            _message = "EphemerisDataProvider is not assigned.";
            Debug.LogError($"[{nameof(SimulationTime)}] {_message}", this);
            return;
        }

        if (!dataProvider.TryLoad(out string loadError))
        {
            _message = "Could not load DE440 cache: " + loadError;
            Debug.LogError($"[{nameof(SimulationTime)}] {_message}", this);
            return;
        }

        DateTime requestedUtc = DateTime.UtcNow;
        if (!SetUtc(requestedUtc, out string timeError))
        {
            DateTime fallback = requestedUtc < dataProvider.StartUtc
                ? dataProvider.StartUtc
                : dataProvider.EndUtc;
            if (!SetUtc(fallback, out timeError))
            {
                _message = "Could not initialize simulation time: " + timeError;
                Debug.LogError($"[{nameof(SimulationTime)}] {_message}", this);
                return;
            }
            _message = "Current UTC is outside the ephemeris cache range. Started at " +
                       fallback.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC.";
            Debug.LogWarning(
                $"[{nameof(SimulationTime)}] Current UTC is outside cache coverage. " +
                $"Using {fallback:yyyy-MM-dd HH:mm:ss} UTC.", this);
        }

        _timeInput = _currentState.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        _isReady = true;
        _isPlaying = playOnStart;
        if (string.IsNullOrEmpty(_message) || _message.StartsWith("Time set", StringComparison.Ordinal))
            _message = "DE440s ephemeris cache ready.";
        PublishState();
    }

    private void Update()
    {
        if (!_isReady || !_isPlaying)
            return;

        double nextEt = _currentEtSeconds + Time.unscaledDeltaTime *
                        simulationSecondsPerRealSecond;
        SetEt(nextEt);
    }

    public void SetPlaying(bool playing)
    {
        if (_isReady)
            _isPlaying = playing;
    }

    public void TogglePlaying() => SetPlaying(!_isPlaying);

    public void SetPlaybackRate(double secondsPerRealSecond)
    {
        if (double.IsNaN(secondsPerRealSecond) || double.IsInfinity(secondsPerRealSecond) ||
            secondsPerRealSecond <= 0.0)
            return;
        simulationSecondsPerRealSecond = Math.Max(
            MinimumPlaybackRate, Math.Min(MaximumPlaybackRate, secondsPerRealSecond));
    }

    private void AdvanceBySeconds(double seconds)
    {
        if (_isReady && !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds > 0.0)
            SetEt(_currentEtSeconds + seconds);
    }

    private void AdvanceBySelectedUnits()
    {
        if (!_isReady)
            return;

        if (!int.TryParse(_stepCountInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ||
            count <= 0)
        {
            _message = "Enter a positive whole number of time units.";
            return;
        }

        switch (_selectedStepUnit)
        {
            case TimeStepUnit.Hour:
                AdvanceBySeconds(count * SecondsPerHour);
                break;
            case TimeStepUnit.Day:
                AdvanceBySeconds(count * SecondsPerDay);
                break;
        }
    }

    public bool SetUtc(DateTime utc, out string error)
    {
        error = null;
        if (dataProvider == null)
        {
            error = "EphemerisDataProvider is not assigned.";
            return false;
        }

        if (!dataProvider.TryGetAtUtc(utc, out EphemerisState state, out error))
            return false;

        _currentEtSeconds = state.etSeconds;
        _currentState = state;
        _isReady = true;
        _message = string.Empty;
        UtcTimeSet?.Invoke();
        PublishState();
        return true;
    }

    public bool TryGetCurrentState(out EphemerisState state)
    {
        state = _currentState;
        return _isReady;
    }

    private void SetEt(double etSeconds)
    {
        etSeconds = Math.Max(dataProvider.StartEtSeconds, Math.Min(dataProvider.EndEtSeconds, etSeconds));
        if (dataProvider.TryGetAtEt(etSeconds, out EphemerisState state, out string error))
        {
            _currentEtSeconds = etSeconds;
            _currentState = state;
            PublishState();
            if (_currentEtSeconds >= dataProvider.EndEtSeconds)
                _isPlaying = false;
        }
        else
        {
            _message = "Could not evaluate ephemeris cache: " + error;
            _isPlaying = false;
        }
    }

    private void PublishState()
    {
        if (_isReady)
            StateUpdated?.Invoke(_currentState);
    }

    private static bool TryParseUtc(string text, out DateTime utc)
    {
        string[] formats =
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm:ss'Z'"
        };
        return DateTime.TryParseExact(
            text.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out utc);
    }

    private static float PlaybackRateToSlider(double secondsPerRealSecond)
    {
        float logRate = Mathf.Log10((float)Math.Max(
            MinimumPlaybackRate, Math.Min(MaximumPlaybackRate, secondsPerRealSecond)));
        return Mathf.InverseLerp(MinimumPlaybackLog10, MaximumPlaybackLog10, logRate);
    }

    private static double SliderToPlaybackRate(float sliderValue)
    {
        float logRate = Mathf.Lerp(MinimumPlaybackLog10, MaximumPlaybackLog10, sliderValue);
        return Math.Pow(10.0, logRate);
    }

    private static string FormatPlaybackRate(double secondsPerRealSecond)
    {
        if (secondsPerRealSecond >= SecondsPerDay)
            return (secondsPerRealSecond / SecondsPerDay).ToString("F1", CultureInfo.InvariantCulture) + " days/s";
        if (secondsPerRealSecond >= 3600.0)
            return (secondsPerRealSecond / 3600.0).ToString("F1", CultureInfo.InvariantCulture) + " hours/s";
        if (secondsPerRealSecond >= 60.0)
            return (secondsPerRealSecond / 60.0).ToString("F1", CultureInfo.InvariantCulture) + " min/s";
        return secondsPerRealSecond.ToString("F1", CultureInfo.InvariantCulture) + " sec/s";
    }

    /// <summary>Draws the existing orbital controls inside the shared expandable panel stack.</summary>
    public void DrawControls()
    {
        GUILayout.Label("Orbital simulation · JPL DE440s");

        if (!_isReady)
        {
            GUILayout.Label(_message);
            return;
        }

        GUILayout.Label($"UTC  {_currentState.UtcDateTime:yyyy-MM-dd HH:mm:ss}");
        GUILayout.Label($"ET   {_currentEtSeconds:F3} s past J2000 TDB");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(_isPlaying ? "Pause" : "Play", GUILayout.Width(72f)))
            TogglePlaying();
        if (GUILayout.Button("+1 hour", GUILayout.Width(82f)))
            AdvanceBySeconds(SecondsPerHour);
        if (GUILayout.Button("+1 day", GUILayout.Width(82f)))
            AdvanceBySeconds(SecondsPerDay);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Speed", GUILayout.Width(45f));
        float speedSlider = GUILayout.HorizontalSlider(
            PlaybackRateToSlider(simulationSecondsPerRealSecond), 0f, 1f, GUILayout.Width(210f));
        SetPlaybackRate(SliderToPlaybackRate(speedSlider));
        GUILayout.Label(FormatPlaybackRate(simulationSecondsPerRealSecond), GUILayout.Width(120f));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("1 min/s", GUILayout.Width(70f));
        GUILayout.FlexibleSpace();
        GUILayout.Label("30 days/s", GUILayout.Width(75f));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Go forward", GUILayout.Width(78f));
        _stepCountInput = GUILayout.TextField(_stepCountInput, GUILayout.Width(64f));
        if (GUILayout.Button(_selectedStepUnit + " ▼", GUILayout.Width(100f)))
            _showStepUnitMenu = !_showStepUnitMenu;
        if (GUILayout.Button("Advance", GUILayout.Width(76f)))
            AdvanceBySelectedUnits();
        GUILayout.EndHorizontal();

        if (_showStepUnitMenu)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(142f);
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(100f));
            foreach (TimeStepUnit unit in TimeStepUnits)
            {
                if (GUILayout.Button(unit.ToString()))
                {
                    _selectedStepUnit = unit;
                    _showStepUnitMenu = false;
                }
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        GUILayout.BeginHorizontal();
        _timeInput = GUILayout.TextField(_timeInput, GUILayout.Width(235f));
        if (GUILayout.Button("Set UTC", GUILayout.Width(72f)))
        {
            if (TryParseUtc(_timeInput, out DateTime selectedUtc))
            {
                if (!SetUtc(selectedUtc, out string error))
                    _message = error;
            }
            else
            {
                _message = "Enter UTC as yyyy-MM-dd HH:mm:ss.";
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.Label("Daily DE440s interpolation · IAU Earth spin · visualization only.");
        GUILayout.Label("Sun fixed; Sun–Earth distance compressed; Earth–Moon distance retained.");
        if (!string.IsNullOrEmpty(_message))
            GUILayout.Label(_message);
    }
}
