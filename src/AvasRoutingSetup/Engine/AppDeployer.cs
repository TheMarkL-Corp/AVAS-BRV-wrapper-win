using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace AvasRoutingSetup.Engine;

/// <summary>
/// Extracts and copies application payload files to the target installation directory.
/// </summary>
public static class AppDeployer
{
    /// <summary>
    /// Deploys payload files (either from a zip archive or a directory) to the target directory.
    /// </summary>
    public static async Task<(bool Success, string Message)> DeployAsync(
        string sourcePayloadPath,
        string targetDirectory,
        IProgress<(int Current, int Total, string Status)>? progress = null,
        Action<string>? log = null)
    {
        try
        {
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
                log?.Invoke($"Created installation directory: {targetDirectory}");
            }

            // Case 1: Source is a ZIP archive
            if (File.Exists(sourcePayloadPath) && sourcePayloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return await DeployFromZipAsync(sourcePayloadPath, targetDirectory, progress, log);
            }

            // Case 2: Source is a Directory
            if (Directory.Exists(sourcePayloadPath))
            {
                return await DeployFromDirectoryAsync(sourcePayloadPath, targetDirectory, progress, log);
            }

            return (false, $"Source payload not found: {sourcePayloadPath}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Deployment error: {ex.Message}");
            return (false, $"Deployment failed: {ex.Message}");
        }
    }

    private static async Task<(bool Success, string Message)> DeployFromZipAsync(
        string zipPath,
        string targetDirectory,
        IProgress<(int Current, int Total, string Status)>? progress,
        Action<string>? log)
    {
        return await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zipPath);
            int totalEntries = archive.Entries.Count;
            int count = 0;

            // Check if root of zip has a top-level wrapper directory (e.g. AVAS-Routing-SW-v1.1.0-win-x64/)
            string? rootPrefix = null;
            var firstEntry = archive.Entries.Count > 0 ? archive.Entries[0].FullName : null;
            if (firstEntry != null && firstEntry.Contains('/'))
            {
                string candidate = firstEntry.Substring(0, firstEntry.IndexOf('/') + 1);
                bool allMatch = true;
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        allMatch = false;
                        break;
                    }
                }
                if (allMatch) rootPrefix = candidate;
            }

            foreach (var entry in archive.Entries)
            {
                count++;
                string relativePath = rootPrefix != null && entry.FullName.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                    ? entry.FullName.Substring(rootPrefix.Length)
                    : entry.FullName;

                if (string.IsNullOrWhiteSpace(relativePath)) continue;

                string destinationPath = Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                string? dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                entry.ExtractToFile(destinationPath, overwrite: true);
                log?.Invoke($"Extracted: {relativePath}");
                progress?.Report((count, totalEntries, $"Extracting: {Path.GetFileName(destinationPath)}"));
            }

            return (true, "Application files deployed successfully from package archive.");
        });
    }

    private static async Task<(bool Success, string Message)> DeployFromDirectoryAsync(
        string sourceDir,
        string targetDirectory,
        IProgress<(int Current, int Total, string Status)>? progress,
        Action<string>? log)
    {
        return await Task.Run(() =>
        {
            var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
            int total = files.Length;
            int count = 0;

            foreach (var file in files)
            {
                count++;
                string relativePath = Path.GetRelativePath(sourceDir, file);
                string destFile = Path.Combine(targetDirectory, relativePath);

                string? dir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Copy(file, destFile, overwrite: true);
                log?.Invoke($"Copied: {relativePath}");
                progress?.Report((count, total, $"Copying: {Path.GetFileName(destFile)}"));
            }

            return (true, "Application files deployed successfully from directory.");
        });
    }
}
