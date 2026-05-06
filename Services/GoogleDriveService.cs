using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MineBackup.Services;

public class GoogleDriveService
{
    private readonly ILogger<GoogleDriveService> _logger;
    private readonly HttpClient _httpClient;
    private string? _accessToken;

    public GoogleDriveService(ILogger<GoogleDriveService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<bool> AuthenticateAsync()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var workingDir = Directory.GetCurrentDirectory();

        string? tokenPath = FindFile("token.json", baseDir, workingDir);
        string? credentialsPath = FindFile("credentials.json", baseDir, workingDir);

        if (tokenPath == null)
        {
            if (credentialsPath == null)
            {
                _logger.LogError("credentials.json not found. Initial login impossible.");
                return false;
            }
            return await PerformInitialLoginAsync(credentialsPath, Path.Combine(workingDir, "token.json"));
        }

        try
        {
            var tokenJson = await File.ReadAllTextAsync(tokenPath);
            var tokenData = JsonSerializer.Deserialize(tokenJson, SourceGenerationContext.Default.TokenResponse);
            
            if (tokenData == null) return false;

            if (!string.IsNullOrEmpty(tokenData.RefreshToken) && credentialsPath != null)
            {
                var credsJson = await File.ReadAllTextAsync(credentialsPath);
                using var doc = JsonDocument.Parse(credsJson);
                var root = doc.RootElement.GetProperty("installed");
                var clientId = root.GetProperty("client_id").GetString();
                var clientSecret = root.GetProperty("client_secret").GetString();

                var values = new Dictionary<string, string>
                {
                    { "client_id", clientId! },
                    { "client_secret", clientSecret! },
                    { "refresh_token", tokenData.RefreshToken },
                    { "grant_type", "refresh_token" }
                };

                var content = new FormUrlEncodedContent(values);
                var response = await _httpClient.PostAsync("https://oauth2.googleapis.com/token", content);
                
                if (response.IsSuccessStatusCode)
                {
                    var newTokenData = await response.Content.ReadFromJsonAsync(SourceGenerationContext.Default.TokenResponse);
                    if (newTokenData != null)
                    {
                        _accessToken = newTokenData.AccessToken;
                        // Preserve refresh token
                        if (string.IsNullOrEmpty(newTokenData.RefreshToken)) newTokenData.RefreshToken = tokenData.RefreshToken;
                        
                        var updatedJson = JsonSerializer.Serialize(newTokenData, SourceGenerationContext.Default.TokenResponse);
                        await File.WriteAllTextAsync(tokenPath, updatedJson);
                        return true;
                    }
                }
            }

            _accessToken = tokenData.AccessToken;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication failed");
            return false;
        }
    }

    private async Task<bool> PerformInitialLoginAsync(string credentialsPath, string saveTokenPath)
    {
        _logger.LogInformation("Starting initial login flow...");
        try
        {
            var credsJson = await File.ReadAllTextAsync(credentialsPath);
            using var doc = JsonDocument.Parse(credsJson);
            var root = doc.RootElement.GetProperty("installed");
            var clientId = root.GetProperty("client_id").GetString();
            var clientSecret = root.GetProperty("client_secret").GetString();
            var redirectUri = "http://localhost:5000/";

            var authUrl = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code&scope={Uri.EscapeDataString("https://www.googleapis.com/auth/drive.file")}&access_type=offline&prompt=consent";

            Console.WriteLine("Kérlek nyisd meg az alábbi linket a böngésződben a hitelesítéshez:");
            Console.WriteLine(authUrl);

            using var listener = new System.Net.HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            var context = await listener.GetContextAsync();
            var code = context.Request.QueryString["code"];

            using (var sw = new StreamWriter(context.Response.OutputStream))
            {
                await sw.WriteAsync("Sikeres hitelesítés! Most már bezárhatod ezt az ablakot.");
            }
            context.Response.Close();
            listener.Stop();

            if (string.IsNullOrEmpty(code)) return false;

            var values = new Dictionary<string, string>
            {
                { "client_id", clientId! },
                { "client_secret", clientSecret! },
                { "code", code },
                { "grant_type", "authorization_code" },
                { "redirect_uri", redirectUri }
            };

            var content = new FormUrlEncodedContent(values);
            var response = await _httpClient.PostAsync("https://oauth2.googleapis.com/token", content);
            
            if (response.IsSuccessStatusCode)
            {
                var tokenData = await response.Content.ReadFromJsonAsync(SourceGenerationContext.Default.TokenResponse);
                if (tokenData != null)
                {
                    _accessToken = tokenData.AccessToken;
                    var json = JsonSerializer.Serialize(tokenData, SourceGenerationContext.Default.TokenResponse);
                    await File.WriteAllTextAsync(saveTokenPath, json);
                    _logger.LogInformation("Token saved to {Path}", saveTokenPath);
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial login failed");
            return false;
        }
    }

    private string? FindFile(string fileName, string baseDir, string workingDir)
    {
        var paths = new[] { Path.Combine(baseDir, fileName), Path.Combine(workingDir, fileName) };
        return paths.FirstOrDefault(File.Exists);
    }

    public async Task<bool> UploadFileAsync(string filePath, string folderId, IProgress<long> progress)
    {
        if (string.IsNullOrEmpty(_accessToken)) return false;

        var fileInfo = new FileInfo(filePath);
        var fileName = fileInfo.Name;

        _logger.LogInformation("Starting upload: {Name} ({Size} bytes)", fileName, fileInfo.Length);

        try
        {
            // 1. Initiate Resumable Upload
            var metadata = new GoogleDriveModels.UploadMetadata { Name = fileName, Parents = new[] { folderId } };
            var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            request.Content = JsonContent.Create(metadata, SourceGenerationContext.Default.UploadMetadata);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return false;

            var uploadUrl = response.Headers.Location;
            if (uploadUrl == null) return false;

            // 2. Upload Data in chunks
            using var fileStream = File.OpenRead(filePath);
            var bufferSize = 10 * 1024 * 1024; // 10MB chunks
            var buffer = new byte[bufferSize];
            long bytesUploaded = 0;

            while (bytesUploaded < fileInfo.Length)
            {
                var bytesToRead = (int)Math.Min(bufferSize, fileInfo.Length - bytesUploaded);
                var read = await fileStream.ReadAsync(buffer.AsMemory(0, bytesToRead));

                var chunkContent = new ByteArrayContent(buffer, 0, read);
                chunkContent.Headers.ContentRange = new ContentRangeHeaderValue(bytesUploaded, bytesUploaded + read - 1, fileInfo.Length);
                
                var chunkResponse = await _httpClient.PutAsync(uploadUrl, chunkContent);
                
                bytesUploaded += read;
                progress.Report(bytesUploaded);

                if (bytesUploaded < fileInfo.Length && chunkResponse.StatusCode != (System.Net.HttpStatusCode)308)
                {
                     _logger.LogError("Upload interrupted at {Pos}", bytesUploaded);
                     return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload failed for {Name}", fileName);
            return false;
        }
    }

    public async Task PurgeOldBackupsAsync(string folderId, int retentionDays)
    {
        if (string.IsNullOrEmpty(_accessToken)) return;

        _logger.LogInformation("Checking for backups older than {Days} days...", retentionDays);

        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
            var cutoffStr = cutoffDate.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var query = $"'{folderId}' in parents and modifiedTime < '{cutoffStr}' and trashed = false";
            var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}&fields=files(id,name,modifiedTime)";

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            var response = await _httpClient.GetFromJsonAsync(url, SourceGenerationContext.Default.FileList);

            if (response?.Files != null && response.Files.Count > 0)
            {
                foreach (var file in response.Files)
                {
                    _logger.LogInformation("Deleting old backup: {Name} (Modified: {Time})", file.Name, file.ModifiedTime);
                    await _httpClient.DeleteAsync($"https://www.googleapis.com/drive/v3/files/{file.Id}");
                }
            }
            else
            {
                _logger.LogInformation("No old backups found to delete.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Maintenance failed");
        }
    }
}
