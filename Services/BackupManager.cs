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
    public async Task RunAsync()
    {
        AnsiConsole.Write(new FigletText("MineBackup").Centered().Color(Color.Aqua));
        AnsiConsole.Write(new Rule("[bold white]Minecraft Biztonsági Mentés[/]").RuleStyle("white"));
        logger.LogInformation("=== Biztonsági mentés elindítva ===");

        var config = configService.LoadConfig();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red][[HIBA]][/] Nem sikerült betölteni a konfigurációt.");
            return;
        }

        if (config.DriveFolderId == "YOUR_GOOGLE_DRIVE_FOLDER_ID" || string.IsNullOrEmpty(config.DriveFolderId))
        {
            AnsiConsole.MarkupLine("[red][[HIBA]][/] Kérlek állítsd be a 'drive_folder_id'-t a config.json fájlban!");
            return;
        }

        var tempPath = Path.GetFullPath(config.TempZipFolder);
        Directory.CreateDirectory(tempPath);

        // 1. Step: Authentication
        AnsiConsole.MarkupLine("[yellow][[1/3]][/] Google Drive bejelentkezés...");
        if (!await driveService.AuthenticateAsync())
        {
            AnsiConsole.MarkupLine("[red][[HIBA]][/] Nem sikerült bejelentkezni a Google Drive-ba");
            return;
        }

        // Resume: Upload leftover files in tempPath
        var leftoverFiles = Directory.GetFiles(tempPath);
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
                            var success = await driveService.UploadFileAsync(file, config.DriveFolderId, new Progress<long>(p => task.Value = p));
                            if (success) File.Delete(file);
                            return success;
                        }));
                    }
                    await Task.WhenAll(uploadTasks);
                });
        }

        // 2. Step: Backup & Upload
        AnsiConsole.MarkupLine("[yellow][[2/3]][/] Biztonsági mentések készítése és feltöltése...");
        
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
                if (config.FilesBackupEnabled)
                {
                    foreach (var source in config.BackupSources)
                    {
                        var name = new DirectoryInfo(source).Name;
                        tasks.Add(Task.Run(async () =>
                        {
                            var task = ctx.AddTask($"Szerver tömörítés: {name}");
                            var zipPath = await zipService.ZipDirectoryAsync(source, tempPath, config.ExcludePatterns, new Progress<int>(p => 
                            {
                                // Zip progress 0-50%
                                task.Value = p / 2.0;
                            }));

                            if (zipPath != null && File.Exists(zipPath))
                            {
                                var fileSize = new FileInfo(zipPath).Length;
                                task.Description = $"Feltöltés: {name}";
                                var success = await driveService.UploadFileAsync(zipPath, config.DriveFolderId, new Progress<long>(p => 
                                {
                                    // Upload progress 50-100%
                                    if (fileSize > 0)
                                    {
                                        task.Value = 50 + (p * 50.0 / fileSize);
                                    }
                                }));
                                if (success) File.Delete(zipPath);
                            }
                            task.Value = 100;
                            task.Description = $"Kész: {name}";
                        }));
                    }
                }

                // DB tasks (Dump + Upload)
                if (config.MySql.Enabled)
                {
                    foreach (var db in config.MySql.Databases)
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
                            }));

                            if (dumpPath != null && File.Exists(dumpPath))
                            {
                                var fileSize = new FileInfo(dumpPath).Length;
                                task.Description = $"Feltöltés: {db}";
                                var success = await driveService.UploadFileAsync(dumpPath, config.DriveFolderId, new Progress<long>(p => 
                                {
                                    // Upload progress 50-100%
                                    if (fileSize > 0)
                                    {
                                        task.Value = 50 + (p * 50.0 / fileSize);
                                    }
                                }));
                                if (success) File.Delete(dumpPath);
                            }
                            task.Value = 100;
                            task.Description = $"Kész: {db}";
                        }));
                    }
                }

                await Task.WhenAll(tasks);
            });

        // 3. Step: Maintenance
        AnsiConsole.MarkupLine("[yellow][[3/3]][/] Karbantartás (régi mentések törlése)...");
        await driveService.PurgeOldBackupsAsync(config.DriveFolderId, config.RetentionDays);

        AnsiConsole.Write(new Rule("[bold green]A biztonsági mentés sikeresen befejeződött![/]").RuleStyle("green"));
        logger.LogInformation("=== Biztonsági mentés sikeresen befejeződött ===");
    }
}
