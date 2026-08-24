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
[JsonSerializable(typeof(DiscordModels.WebhookPayload))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}

public static class DiscordModels
{
    public class WebhookPayload
    {
        [JsonPropertyName("username")] public string Username { get; set; } = "MineBackup";
        [JsonPropertyName("embeds")] public List<Embed> Embeds { get; set; } = [];
    }

    public class Embed
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("color")] public int Color { get; set; }
        [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;
        [JsonPropertyName("fields")] public List<EmbedField> Fields { get; set; } = [];
    }

    public class EmbedField
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
        [JsonPropertyName("inline")] public bool Inline { get; set; }
    }
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
