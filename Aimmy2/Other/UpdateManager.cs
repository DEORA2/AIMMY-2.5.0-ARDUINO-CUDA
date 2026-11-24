﻿using Whales2.Other;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;

namespace Other
{
    /// <summary>
    /// Checks this fork's GitHub releases for a newer version and, if the user agrees, downloads the zip,
    /// copies it over the app folder (the bin folder with models and settings is not in the zip, so it is kept)
    /// and restarts.
    /// </summary>
    internal class UpdateManager : IDisposable
    {
        private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(30) };

        private static int CompareVersions(string currentVersion, string latestVersion)
        {
            currentVersion = currentVersion.TrimStart('v', 'V');
            latestVersion = latestVersion.TrimStart('v', 'V');

            if (Version.TryParse(currentVersion, out var current) && Version.TryParse(latestVersion, out var latest))
                return current.CompareTo(latest);

            return string.Compare(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks for a newer release. With <paramref name="quiet"/> (the check on start) nothing is shown
        /// unless an update is available: no "up to date" message and no error when offline.
        /// </summary>
        public async Task CheckForUpdate(string currentVersion, bool quiet = false)
        {
            string latestVersion, zipUrl;
            long sizeBytes;
            try
            {
                using var githubManager = new GithubManager();
                (latestVersion, zipUrl, sizeBytes) = await githubManager.GetLatestReleaseZip(AppInfo.RepoOwner, AppInfo.RepoName);
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, $"Couldn't check for updates: {ex.Message}", !quiet);
                return;
            }

            int comparison = CompareVersions(currentVersion, latestVersion);
            if (comparison == 0)
            {
                LogManager.Log(LogManager.LogLevel.Info, $"You are up to date (v{currentVersion.TrimStart('v', 'V')}).", !quiet);
                return;
            }
            if (comparison > 0)
            {
                LogManager.Log(LogManager.LogLevel.Info, $"You are running a newer version (v{currentVersion.TrimStart('v', 'V')}) than the latest release ({latestVersion}).", !quiet);
                return;
            }

            var answer = MessageBox.Show(
                $"Whales {latestVersion} is available (you have v{currentVersion.TrimStart('v', 'V')}).\n\n" +
                $"Download it ({sizeBytes / (1024 * 1024)} MB) and install it now? Whales will close and restart.\n" +
                "Your models and settings are kept.\n\nChoose No to see what's new on GitHub instead." +
                (quiet ? "\n\n(You can turn off this check in Settings.)" : ""),
                "Update available", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);

            if (answer == MessageBoxResult.No)
            {
                Process.Start(new ProcessStartInfo { FileName = AppInfo.ReleaseUrl(latestVersion), UseShellExecute = true });
                return;
            }
            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                await DoUpdate(zipUrl);
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, $"Update failed: {ex.Message}", true, 8000);
            }
        }

        private async Task DoUpdate(string zipUrl)
        {
            LogManager.Log(LogManager.LogLevel.Info, "Downloading the update. Whales will restart when it's done...", true, 8000);

            string tempPath = Path.GetTempPath();
            string localZipPath = Path.Combine(tempPath, "WhalesUpdate.zip");
            string extractPath = Path.Combine(tempPath, "WhalesUpdate");

            using (var response = await client.GetAsync(new Uri(zipUrl), HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(localZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await stream.CopyToAsync(fileStream);
            }

            string mainAppPath = Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't find the running exe.");
            string mainAppDir = Path.GetDirectoryName(mainAppPath) ?? throw new InvalidOperationException("Couldn't find the app folder.");
            string exeName = Path.GetFileName(mainAppPath);

            // Release zips keep everything inside one folder (e.g. Whales-v2.5.5-Debug-x64\), so copy
            // from whichever folder in the zip holds the exe.
            string sourceDir = await Task.Run(() =>
            {
                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);
                ZipFile.ExtractToDirectory(localZipPath, extractPath, true);

                string? exeInZip = Directory.GetFiles(extractPath, exeName, SearchOption.AllDirectories).FirstOrDefault();
                return exeInZip != null ? Path.GetDirectoryName(exeInZip)! : extractPath;
            });

            if (!File.Exists(Path.Combine(sourceDir, exeName)))
                throw new InvalidOperationException($"The download doesn't contain {exeName}.");

            // Waits for this process to exit (up to ~30 s) so its files aren't locked, copies the new files
            // over the app folder, restarts the app and cleans up.
            int pid = Environment.ProcessId;
            string batchScriptPath = Path.Combine(mainAppDir, "update.bat");
            using (var sw = new StreamWriter(batchScriptPath))
            {
                sw.WriteLine("@echo off");
                sw.WriteLine("echo Updating Whales, please wait...");
                sw.WriteLine("set /a tries=0");
                sw.WriteLine(":wait");
                sw.WriteLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
                sw.WriteLine("if not errorlevel 1 if %tries% lss 30 (set /a tries+=1 & timeout /t 1 /nobreak >nul & goto wait)");
                sw.WriteLine("timeout /t 1 /nobreak >nul");
                sw.WriteLine($"xcopy \"{sourceDir}\" \"{mainAppDir}\" /E /Y /I /Q >nul");
                sw.WriteLine($"start \"\" /D \"{mainAppDir}\" \"{mainAppPath}\"");
                sw.WriteLine($"del /f /q \"{localZipPath}\"");
                sw.WriteLine($"rd /s /q \"{extractPath}\"");
                sw.WriteLine("(goto) 2>nul & del /f /q \"%~f0\"");
            }

            Process.Start(new ProcessStartInfo { FileName = batchScriptPath, WorkingDirectory = mainAppDir, UseShellExecute = true });

            // Close normally so settings are saved; the script waits for the process to exit.
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
