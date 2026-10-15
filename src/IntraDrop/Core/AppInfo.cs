namespace IntraDrop.Core;

public static class AppInfo
{
    public const string Name = "IntraDrop";

    /// <summary>csproj &lt;Version&gt; 에서 읽는다. 상수로 두면 릴리스마다 갱신을 빠뜨린다 (1.9.4 는 1.8.2 로 표시됐다).</summary>
    public static readonly string Version =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static readonly string DisplayName = Name + " " + Version;
}
