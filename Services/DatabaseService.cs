using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using MineBackup.Models;

namespace MineBackup.Services;

public class DatabaseService
{
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(ILogger<DatabaseService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> DumpDatabaseAsync(MySqlConfig config, string destDir, IProgress<int> progress)
    {
        var dbName = config.DatabaseName ?? "UnknownDB";
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var zipFilename = $"DB_{dbName}_{timestamp}.zip";
        var zipPath = Path.Combine(destDir, zipFilename);
        var tempSqlFile = Path.Combine(destDir, $"{dbName}_full_backup.sql");

        try
        {
            var connString = $"Server={config.Host};Port={config.Port};User ID={config.User};Password={config.Password};Database={dbName};SslMode=None;AllowUserVariables=True;";

            using (var conn = new MySqlConnection(connString))
            using (var cmd = new MySqlCommand())
            using (var mb = new MySqlBackup(cmd))
            {
                cmd.Connection = conn;
                await conn.OpenAsync();

                _logger.LogInformation("[{DB}] Starting industry-standard SQL dump using MySqlBackup.NET...", dbName);
                
                // MySqlBackup.NET handles BLOBs, UUIDs, and culture-invariant formatting correctly.
                mb.ExportInfo.AddCreateDatabase = false;
                mb.ExportInfo.ExportTableStructure = true;
                mb.ExportInfo.ExportRows = true;
                
                // Run the export
                mb.ExportToFile(tempSqlFile);
                
                _logger.LogInformation("[{DB}] SQL dump completed. Compressing...", dbName);
                progress.Report(50);
            }

            // ZIP the single SQL file
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (var zipFile = File.Create(zipPath))
            using (var archive = new ZipArchive(zipFile, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(tempSqlFile, $"{dbName}_full_backup.sql");
            }

            // Cleanup temp SQL file
            if (File.Exists(tempSqlFile)) File.Delete(tempSqlFile);

            progress.Report(100);
            _logger.LogInformation("[{DB}] Backup ZIP created: {Path}", dbName, zipPath);
            return zipPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{DB}] Error during standardized database backup", dbName);
            if (File.Exists(tempSqlFile)) File.Delete(tempSqlFile);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            return null;
        }
    }
}