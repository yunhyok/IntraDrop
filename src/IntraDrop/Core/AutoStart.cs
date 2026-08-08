using Microsoft.Win32;

namespace IntraDrop.Core;

public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "IntraDrop";

    public static void Apply(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key == null) return;

        if (enable)
            key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
        else if (key.GetValue(ValueName) != null)
            key.DeleteValue(ValueName);
    }
}
