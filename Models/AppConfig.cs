using System.Text.Json.Serialization;

namespace MineBackup.Models;

public class AppConfig
{
    [JsonPropertyName("backup_sources")]
    public List<string> BackupSources { get; set; } = new();

    [JsonPropertyName("exclude_patterns")]
    public List<string> ExcludePatterns { get; set; } = new();

    [JsonPropertyName("mysql")]
    public MySqlConfig MySql { get; set; } = new();

    [JsonPropertyName("drive_folder_id")]
    public string DriveFolderId { get; set; } = string.Empty;

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 10;

    [JsonPropertyName("temp_zip_folder")]
    public string TempZipFolder { get; set; } = "temp";
}

public class MySqlConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = "localhost";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 3306;

    [JsonPropertyName("user")]
    public string User { get; set; } = "root";

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("databases")]
    public List<string> Databases { get; set; } = new();

    // Helper for DB individual processing
    [JsonIgnore]
    public string? DatabaseName { get; set; }
}
