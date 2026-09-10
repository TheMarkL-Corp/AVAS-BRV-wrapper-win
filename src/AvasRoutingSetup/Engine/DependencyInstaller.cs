using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace AvasRoutingSetup.Engine;

/// <summary>
/// Executes local, offline dependency redistributables in silent mode with exit code tracking.
/// </summary>
public static class DependencyInstaller
{
    /// <summary>
    /// Silently installs .NET 8 Windows Desktop Runtime from the offline package.
    /// </summary>
    public static async Task<(bool Success, string Message)> InstallDotNet8RuntimeAsync(string installerExePath, Action<string>? log = null)
    {
        if (!File.Exists(installerExePath))
        {
            return (false, $"Offline redistributable not found: {installerExePath}");
        }

        log?.Invoke($"Installing .NET 8 Desktop Runtime from: {Path.GetFileName(installerExePath)}...");

        // Standard Microsoft Burn installer arguments: /install /quiet /norestart
        return await RunProcessAsync(installerExePath, "/install /quiet /norestart", log);
    }

    /// <summary>
    /// Silently installs Microsoft Edge WebView2 Runtime from the offline standalone installer.
    /// </summary>
    public static async Task<(bool Success, string Message)> InstallWebView2RuntimeAsync(string installerExePath, Action<string>? log = null)
    {
        if (!File.Exists(installerExePath))
        {
            return (false, $"Offline redistributable not found: {installerExePath}");
        }

        log?.Invoke($"Installing Microsoft Edge WebView2 Runtime from: {Path.GetFileName(installerExePath)}...");

        // Microsoft Edge WebView2 Standalone Installer arguments: /silent /install
        return await RunProcessAsync(installerExePath, "/silent /install", log);
    }

    private static async Task<(bool Success, string Message)> RunProcessAsync(string exePath, string arguments, Action<string>? log)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas" // Elevate for admin installation
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            await process.WaitForExitAsync();

            int exitCode = process.ExitCode;
            log?.Invoke($"Process {Path.GetFileName(exePath)} exited with code {exitCode}.");

            // 0 = Success, 3010 = Success (Reboot required), 1641 = Success (Initiated reboot)
            if (exitCode == 0 || exitCode == 3010 || exitCode == 1641)
            {
                return (true, $"Installed successfully (Exit code {exitCode})");
            }

            return (false, $"Installation returned exit code {exitCode}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Installation error: {ex.Message}");
            return (false, $"Execution failed: {ex.Message}");
        }
    }
}
