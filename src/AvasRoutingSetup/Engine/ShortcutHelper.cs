using System;
using System.IO;

namespace AvasRoutingSetup.Engine;

/// <summary>
/// Creates Windows Shell shortcuts (.lnk) on Desktop and Start Menu using COM WScript.Shell.
/// </summary>
public static class ShortcutHelper
{
    private const string AppShortcutName = "AVAS Routing Software.lnk";
    private const string AppDescription = "AVAS Routing Software — Advantech Dual Link SDVoE Manager";

    /// <summary>
    /// Creates a shortcut on the current user's Desktop.
    /// </summary>
    public static bool CreateDesktopShortcut(string targetExePath, string? iconPath = null, Action<string>? log = null)
    {
        try
        {
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string shortcutPath = Path.Combine(desktopDir, AppShortcutName);

            CreateShortcutInternal(shortcutPath, targetExePath, iconPath);
            log?.Invoke($"Desktop shortcut created: {shortcutPath}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed to create desktop shortcut: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates a shortcut in the Start Menu Programs folder.
    /// </summary>
    public static bool CreateStartMenuShortcut(string targetExePath, string? iconPath = null, Action<string>? log = null)
    {
        try
        {
            string startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string advantechMenuDir = Path.Combine(startMenuDir, "Advantech");
            if (!Directory.Exists(advantechMenuDir))
            {
                Directory.CreateDirectory(advantechMenuDir);
            }

            string shortcutPath = Path.Combine(advantechMenuDir, AppShortcutName);
            CreateShortcutInternal(shortcutPath, targetExePath, iconPath);
            log?.Invoke($"Start Menu shortcut created: {shortcutPath}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed to create Start Menu shortcut: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes shortcuts from Desktop and Start Menu.
    /// </summary>
    public static void RemoveShortcuts(Action<string>? log = null)
    {
        try
        {
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string desktopShortcut = Path.Combine(desktopDir, AppShortcutName);
            if (File.Exists(desktopShortcut))
            {
                File.Delete(desktopShortcut);
                log?.Invoke($"Removed desktop shortcut: {desktopShortcut}");
            }

            string startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string advantechMenuShortcut = Path.Combine(startMenuDir, "Advantech", AppShortcutName);
            if (File.Exists(advantechMenuShortcut))
            {
                File.Delete(advantechMenuShortcut);
                log?.Invoke($"Removed Start Menu shortcut: {advantechMenuShortcut}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Error removing shortcuts: {ex.Message}");
        }
    }

    private static void CreateShortcutInternal(string shortcutPath, string targetExePath, string? iconPath)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            throw new InvalidOperationException("WScript.Shell COM object is not available on this system.");
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);

        shortcut.TargetPath = targetExePath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetExePath);
        shortcut.Description = AppDescription;

        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            shortcut.IconLocation = iconPath;
        }
        else
        {
            shortcut.IconLocation = $"{targetExePath},0";
        }

        shortcut.Save();
    }
}
