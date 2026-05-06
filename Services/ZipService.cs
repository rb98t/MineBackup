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

    public async Task<string?> ZipDirectoryAsync(string sourceDir, string destDir, List<string> excludePatterns, IProgress<int> progress)
    {
        var sourcePath = Path.GetFullPath(sourceDir);
        var dirName = new DirectoryInfo(sourcePath).Name;
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var zipFilename = $"{dirName}_{timestamp}.zip";
        var zipPath = Path.Combine(destDir, zipFilename);

        _logger.LogInformation("[{Name}] Starting compression: {Path}", dirName, zipPath);

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
                    var entryName = Path.Combine(dirName, relativePath);

                    try
                    {
                        archive.CreateEntryFromFile(file, entryName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{Name}] Skipping file (access denied): {Path}", dirName, file);
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
            _logger.LogError(ex, "[{Name}] Error during compression", dirName);
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
