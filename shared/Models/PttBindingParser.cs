namespace FreeAiSsd.Shared.Models;

public static class PttBindingParser
{
    /// <summary>
    /// Parses a HOTAS PTT binding string of the form "DeviceName|ButtonIndex".
    /// Returns deviceName=null if the binding is missing or malformed.
    /// </summary>
    public static void ParseHotas(string? binding, out string? deviceName, out int buttonIndex)
    {
        deviceName = null;
        buttonIndex = 0;
        var segments = (binding ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2 && int.TryParse(segments[1], out var idx))
        {
            deviceName = segments[0];
            buttonIndex = idx;
        }
    }
}
