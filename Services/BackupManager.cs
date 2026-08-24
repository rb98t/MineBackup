using Microsoft.Extensions.Logging;
using MineBackup.Models;
using Spectre.Console;

namespace MineBackup.Services;

public class BackupManager(
    ILogger<BackupManager> logger,
    ConfigService configService,
    ZipService zipService,
    DatabaseService databaseService,
    GoogleDriveService driveService)
{
    /// <returns>True when every configured backup was produced and uploaded.</returns>
    public async Task<bool> RunAsync(CliOptions? options = null)
    {
        options ??= new CliOptions();

        AnsiConsole.Write(new FigletText("MineBackup").Centered().Color(Color.Aqua));
        AnsiConsole.Write(new Rule("[bold white]Minecraft Biztonsági Mentés[/]").RuleStyle("white"));
        logger.LogInformation("=== Biztonsági mentés elindítva ===");

        var config = configService.LoadConfig();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red][[HIBA]][/] Nem sikerült betölteni a konfigurációt.");
            return false;
        }

        var driveFolderId = options.DriveFolderId ?? config.DriveFolderId;
        if (driveFolderId == "YOUR_GOOGLE_DRIVE_FOLDER_ID" || string.IsNullOrEmpty(driveFolderId))
        {
            AnsiConsole.MarkupLine("[red][[HIBA]][/] Kérlek állítsd be a 'drive_folder_id'-t a config.json fájlban!");
            return false;
        }

        // A targeted run replaces both lists wholesale, so `--source X` cannot accidentally drag the
        // configured databases (or the other way round) along with it.
        var sources = options.IsTargetedRun ? options.Sources : config.BackupSources;
        var databases = options.IsTargetedRun ? options.Databases : config.MySql.Databases;
        var filesEnabled = options.IsTargetedRun ? sources.Count > 0 : config.FilesBackupEnabled;
        var databasesEnabled = options.IsTargetedRun ? databases.Count > 0 : config.MySql.Enabled;

        if (options.IsTargetedRun)
        {
            logger.LogInformation("Célzott futás: {Sources} mappa, {Databases} adatbázis, előtag='{Prefix}'",
                sources.Count, databases.Count, options.Prefix ?? "-");
        }

        var tempPath = Path.GetFullPath(config.TempZipFolder);
        Directory.CreateDirectory(tempPath);

        // 1. Step: Authentication
        AnsiConsole.MarkupLine("[yellow][[1/3]][/] Google Drive bejelentkezés...");
        if (!await driveService.AuthenticateAsync())
        {
            AnsiConsole.MarkupLine("[red][[HIBA]][/] Nem sikerült bejelentkezni a Google Drive-ba");
            return false;
        }

        // Resume: Upload leftover files in tempPath.
        // Skipped for targeted runs: a deploy calling in here must not end up waiting out the upload of a
        // half-finished multi-gigabyte world backup left behind by the nightly job.
        var leftoverFiles = options.IsTargetedRun ? Array.Empty<string>() : Directory.GetFiles(tempPath);
        if (leftoverFiles.Length > 0)
        {
            AnsiConsole.MarkupLine("[yellow][[INFO]][/] Félbemaradt mentések feltöltése...");
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new TransferSpeedColumn(), new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var uploadTasks = new List<Task<bool>>();
                    foreach (var file in leftoverFiles)
                    {
                        var fileName = Path.GetFileName(file);
                        var fileSize = new FileInfo(file).Length;
                        var task = ctx.AddTask($"Feltöltés: {fileName}", maxValue: fileSize);

                        uploadTasks.Add(Task.Run(async () =>
                        {
                            var success = await driveService.UploadFileAsync(file, driveFolderId, new Progress<long>(p => task.Value = p));
                            if (success) File.Delete(file);
                            return success;
                        }));
                    }
                    await Task.WhenAll(uploadTasks);
                });
        }

        // 2. Step: Backup & Upload
        AnsiConsole.MarkupLine("[yellow][[2/3]][/] Biztonsági mentések készítése és feltöltése...");

        var failures = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                var tasks = new List<Task>();

                // Server tasks (Zip + Upload)
                if (filesEnabled)
                {
                    foreach (var source in sources)
                    {
                        var name = new DirectoryInfo(source).Name;

                        // 1. Szerver mentése (kivéve a logs mappa)
                        tasks.Add(Task.Run(async () =>
                        {
                            var task = ctx.AddTask($"Szerver tömörítés: {name}");

                            var serverExcludes = new List<string>(config.ExcludePatterns);
                            if (!serverExcludes.Contains("logs", StringComparer.OrdinalIgnoreCase))
                            {
                                serverExcludes.Add("logs");
                            }

                            var zipPath = await zipService.ZipDirectoryAsync(source, tempPath, serverExcludes, new Progress<int>(p =>
                            {
                                // Zip progress 0-50%
                                task.Value = p / 2.0;
                            }), ApplyPrefix(options.Prefix, name));

                            if (zipPath != null && File.Exists(zipPath))
                            {
                                var fileSize = new FileInfo(zipPath).Length;
                                task.Description = $"Feltöltés: {name}";
                                var success = await driveService.UploadFileAsync(zipPath, driveFolderId, new Progress<long>(p =>
                                {
                                    // Upload progress 50-100%
                                    if (fileSize > 0)
                                    {
                                        task.Value = 50 + (p * 50.0 / fileSize);
                                    }
                                }));
                                if (success) File.Delete(zipPath);
                                else Interlocked.Increment(ref failures);
                            }
                            else
                            {
                                Interlocked.Increment(ref failures);
                            }
                            task.Value = 100;
                            task.Description = $"Kész: {name}";
                        }));

                        // 2. Logok külön mentése
                        var logsPath = Path.Combine(source, "logs");
                        if (Directory.Exists(logsPath))
                        {
                            var logsTaskName = $"{name}_logs";
                            tasks.Add(Task.Run(async () =>
                            {
                                var task = ctx.AddTask($"Logok tömörítése: {logsTaskName}");
                                var zipPath = await zipService.ZipDirectoryAsync(logsPath, tempPath, new List<string>(), new Progress<int>(p =>
                                {
                                    // Zip progress 0-50%
                                    task.Value = p / 2.0;
                                }), ApplyPrefix(options.Prefix, logsTaskName));

                                if (zipPath != null && File.Exists(zipPath))
                                {
                                    var fileSize = new FileInfo(zipPath).Length;
                                    task.Description = $"Feltöltés: {logsTaskName}";
                                    var success = await driveService.UploadFileAsync(zipPath, driveFolderId, new Progress<long>(p =>
                                    {
                                        // Upload progress 50-100%
                                        if (fileSize > 0)
                                        {
                                            task.Value = 50 + (p * 50.0 / fileSize);
                                        }
                                    }));
                                    if (success) File.Delete(zipPath);
                                    else Interlocked.Increment(ref failures);
                                }
                                else
                                {
                                    Interlocked.Increment(ref failures);
                                }
                                task.Value = 100;
                                task.Description = $"Kész: {logsTaskName}";
                            }));
                        }
                    }
                }

                // DB tasks (Dump + Upload)
                if (databasesEnabled)
                {
                    foreach (var db in databases)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            var task = ctx.AddTask($"Adatbázis tömörítés: {db}");
                            var dbConfig = new MySqlConfig
                            {
                                Host = config.MySql.Host,
                                Port = config.MySql.Port,
                                User = config.MySql.User,
                                Password = config.MySql.Password,
                                DatabaseName = db
                            };

                            var dumpPath = await databaseService.DumpDatabaseAsync(dbConfig, tempPath, new Progress<int>(p =>
                            {
                                // Dump progress 0-50%
                                task.Value = p / 2.0;
                            }), options.Prefix);

                            if (dumpPath != null && File.Exists(dumpPath))
                            {
                                var fileSize = new FileInfo(dumpPath).Length;
                                task.Description = $"Feltöltés: {db}";
                                var success = await driveService.UploadFileAsync(dumpPath, driveFolderId, new Progress<long>(p =>
                                {
                                    // Upload progress 50-100%
                                    if (fileSize > 0)
                                    {
                                        task.Value = 50 + (p * 50.0 / fileSize);
                                    }
                                }));
                                if (success) File.Delete(dumpPath);
                                else Interlocked.Increment(ref failures);
                            }
                            else
                            {
                                Interlocked.Increment(ref failures);
                            }
                            task.Value = 100;
                            task.Description = $"Kész: {db}";
                        }));
                    }
                }

                await Task.WhenAll(tasks);
            });

        // 3. Step: Maintenance
        if (options.NoPurge)
        {
            AnsiConsole.MarkupLine("[yellow][[3/3]][/] Karbantartás kihagyva (--no-purge).");
            logger.LogInformation("Retenciós takarítás kihagyva (--no-purge).");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow][[3/3]][/] Karbantartás (régi mentések törlése)...");
            await driveService.PurgeOldBackupsAsync(driveFolderId, config.RetentionDays);
        }

        if (failures > 0)
        {
            AnsiConsole.Write(new Rule($"[bold red]{failures} mentés nem sikerült![/]").RuleStyle("red"));
            logger.LogError("=== A biztonsági mentés {Count} hibával fejeződött be ===", failures);
            return false;
        }

        AnsiConsole.Write(new Rule("[bold green]A biztonsági mentés sikeresen befejeződött![/]").RuleStyle("green"));
        logger.LogInformation("=== Biztonsági mentés sikeresen befejeződött ===");
        return true;
    }

    private static string ApplyPrefix(string? prefix, string name) =>
        string.IsNullOrEmpty(prefix) ? name : $"{prefix}_{name}";
}
