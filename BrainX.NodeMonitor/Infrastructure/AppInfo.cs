using System.Reflection;

namespace BrainX.ServerManager.Infrastructure;

internal static class AppInfo
{
    public const string Product = "BrainX Server Manager";

    /// <summary>"2.0.1234+abc1234" in CI builds, "2.0.0-dev" locally.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Version without the "+commit" build metadata, for display.</summary>
    public static string ShortVersion => Version.Split('+')[0];
}
