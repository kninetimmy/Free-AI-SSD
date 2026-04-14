namespace FreeAiSsd.Shared.Client;

/// <summary>
/// Describes a connected joystick/HOTAS device detected via DirectInput.
/// </summary>
public sealed class HotasDeviceInfo
{
    public required string DeviceName { get; init; }
    public required Guid InstanceGuid { get; init; }
    public int ButtonCount { get; init; }
}

/// <summary>
/// Listens for joystick button presses globally (even when another application has focus)
/// using DirectInput. Designed for HOTAS push-to-talk in flight sims.
/// </summary>
public interface IHotasInputService : IDisposable
{
    event Action<string>? LogMessage;

    /// <summary>Raised when the configured PTT button is pressed (edge: up → down).</summary>
    event Action? PttButtonPressed;

    /// <summary>Raised when the configured PTT button is released (edge: down → up).</summary>
    event Action? PttButtonReleased;

    bool IsRunning { get; }

    /// <summary>
    /// Starts polling the configured joystick device for button state changes.
    /// Uses a background thread with ~8ms poll interval (~120 Hz).
    /// </summary>
    void Start(string? deviceName, int buttonIndex);

    void Stop();

    /// <summary>Returns all currently connected joystick/HOTAS devices.</summary>
    IReadOnlyList<HotasDeviceInfo> GetConnectedDevices();

    /// <summary>
    /// Enters detection mode: polls all connected devices and invokes the callback
    /// when any button is pressed. Used by the configuration UI for "press any button" assignment.
    /// Call <see cref="EndButtonDetection"/> to exit detection mode.
    /// </summary>
    void BeginButtonDetection(Action<string, int> onButtonDetected);

    /// <summary>Exits button detection mode and returns to normal operation.</summary>
    void EndButtonDetection();
}
