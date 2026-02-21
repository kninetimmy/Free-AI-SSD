using SharpDX.DirectInput;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Listens for joystick/HOTAS button presses via DirectInput.
/// Works globally — even when another application (DCS, IL-2) has focus —
/// by acquiring devices with <c>CooperativeLevel.Background | NonExclusive</c>.
///
/// Polling runs on a dedicated background thread at ~120 Hz when active.
/// When PTT is not enabled, no polling occurs.
/// </summary>
public sealed class HotasInputService : IHotasInputService
{
    private readonly object _lock = new();
    private DirectInput? _directInput;
    private Joystick? _activeJoystick;
    private string? _targetDeviceName;
    private int _targetButtonIndex;

    private Thread? _pollThread;
    private volatile bool _running;
    private volatile bool _detecting;
    private Action<string, int>? _detectionCallback;

    // Edge detection: track previous button state to fire only on transitions
    private bool _previousButtonState;

    // Device reconnection: periodically re-scan when the target device is missing
    private DateTime _lastDeviceScan = DateTime.MinValue;
    private static readonly TimeSpan DeviceScanInterval = TimeSpan.FromSeconds(3);

    public event Action<string>? LogMessage;
    public event Action? PttButtonPressed;
    public event Action? PttButtonReleased;

    public bool IsRunning => _running;

    public IReadOnlyList<HotasDeviceInfo> GetConnectedDevices()
    {
        var di = EnsureDirectInput();
        var devices = new List<HotasDeviceInfo>();

        foreach (var instance in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
        {
            try
            {
                using var joystick = new Joystick(di, instance.InstanceGuid);
                devices.Add(new HotasDeviceInfo
                {
                    DeviceName = instance.InstanceName,
                    InstanceGuid = instance.InstanceGuid,
                    ButtonCount = joystick.Capabilities.ButtonCount
                });
            }
            catch
            {
                // Skip devices that fail to initialize
            }
        }

        return devices;
    }

    public void Start(string? deviceName, int buttonIndex)
    {
        lock (_lock)
        {
            if (_running) return;

            _targetDeviceName = deviceName;
            _targetButtonIndex = buttonIndex;
            _previousButtonState = false;
            _running = true;

            _pollThread = new Thread(PollLoop)
            {
                Name = "HOTAS-Input-Poll",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _pollThread.Start();

            Log($"HOTAS input started: device='{deviceName}', button={buttonIndex}");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
        }

        _pollThread?.Join(timeout: TimeSpan.FromSeconds(2));
        ReleaseJoystick();
        Log("HOTAS input stopped.");
    }

    public void BeginButtonDetection(Action<string, int> onButtonDetected)
    {
        lock (_lock)
        {
            _detectionCallback = onButtonDetected;
            _detecting = true;

            if (!_running)
            {
                _running = true;
                _pollThread = new Thread(PollLoop)
                {
                    Name = "HOTAS-Detection-Poll",
                    IsBackground = true
                };
                _pollThread.Start();
            }
        }

        Log("Button detection mode started. Press any HOTAS button...");
    }

    public void EndButtonDetection()
    {
        lock (_lock)
        {
            _detecting = false;
            _detectionCallback = null;

            // If we were only running for detection (no configured device), stop
            if (_targetDeviceName is null)
            {
                _running = false;
            }
        }

        Log("Button detection mode ended.");
    }

    public void Dispose()
    {
        _running = false;
        _detecting = false;
        _pollThread?.Join(timeout: TimeSpan.FromSeconds(2));
        ReleaseJoystick();

        lock (_lock)
        {
            _directInput?.Dispose();
            _directInput = null;
        }
    }

    // ─── Private ────────────────────────────────────────────────────────────

    private void PollLoop()
    {
        while (_running)
        {
            try
            {
                if (_detecting)
                {
                    PollForDetection();
                }
                else
                {
                    PollConfiguredButton();
                }
            }
            catch (SharpDX.SharpDXException ex) when (ex.HResult == unchecked((int)0x8007001E)) // DIERR_INPUTLOST
            {
                Log("Joystick input lost — device may have been disconnected.");
                ReleaseJoystick();
            }
            catch (SharpDX.SharpDXException ex) when (ex.HResult == unchecked((int)0x80040154)) // CLASS_NOT_REGISTERED
            {
                Log("DirectInput device class not registered. Will retry.");
                ReleaseJoystick();
            }
            catch (Exception ex)
            {
                Log($"HOTAS poll error: {ex.Message}");
                ReleaseJoystick();
            }

            Thread.Sleep(8); // ~120 Hz
        }
    }

    private void PollConfiguredButton()
    {
        var joystick = EnsureJoystickAcquired();
        if (joystick is null) return;

        joystick.Poll();
        var state = joystick.GetCurrentState();
        var buttons = state.Buttons;

        if (_targetButtonIndex < 0 || _targetButtonIndex >= buttons.Length) return;

        bool currentState = buttons[_targetButtonIndex];

        if (currentState && !_previousButtonState)
        {
            PttButtonPressed?.Invoke();
        }
        else if (!currentState && _previousButtonState)
        {
            PttButtonReleased?.Invoke();
        }

        _previousButtonState = currentState;
    }

    private void PollForDetection()
    {
        var di = EnsureDirectInput();
        var callback = _detectionCallback;
        if (callback is null) return;

        foreach (var instance in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
        {
            if (!_detecting) break;

            try
            {
                using var joystick = new Joystick(di, instance.InstanceGuid);
                joystick.Properties.BufferSize = 128;
                joystick.Acquire();
                joystick.Poll();

                var state = joystick.GetCurrentState();
                var buttons = state.Buttons;

                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i])
                    {
                        callback(instance.InstanceName, i);
                        return; // Only report the first detected button
                    }
                }
            }
            catch
            {
                // Skip devices that can't be polled during detection
            }
        }
    }

    private Joystick? EnsureJoystickAcquired()
    {
        if (_activeJoystick is not null)
            return _activeJoystick;

        // Rate-limit device scanning to avoid hammering DirectInput
        if (DateTime.UtcNow - _lastDeviceScan < DeviceScanInterval)
            return null;

        _lastDeviceScan = DateTime.UtcNow;

        var di = EnsureDirectInput();
        DeviceInstance? match = null;

        foreach (var instance in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
        {
            if (_targetDeviceName is not null &&
                string.Equals(instance.InstanceName, _targetDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                match = instance;
                break;
            }
        }

        if (match is null)
        {
            // Only log once per scan interval to avoid log spam
            return null;
        }

        try
        {
            var joystick = new Joystick(di, match.InstanceGuid);
            joystick.Properties.BufferSize = 128;
            // Background + NonExclusive: works even when DCS/IL-2 has focus
            joystick.SetCooperativeLevel(IntPtr.Zero, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
            joystick.Acquire();

            _activeJoystick = joystick;
            _previousButtonState = false;
            Log($"Joystick acquired: {match.InstanceName} ({joystick.Capabilities.ButtonCount} buttons)");
            return joystick;
        }
        catch (Exception ex)
        {
            Log($"Failed to acquire joystick '{match.InstanceName}': {ex.Message}");
            return null;
        }
    }

    private void ReleaseJoystick()
    {
        lock (_lock)
        {
            try { _activeJoystick?.Unacquire(); } catch { }
            try { _activeJoystick?.Dispose(); } catch { }
            _activeJoystick = null;
            _previousButtonState = false;
        }
    }

    private DirectInput EnsureDirectInput()
    {
        lock (_lock)
        {
            _directInput ??= new DirectInput();
            return _directInput;
        }
    }

    private void Log(string message) => LogMessage?.Invoke($"[HOTAS] {message}");
}
