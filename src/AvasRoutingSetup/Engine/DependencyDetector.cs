using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AvasRoutingSetup.Engine;

/// <summary>
/// Probes system registry, filesystem, and CLI tools to detect if required runtimes are installed.
/// </summary>
public static class DependencyDetector
{
    private const string WebView2ClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <summary>
    /// Checks if Microsoft.WindowsDesktop.App 8.0 or newer is installed on the machine.
    /// </summary>
    public static (bool IsInstalled, string VersionInfo) CheckDotNet8DesktopRuntime()
    {
        try
        {
            // 1. Filesystem check in shared dotnet location
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string desktopAppPath = Path.Combine(programFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (Directory.Exists(desktopAppPath))
            {
                var dirs = Directory.GetDirectories(desktopAppPath);
                foreach (var dir in dirs)
                {
                    string dirName = Path.GetFileName(dir);
                    if (dirName.StartsWith("8.", StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, $"Found .NET Desktop Runtime {dirName}");
                    }
                }
            }

            // 2. Registry check under Uninstall keys (64-bit and 32-bit views)
            if (CheckUninstallRegistryForDotNet8(RegistryView.Registry64, out string regVer) ||
                CheckUninstallRegistryForDotNet8(RegistryView.Registry32, out regVer))
            {
                return (true, regVer);
            }

            // 3. Command line probe: dotnet --list-runtimes
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-runtimes",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(3000);
                    if (output.Contains("Microsoft.WindowsDesktop.App 8."))
                    {
                        return (true, "Found via dotnet CLI");
                    }
                }
            }
            catch
            {
                // dotnet CLI might not be on system PATH
            }
        }
        catch (Exception ex)
        {
            return (false, $"Detection error: {ex.Message}");
        }

        return (false, "Not Installed");
    }

    private static bool CheckUninstallRegistryForDotNet8(RegistryView view, out string version)
    {
        version = string.Empty;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstallKey == null) return false;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var subKey = uninstallKey.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var displayName = subKey.GetValue("DisplayName") as string;
                if (!string.IsNullOrEmpty(displayName) &&
                    displayName.Contains("Windows Desktop Runtime", StringComparison.OrdinalIgnoreCase) &&
                    displayName.Contains("8.", StringComparison.OrdinalIgnoreCase))
                {
                    version = displayName;
                    return true;
                }
            }
        }
        catch
        {
            // Ignore registry permission errors
        }

        return false;
    }

    /// <summary>
    /// Checks if Microsoft Edge WebView2 Runtime is installed.
    /// </summary>
    public static (bool IsInstalled, string VersionInfo) CheckWebView2Runtime()
    {
        try
        {
            // Check HKLM 32-bit / WOW6432Node
            string[] registryPaths = new[]
            {
                $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2ClientGuid}",
                $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2ClientGuid}"
            };

            foreach (var path in registryPaths)
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key != null)
                {
                    var pv = key.GetValue("pv") as string;
                    if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0")
                    {
                        return (true, $"WebView2 version {pv}");
                    }
                }
            }

            // Check HKCU
            string hkcuPath = $@"Software\Microsoft\EdgeUpdate\Clients\{WebView2ClientGuid}";
            using (var key = Registry.CurrentUser.OpenSubKey(hkcuPath))
            {
                if (key != null)
                {
                    var pv = key.GetValue("pv") as string;
                    if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0")
                    {
                        return (true, $"WebView2 version {pv} (User)");
                    }
                }
            }

            // Filesystem fallback check
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string wv2Dir = Path.Combine(programFilesX86, @"Microsoft\EdgeWebView\Application");
            if (Directory.Exists(wv2Dir))
            {
                var dirs = Directory.GetDirectories(wv2Dir);
                foreach (var dir in dirs)
                {
                    string dirName = Path.GetFileName(dir);
                    if (Version.TryParse(dirName, out _))
                    {
                        return (true, $"WebView2 version {dirName} (Filesystem)");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return (false, $"Detection error: {ex.Message}");
        }

        return (false, "Not Installed");
    }
}
