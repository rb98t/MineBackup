using System.Text.Json;
using Microsoft.Extensions.Logging;
using MineBackup.Models;

namespace MineBackup.Services;

public class ConfigService
{
    private readonly ILogger<ConfigService> _logger;
    private AppConfig? _config;

    public ConfigService(ILogger<ConfigService> logger)
    {
        _logger = logger;
    }

    public AppConfig? LoadConfig()
    {
        if (_config != null) return _config;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var workingDir = Directory.GetCurrentDirectory();
        
        var pathsToTry = new[]
        {
            Path.Combine(baseDir, "config.json"),
            Path.Combine(workingDir, "config.json")
        };

        string? foundPath = null;
        foreach (var path in pathsToTry)
        {
            if (File.Exists(path))
            {
                foundPath = path;
                break;
            }
        }

        try
        {
            if (foundPath == null)
            {
                _logger.LogError("Config file not found in searched paths.");
                return null;
            }

            var json = File.ReadAllText(foundPath);
            _config = JsonSerializer.Deserialize(json, SourceGenerationContext.Default.AppConfig);
            return _config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load config.json");
            return null;
        }
    }
}
