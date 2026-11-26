using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Whales2.MouseMovementLibraries.SketchBuilder
{
    /// <summary>
    /// Saves sketches under %LOCALAPPDATA%\Whales\sketches and compiles / uploads them with the Arduino IDE's
    /// command line (Arduino IDE 1.x arduino_debug.exe, or arduino-cli if it's on the PATH or next to IDE 2.x).
    /// </summary>
    internal static class ArduinoUploader
    {
        public const string Fqbn = "arduino:avr:leonardo";

        public static string SketchRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Whales", "sketches");

        /// <summary>Writes the sketch to its own folder (the Arduino tools need folder name == file name). Returns the .ino path.</summary>
        public static string Save(string name, string code)
        {
            string dir = Path.Combine(SketchRoot, name);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".ino");
            File.WriteAllText(path, code, new UTF8Encoding(false));
            return path;
        }

        private enum Tool { None, Ide1, Cli }

        private static (Tool tool, string? path) FindTool()
        {
            string? pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string? pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // arduino-cli: on the PATH, or bundled inside Arduino IDE 2.x
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    string cli = Path.Combine(dir.Trim(), "arduino-cli.exe");
                    if (File.Exists(cli)) return (Tool.Cli, cli);
                }
                catch { }
            }
            foreach (var root in new[] { Path.Combine(local, "Programs", "Arduino IDE"), pf == null ? null : Path.Combine(pf, "Arduino IDE") })
            {
                if (root == null) continue;
                string cli = Path.Combine(root, "resources", "app", "lib", "backend", "resources", "arduino-cli.exe");
                if (File.Exists(cli)) return (Tool.Cli, cli);
            }

            // Arduino IDE 1.x
            foreach (var root in new[] { pf86, pf })
            {
                if (root == null) continue;
                string ide = Path.Combine(root, "Arduino", "arduino_debug.exe");
                if (File.Exists(ide)) return (Tool.Ide1, ide);
            }
            return (Tool.None, null);
        }

        public static bool IsAvailable => FindTool().tool != Tool.None;

        public sealed record Outcome(bool Success, string Output);

        /// <summary>Compiles the sketch for the Leonardo; with a port, also uploads it.</summary>
        public static async Task<Outcome> RunAsync(string inoPath, string? uploadPort)
        {
            var (tool, exe) = FindTool();
            if (tool == Tool.None || exe == null)
                return new Outcome(false, "No Arduino IDE found. Install the Arduino IDE, or save the sketch and upload it yourself.");

            string args = tool == Tool.Ide1
                ? (uploadPort == null
                    ? $"--verify --board {Fqbn} \"{inoPath}\""
                    : $"--upload --board {Fqbn} --port {uploadPort} \"{inoPath}\"")
                : (uploadPort == null
                    ? $"compile --fqbn {Fqbn} \"{Path.GetDirectoryName(inoPath)}\""
                    : $"compile --upload --fqbn {Fqbn} --port {uploadPort} \"{Path.GetDirectoryName(inoPath)}\"");

            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                using var process = Process.Start(psi)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var exited = process.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMinutes(4))) != exited)
                {
                    try { process.Kill(true); } catch { }
                    return new Outcome(false, "The Arduino tools took more than 4 minutes and were stopped.");
                }
                string output = (await stdout + "\n" + await stderr).Trim();
                return new Outcome(process.ExitCode == 0, output);
            }
            catch (Exception ex)
            {
                return new Outcome(false, $"Couldn't run the Arduino tools: {ex.Message}");
            }
        }
    }
}
