using System.Data;
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
        var tempFolder = Path.Combine(destDir, $"DB_{dbName}_{timestamp}");

        try
        {
            Directory.CreateDirectory(tempFolder);
            var schemaFile = Path.Combine(tempFolder, $"{dbName}_schema.sql");
            var dataFile = Path.Combine(tempFolder, $"{dbName}_data.sql");

            var connString = $"Server={config.Host};Port={config.Port};User ID={config.User};Password={config.Password};Database={dbName};SslMode=None;";

            using var conn = new MySqlConnection(connString);
            await conn.OpenAsync();

            var tables = new List<string>();
            using (var cmd = new MySqlCommand("SHOW TABLES", conn))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            var totalSteps = tables.Count * 2;
            var currentStep = 0;

            // 1. Schema
            using (var sw = new StreamWriter(schemaFile, false, new UTF8Encoding(false)))
            {
                await sw.WriteLineAsync("SET FOREIGN_KEY_CHECKS=0;");
                foreach (var table in tables)
                {
                    using (var cmd = new MySqlCommand($"SHOW CREATE TABLE `{table}`", conn))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            var createStmt = reader.GetString(1);
                            await sw.WriteLineAsync($"DROP TABLE IF EXISTS `{table}`;");
                            await sw.WriteLineAsync($"{createStmt};");
                            await sw.WriteLineAsync();
                        }
                    }
                    currentStep++;
                    progress.Report(currentStep * 100 / totalSteps);
                }
                await sw.WriteLineAsync("SET FOREIGN_KEY_CHECKS=1;");
            }

            // 2. Data
            using (var sw = new StreamWriter(dataFile, false, new UTF8Encoding(false)))
            {
                await sw.WriteLineAsync("SET FOREIGN_KEY_CHECKS=0;");
                foreach (var table in tables)
                {
                    using (var cmd = new MySqlCommand($"SELECT * FROM `{table}`", conn))
                    using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess))
                    {
                        var hasData = false;
                        var columns = new List<string>();
                        for (int i = 0; i < reader.FieldCount; i++) columns.Add($"`{reader.GetName(i)}`");
                        var colsStr = string.Join(", ", columns);

                        while (await reader.ReadAsync())
                        {
                            if (!hasData)
                            {
                                await sw.WriteLineAsync($"INSERT INTO `{table}` ({colsStr}) VALUES");
                                hasData = true;
                            }
                            else
                            {
                                await sw.WriteLineAsync(",");
                            }

                            var vals = new List<string>();
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                if (await reader.IsDBNullAsync(i)) vals.Add("NULL");
                                else
                                {
                                    var val = reader.GetValue(i);
                                    vals.Add(FormatValue(val));
                                }
                            }
                            await sw.WriteAsync($"({string.Join(", ", vals)})");
                        }

                        if (hasData) await sw.WriteLineAsync(";");
                    }
                    currentStep++;
                    progress.Report(currentStep * 100 / totalSteps);
                }
                await sw.WriteLineAsync("SET FOREIGN_KEY_CHECKS=1;");
            }

            // ZIP
            using (var zipFile = File.Create(zipPath))
            using (var archive = new ZipArchive(zipFile, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(schemaFile, $"{dbName}_schema.sql");
                archive.CreateEntryFromFile(dataFile, $"{dbName}_data.sql");
            }

            Directory.Delete(tempFolder, true);
            return zipPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{DB}] Error during database backup", dbName);
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            return null;
        }
    }

    private string FormatValue(object val)
    {
        if (val is string s) return $"'{MySqlHelper.EscapeString(s)}'";
        if (val is DateTime dt) return $"'{dt:yyyy-MM-dd HH:mm:ss}'";
        if (val is byte[] b) return $"X'{Convert.ToHexString(b)}'";
        if (val is bool bl) return bl ? "1" : "0";
        return val.ToString() ?? "NULL";
    }
}