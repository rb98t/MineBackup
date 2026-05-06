using System.Text.Json.Serialization;
using MineBackup.Models;

namespace MineBackup;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(MySqlConfig))]
[JsonSerializable(typeof(GoogleDriveModels.File))]
[JsonSerializable(typeof(GoogleDriveModels.FileList))]
[JsonSerializable(typeof(GoogleDriveModels.TokenResponse))]
[JsonSerializable(typeof(GoogleDriveModels.UploadMetadata))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}

public static class GoogleDriveModels
{
    public class UploadMetadata
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("parents")] public string[] Parents { get; set; } = Array.Empty<string>();
    }
    public class File
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("modifiedTime")] public string? ModifiedTime { get; set; }
    }

    public class FileList
    {
        [JsonPropertyName("files")] public List<File> Files { get; set; } = new();
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }

    public class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("token_type")] public string TokenType { get; set; } = string.Empty;
    }
}
