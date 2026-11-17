using Microsoft.Win32;

namespace IntraDrop.Core;

public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "IntraDrop";

    /// <summary>Run 키에 항목이 있으면 true. 인스톨러가 "자동 실행" 작업을 선택했을 때 만든다.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) != null;
    }

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
