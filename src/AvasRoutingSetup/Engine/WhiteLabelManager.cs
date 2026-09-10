using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AvasRoutingSetup.Engine;

/// <summary>
/// Handles white-labeling the BlueRiver AV Manager UI and backend configuration to Advantech.
/// </summary>
public static class WhiteLabelManager
{
    private const string ServiceName = "bavm";

    /// <summary>
    /// Locates the BlueRiver AV Manager installation path in AppData or Program Files.
    /// </summary>
    public static bool TryGetBlueRiverAppPath(out string appPath)
    {
        // 1. Primary path: %APPDATA%\Semtech\BlueRiver AV Manager\app
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string defaultPath = Path.Combine(appData, @"Semtech\BlueRiver AV Manager\app");
        if (Directory.Exists(defaultPath))
        {
            appPath = defaultPath;
            return true;
        }

        // 2. ProgramData fallback
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string commonPath = Path.Combine(programData, @"Semtech\BlueRiver AV Manager\app");
        if (Directory.Exists(commonPath))
        {
            appPath = commonPath;
            return true;
        }

        appPath = defaultPath;
        return false;
    }

    /// <summary>
    /// Applies Advantech white-labeling assets and configuration to BlueRiver AV Manager.
    /// </summary>
    public static async Task<(bool Success, string Message)> ApplyWhiteLabelAsync(
        string whiteLabelSourceDir,
        Action<string>? log = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!TryGetBlueRiverAppPath(out string targetAppPath))
                {
                    log?.Invoke($"Warning: BlueRiver AV Manager app directory not found at {targetAppPath}. It will be created.");
                }

                log?.Invoke($"Target BlueRiver App path: {targetAppPath}");

                string imageDestDir = Path.Combine(targetAppPath, @"front\images");
                string configDestDir = Path.Combine(targetAppPath, @"src\config");

                Directory.CreateDirectory(imageDestDir);
                Directory.CreateDirectory(configDestDir);

                // 1. Deploy Logo
                string sourceLogo = Path.Combine(whiteLabelSourceDir, "logo.svg");
                if (File.Exists(sourceLogo))
                {
                    string destLogo = Path.Combine(imageDestDir, "logo.svg");
                    string bakLogo = Path.Combine(imageDestDir, "logo.svg.bak");

                    if (File.Exists(destLogo) && !File.Exists(bakLogo))
                    {
                        File.Copy(destLogo, bakLogo, overwrite: false);
                        log?.Invoke("Backed up original logo.svg to logo.svg.bak");
                    }

                    File.Copy(sourceLogo, destLogo, overwrite: true);
                    log?.Invoke("Deployed Advantech logo.svg successfully.");
                }
                else
                {
                    log?.Invoke($"Notice: Source logo.svg not found at {sourceLogo}, skipping logo replacement.");
                }

                // 2. Deploy Configuration / index.js
                string sourceIndex = Path.Combine(whiteLabelSourceDir, "index.js");
                if (File.Exists(sourceIndex))
                {
                    string destIndex = Path.Combine(configDestDir, "index.js");
                    string bakIndex = Path.Combine(configDestDir, "index.js.bak");

                    if (File.Exists(destIndex) && !File.Exists(bakIndex))
                    {
                        File.Copy(destIndex, bakIndex, overwrite: false);
                        log?.Invoke("Backed up original index.js to index.js.bak");
                    }

                    File.Copy(sourceIndex, destIndex, overwrite: true);
                    log?.Invoke("Deployed Advantech index.js branding configuration successfully.");
                }
                else
                {
                    log?.Invoke($"Notice: Source index.js not found at {sourceIndex}, skipping config replacement.");
                }

                // 3. Restart bavm Windows service if present and running
                TryRestartBavmService(log);

                return (true, "Advantech white-labeling applied successfully.");
            }
            catch (Exception ex)
            {
                log?.Invoke($"White-labeling error: {ex.Message}");
                return (false, $"Failed to apply white-labeling: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Restores original BlueRiver AV Manager files from backup (.bak).
    /// </summary>
    public static bool RestoreOriginal(Action<string>? log = null)
    {
        try
        {
            if (!TryGetBlueRiverAppPath(out string targetAppPath)) return false;

            string bakLogo = Path.Combine(targetAppPath, @"front\images\logo.svg.bak");
            string destLogo = Path.Combine(targetAppPath, @"front\images\logo.svg");
            if (File.Exists(bakLogo))
            {
                File.Copy(bakLogo, destLogo, overwrite: true);
                File.Delete(bakLogo);
                log?.Invoke("Restored original logo.svg.");
            }

            string bakIndex = Path.Combine(targetAppPath, @"src\config\index.js.bak");
            string destIndex = Path.Combine(targetAppPath, @"src\config\index.js");
            if (File.Exists(bakIndex))
            {
                File.Copy(bakIndex, destIndex, overwrite: true);
                File.Delete(bakIndex);
                log?.Invoke("Restored original index.js.");
            }

            TryRestartBavmService(log);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Error restoring original files: {ex.Message}");
            return false;
        }
    }

    private static void TryRestartBavmService(Action<string>? log)
    {
        try
        {
            // Use sc.exe query bavm
            var queryPsi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {ServiceName}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var queryProc = Process.Start(queryPsi);
            if (queryProc == null) return;
            string output = queryProc.StandardOutput.ReadToEnd();
            queryProc.WaitForExit(3000);

            if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"Restarting {ServiceName} service to load new branding assets...");
                RunScCommand($"stop {ServiceName}");
                Thread.Sleep(2000);
                RunScCommand($"start {ServiceName}");
                log?.Invoke($"{ServiceName} service restarted successfully.");
            }
            else if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"Starting {ServiceName} service...");
                RunScCommand($"start {ServiceName}");
                log?.Invoke($"{ServiceName} service started successfully.");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Notice: Could not restart service {ServiceName}: {ex.Message}");
        }
    }

    private static void RunScCommand(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
    }
}
