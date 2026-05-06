using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MineBackup.Services;
using NLog;
using NLog.Extensions.Logging;

namespace MineBackup;

class Program
{
    static async Task Main(string[] args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        try
        {
            var manager = serviceProvider.GetRequiredService<BackupManager>();
            await manager.RunAsync();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Alkalmazás váratlanul leállt.");
            Console.WriteLine($"Váratlan hiba: {ex.Message}");
        }
        finally
        {
            LogManager.Shutdown();
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            loggingBuilder.AddNLog();
        });

        // Services
        services.AddSingleton<HttpClient>();
        services.AddSingleton<ConfigService>();
        services.AddTransient<ZipService>();
        services.AddTransient<DatabaseService>();
        services.AddTransient<GoogleDriveService>();
        services.AddTransient<BackupManager>();

        // NLog configuration
        var config = new NLog.Config.LoggingConfiguration();
        var logfile = new NLog.Targets.FileTarget("logfile") 
        { 
            FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "backup_${shortdate}.log"),
            Layout = "${longdate} [${level:uppercase=true}] ${message} ${exception:format=tostring}" 
        };
        config.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, logfile);
        LogManager.Configuration = config;
    }
}
