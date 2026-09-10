using System;
using System.IO;
using Microsoft.Win32;

namespace AvasRoutingSetup.Engine;

/// <summary>
/// Registers the application in the Windows Uninstall registry key and generates an uninstaller script.
/// </summary>
public static class UninstallRegistryHelper
{
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AVASRoutingSW";

    /// <summary>
    /// Registers the installed application in Windows Add/Remove Programs.
    /// </summary>
    public static void Register(string installDir, string version, Action<string>? log = null)
    {
        try
        {
            string exePath = Path.Combine(installDir, "AvasRoutingApp.exe");
            string iconPath = Path.Combine(installDir, "logo.ico");
            string uninstallBat = Path.Combine(installDir, "uninstall.bat");

            // Create uninstall.bat
            string uninstallContent = $@"@echo off
echo Uninstalling AVAS Routing Software...
taskkill /f /im AvasRoutingApp.exe >nul 2>&1
timeout /t 1 /nobreak >nul
reg delete ""HKCU\{UninstallSubKey}"" /f >nul 2>&1
reg delete ""HKLM\{UninstallSubKey}"" /f >nul 2>&1
del ""%USERPROFILE%\Desktop\AVAS Routing Software.lnk"" >nul 2>&1
del ""%APPDATA%\Microsoft\Windows\Start Menu\Programs\Advantech\AVAS Routing Software.lnk"" >nul 2>&1
rmdir /s /q ""%APPDATA%\Microsoft\Windows\Start Menu\Programs\Advantech"" >nul 2>&1
echo Cleaning up application files...
cd ..
rmdir /s /q ""{installDir}""
echo AVAS Routing Software has been uninstalled successfully.
";
            File.WriteAllText(uninstallBat, uninstallContent);
            log?.Invoke($"Generated uninstaller script: {uninstallBat}");

            // Register in HKCU
            using var key = Registry.CurrentUser.CreateSubKey(UninstallSubKey);
            if (key != null)
            {
                key.SetValue("DisplayName", "AVAS Routing Software");
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "Advantech Co., Ltd.");
                key.SetValue("DisplayIcon", iconPath);
                key.SetValue("InstallLocation", installDir);
                key.SetValue("UninstallString", $"\"{uninstallBat}\"");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                log?.Invoke("Registered application in Windows Programs and Features.");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Warning: Could not register uninstall entry: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the application entry from Windows Add/Remove Programs.
    /// </summary>
    public static void Unregister(Action<string>? log = null)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallSubKey, throwOnMissingSubKey: false);
            log?.Invoke("Removed application from Windows Programs and Features.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Error unregistering application: {ex.Message}");
        }
    }
}
