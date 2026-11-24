using System.Reflection;

namespace Whales2.Other
{
    /// <summary>Where this build comes from: its version (from the project's Version) and its GitHub repository.</summary>
    internal static class AppInfo
    {
        public const string RepoOwner = "DEORA2";
        public const string RepoName = "AIMMY-2.5.0-ARDUINO-CUDA";
        public const string RepoUrl = "https://github.com/" + RepoOwner + "/" + RepoName;

        /// <summary>Version as "major.minor.build", e.g. "2.5.5".</summary>
        public static string Version { get; } =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        public static string ReleaseUrl(string version) => $"{RepoUrl}/releases/tag/v{version.TrimStart('v', 'V')}";
    }
}
