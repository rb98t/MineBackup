using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace MineBackup.Services;

public class ZipService
{
    private readonly ILogger<ZipService> _logger;

    public ZipService(ILogger<ZipService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> ZipDirectoryAsync(string sourceDir, string destDir, List<string> excludePatterns, IProgress<int> progress, string? customPrefix = null)
    {
        var sourcePath = Path.GetFullPath(sourceDir);
        var dirName = new DirectoryInfo(sourcePath).Name;
        var prefix = customPrefix ?? dirName;
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var zipFilename = $"{prefix}_{timestamp}.zip";
        var zipPath = Path.Combine(destDir, zipFilename);

        _logger.LogInformation("[{Name}] Starting compression: {Path}", prefix, zipPath);

        try
        {
            Directory.CreateDirectory(destDir);

            var files = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                .Where(f => !ShouldExclude(f, sourcePath, excludePatterns))
                .ToList();

            var totalFiles = files.Count;
            var processedFiles = 0;

            using (var zipFile = File.Create(zipPath))
            using (var archive = new ZipArchive(zipFile, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(sourcePath, file);
                    // Standardize ZIP paths to use forward slashes and include the top-level directory
                    var entryName = Path.Combine(prefix, relativePath).Replace(Path.DirectorySeparatorChar, '/');

                    try
                    {
                        // Use FileShare.ReadWrite to allow zipping files even if the server is currently using them
                        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                            using (var entryStream = entry.Open())
                            {
                                await stream.CopyToAsync(entryStream);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{Name}] Skipping file (access denied): {Path}", prefix, file);
                    }
                    finally
                    {
                        processedFiles++;
                        progress.Report(processedFiles * 100 / Math.Max(1, totalFiles));
                    }
                }
            }

            return zipPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Name}] Error during compression", prefix);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            return null;
        }
    }

    private bool ShouldExclude(string filePath, string sourcePath, List<string> excludePatterns)
    {
        var relativePath = Path.GetRelativePath(sourcePath, filePath);
        var parts = relativePath.Split(Path.DirectorySeparatorChar);
        return parts.Any(part => excludePatterns.Contains(part, StringComparer.OrdinalIgnoreCase));
    }
}
