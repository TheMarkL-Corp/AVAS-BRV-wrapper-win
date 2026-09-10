using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AvasRoutingSetup.Engine;

/// <summary>
/// Implements the 4-step Advantech White-Labeling process for BlueRiver AV Manager:
/// Step 1: Detect and stop 'bavm' Windows service (if missing, notify user and skip without blocking install).
/// Step 2: Replace logo.svg in %APPDATA%\Semtech\BlueRiver AV Manager\app\front\images\logo.svg.
/// Step 3: Replace branding values (APP_TITLE, APP_HEADER, THEME_PRIMARY_COLOR) in \src\config\index.js.
/// Step 4: Restart 'bavm' Windows service.
/// </summary>
public static class WhiteLabelManager
{
    private const string ServiceName = "bavm";

    /// <summary>
    /// Checks if the 'bavm' Windows service is installed on this machine.
    /// </summary>
    public static bool IsBavmServiceInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {ServiceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            if (process.ExitCode != 0 || output.Contains("1060", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the target BlueRiver AV Manager app directory: %APPDATA%\Semtech\BlueRiver AV Manager\app
    /// </summary>
    public static string GetBlueRiverAppPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, @"Semtech\BlueRiver AV Manager\app");
    }

    /// <summary>
    /// Executes the 4-step Advantech White-Labeling process.
    /// </summary>
    public static async Task<(bool Success, string Message)> ApplyWhiteLabelAsync(
        string whiteLabelSourceDir,
        Action<string>? log = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                string targetAppPath = GetBlueRiverAppPath();

                // =========================================================================
                // 1st Step: Stop bavm windows service
                // (Detect if it exists; if not, notify user that BlueRiver is not installed
                //  locally and proceed with installation without white-labeling)
                // =========================================================================
                log?.Invoke("Step 1/4: Checking if BlueRiver AV Manager ('bavm' service) is installed...");

                bool serviceInstalled = IsBavmServiceInstalled();
                bool dirExists = Directory.Exists(targetAppPath);

                if (!serviceInstalled && !dirExists)
                {
                    string notice = "Notice: BlueRiver AV Manager is not installed locally on this machine ('bavm' service not found). Continuing installation without white-labeling.";
                    log?.Invoke(notice);
                    return (false, notice);
                }

                if (serviceInstalled)
                {
                    log?.Invoke("• 'bavm' Windows service detected. Stopping service...");
                    bool stopped = StopService(ServiceName, timeoutSeconds: 15, log);
                    if (!stopped)
                    {
                        log?.Invoke("• Warning: Could not verify service stop, continuing with file replacement.");
                    }
                    else
                    {
                        log?.Invoke("• 'bavm' service stopped successfully.");
                    }
                }
                else
                {
                    log?.Invoke("• Service 'bavm' not registered, but BlueRiver app directory exists. Proceeding with file replacement.");
                }

                // =========================================================================
                // 2nd Step: Replace logo.svg in %APPDATA%\Semtech\BlueRiver AV Manager\app\front\images\logo.svg
                // Copy logo.svg from Advantech BR Patch / bundled whitelabel source
                // =========================================================================
                log?.Invoke("Step 2/4: Deploying Advantech logo.svg...");
                string imageDestDir = Path.Combine(targetAppPath, @"front\images");
                Directory.CreateDirectory(imageDestDir);

                string sourceLogo = Path.Combine(whiteLabelSourceDir, "logo.svg");
                if (!File.Exists(sourceLogo))
                {
                    // Check fallback in Downloads\Advantech BR Patch\image\logo.svg
                    string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Downloads\Advantech BR Patch\image\logo.svg");
                    if (File.Exists(userDownloads)) sourceLogo = userDownloads;
                }

                if (File.Exists(sourceLogo))
                {
                    string destLogo = Path.Combine(imageDestDir, "logo.svg");
                    string bakLogo = Path.Combine(imageDestDir, "logo.svg.bak");

                    if (File.Exists(destLogo) && !File.Exists(bakLogo))
                    {
                        File.Copy(destLogo, bakLogo, overwrite: false);
                        log?.Invoke("• Backed up original logo.svg to logo.svg.bak");
                    }

                    File.Copy(sourceLogo, destLogo, overwrite: true);
                    log?.Invoke($"• Replaced {destLogo} with Advantech logo.");
                }
                else
                {
                    log?.Invoke($"• Warning: Source logo.svg not found at {sourceLogo}. Skipping logo copy.");
                }

                // =========================================================================
                // 3rd Step: Replace the values in \src\config\index.js:
                // APP_TITLE: 'AV Manager',
                // APP_HEADER: 'AV Manager',
                // THEME_PRIMARY_COLOR: '#0055afff',
                // =========================================================================
                log?.Invoke("Step 3/4: Updating branding values in index.js...");
                string indexJsPath = Path.Combine(targetAppPath, @"src\config\index.js");

                if (File.Exists(indexJsPath))
                {
                    string bakIndex = Path.Combine(targetAppPath, @"src\config\index.js.bak");
                    if (!File.Exists(bakIndex))
                    {
                        File.Copy(indexJsPath, bakIndex, overwrite: false);
                        log?.Invoke("• Backed up original index.js to index.js.bak");
                    }

                    string indexContent = File.ReadAllText(indexJsPath);
                    string patchedContent = PatchIndexJsBranding(indexContent);

                    File.WriteAllText(indexJsPath, patchedContent);
                    log?.Invoke("• Successfully updated index.js (APP_TITLE: 'AV Manager', APP_HEADER: 'AV Manager', THEME_PRIMARY_COLOR: '#0055afff').");
                }
                else
                {
                    log?.Invoke($"• Notice: {indexJsPath} does not exist. Creating configuration from template...");
                    string configDir = Path.Combine(targetAppPath, @"src\config");
                    Directory.CreateDirectory(configDir);

                    string newConfig = GenerateDefaultBrandedIndexJs();
                    File.WriteAllText(indexJsPath, newConfig);
                    log?.Invoke("• Created branded index.js.");
                }

                // =========================================================================
                // 4th Step: Restart the bavm windows service
                // =========================================================================
                if (serviceInstalled)
                {
                    log?.Invoke("Step 4/4: Restarting 'bavm' Windows service...");
                    bool started = StartService(ServiceName, timeoutSeconds: 15, log);
                    if (started)
                    {
                        log?.Invoke("• 'bavm' service started successfully with Advantech branding.");
                    }
                    else
                    {
                        log?.Invoke("• Warning: Could not verify service start. You can start it manually via Services.");
                    }
                }
                else
                {
                    log?.Invoke("Step 4/4: 'bavm' service is not installed, no service restart needed.");
                }

                return (true, "Advantech White-Labeling completed successfully across all 4 steps.");
            }
            catch (Exception ex)
            {
                log?.Invoke($"• White-labeling error: {ex.Message}");
                return (false, $"White-labeling encountered an error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Replaces the 3 branding values in index.js content:
    /// APP_TITLE: 'AV Manager',
    /// APP_HEADER: 'AV Manager',
    /// THEME_PRIMARY_COLOR: '#0055afff',
    /// </summary>
    public static string PatchIndexJsBranding(string content)
    {
        // Replace APP_TITLE
        if (Regex.IsMatch(content, @"APP_TITLE:\s*['""][^'""]*['""]"))
        {
            content = Regex.Replace(content, @"APP_TITLE:\s*['""][^'""]*['""]", "APP_TITLE: 'AV Manager'");
        }

        // Replace APP_HEADER
        if (Regex.IsMatch(content, @"APP_HEADER:\s*['""][^'""]*['""]"))
        {
            content = Regex.Replace(content, @"APP_HEADER:\s*['""][^'""]*['""]", "APP_HEADER: 'AV Manager'");
        }

        // Replace THEME_PRIMARY_COLOR
        if (Regex.IsMatch(content, @"THEME_PRIMARY_COLOR:\s*['""][^'""]*['""]"))
        {
            content = Regex.Replace(content, @"THEME_PRIMARY_COLOR:\s*['""][^'""]*['""]", "THEME_PRIMARY_COLOR: '#0055afff'");
        }

        return content;
    }

    private static string GenerateDefaultBrandedIndexJs()
    {
        return @"import fs from 'fs';
import path from 'path';
import loadConfig from './loadConfig.js';

loadConfig();

const CertificateKeyPath = path.join(process.env.DATA_PATH || '', 'cert', 'server.key');
const CertificatePath = path.join(process.env.DATA_PATH || '', 'cert', 'server.cer');
const IsHttps = fs.existsSync(CertificateKeyPath) && fs.existsSync(CertificatePath);

const Branding = (() => {
  const { APP_TITLE, APP_HEADER, THEME_PRIMARY_COLOR, THEME_SECONDARY_COLOR } = {
    ...{
      APP_TITLE: 'AV Manager',
      APP_HEADER: 'AV Manager',
      THEME_PRIMARY_COLOR: '#0055afff',
      THEME_SECONDARY_COLOR: '#f2f2f2',
    },
    ...process.env,
  };
  return {
    Title: APP_TITLE,
    Header: APP_HEADER,
    PrimaryColor: THEME_PRIMARY_COLOR,
    SecondaryColor: THEME_SECONDARY_COLOR,
  };
})();

const config = { CertificateKeyPath, CertificatePath, IsHttps, Branding };
export default config;
";
    }

    private static bool StopService(string serviceName, int timeoutSeconds, Action<string>? log)
    {
        RunScCommand($"stop {serviceName}");

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            string status = QueryServiceStatus(serviceName);
            if (status.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            Thread.Sleep(500);
        }

        return false;
    }

    private static bool StartService(string serviceName, int timeoutSeconds, Action<string>? log)
    {
        RunScCommand($"start {serviceName}");

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            string status = QueryServiceStatus(serviceName);
            if (status.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            Thread.Sleep(500);
        }

        return false;
    }

    private static string QueryServiceStatus(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {serviceName}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return string.Empty;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void RunScCommand(string args)
    {
        try
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
        catch { }
    }
}
