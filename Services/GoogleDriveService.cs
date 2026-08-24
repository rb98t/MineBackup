using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MineBackup.Services;

public class GoogleDriveService
{
    /// <summary>
    /// How long before actual expiry a token is treated as stale. Google issues one-hour tokens, and a
    /// single upload can easily straddle the boundary.
    /// </summary>
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private readonly ILogger<GoogleDriveService> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    // Captured during AuthenticateAsync so a later refresh does not have to re-read the files.
    private string? _refreshToken;
    private string? _clientId;
    private string? _clientSecret;
    private string? _tokenPath;

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

            _tokenPath = tokenPath;
            _refreshToken = tokenData.RefreshToken;

            if (credentialsPath != null)
            {
                var credsJson = await File.ReadAllTextAsync(credentialsPath);
                using var doc = JsonDocument.Parse(credsJson);
                var root = doc.RootElement.GetProperty("installed");
                _clientId = root.GetProperty("client_id").GetString();
                _clientSecret = root.GetProperty("client_secret").GetString();
            }

            if (!string.IsNullOrEmpty(_refreshToken) && _clientId != null && _clientSecret != null
                && await RefreshAccessTokenAsync())
            {
                return true;
            }

            // Fall back to whatever the file held. It may already be expired -- treat it as such so the
            // first EnsureValidTokenAsync retries the refresh rather than firing a doomed request.
            _accessToken = tokenData.AccessToken;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow;
            return !string.IsNullOrEmpty(_accessToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication failed");
            return false;
        }
    }

    /// <summary>
    /// Exchanges the refresh token for a fresh access token and persists it. Callers must hold
    /// <see cref="_tokenLock"/> unless they are the single-threaded startup path.
    /// </summary>
    private async Task<bool> RefreshAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(_refreshToken) || _clientId == null || _clientSecret == null) return false;

        try
        {
            var values = new Dictionary<string, string>
            {
                { "client_id", _clientId },
                { "client_secret", _clientSecret },
                { "refresh_token", _refreshToken },
                { "grant_type", "refresh_token" }
            };

            var response = await _httpClient.PostAsync(
                "https://oauth2.googleapis.com/token", new FormUrlEncodedContent(values));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token refresh rejected: {Status}", response.StatusCode);
                return false;
            }

            var newTokenData = await response.Content.ReadFromJsonAsync(SourceGenerationContext.Default.TokenResponse);
            if (newTokenData == null || string.IsNullOrEmpty(newTokenData.AccessToken)) return false;

            _accessToken = newTokenData.AccessToken;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                newTokenData.ExpiresIn > 0 ? newTokenData.ExpiresIn : 3600);

            // A refresh response omits the refresh token; keep the one we already have.
            if (string.IsNullOrEmpty(newTokenData.RefreshToken)) newTokenData.RefreshToken = _refreshToken;
            _refreshToken = newTokenData.RefreshToken;

            if (_tokenPath != null)
            {
                var updatedJson = JsonSerializer.Serialize(newTokenData, SourceGenerationContext.Default.TokenResponse);
                await File.WriteAllTextAsync(_tokenPath, updatedJson);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh failed");
            return false;
        }
    }

    /// <summary>
    /// Refreshes the access token if it is close to expiry. A full daily run takes well over an hour, so
    /// the maintenance step at the end used to fire with an expired token and fail with 401 -- meaning
    /// retention never actually ran.
    /// </summary>
    private async Task<bool> EnsureValidTokenAsync()
    {
        if (string.IsNullOrEmpty(_accessToken)) return false;
        if (DateTimeOffset.UtcNow < _accessTokenExpiresAt - TokenRefreshMargin) return true;

        await _tokenLock.WaitAsync();
        try
        {
            // Uploads run concurrently; another one may have refreshed while we waited on the lock.
            if (DateTimeOffset.UtcNow < _accessTokenExpiresAt - TokenRefreshMargin) return true;

            if (!await RefreshAccessTokenAsync())
            {
                _logger.LogWarning("Could not refresh the access token; continuing with the current one.");
            }
            return !string.IsNullOrEmpty(_accessToken);
        }
        finally
        {
            _tokenLock.Release();
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
                    _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                        tokenData.ExpiresIn > 0 ? tokenData.ExpiresIn : 3600);
                    _refreshToken = tokenData.RefreshToken;
                    _clientId = clientId;
                    _clientSecret = clientSecret;
                    _tokenPath = saveTokenPath;

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
        if (!await EnsureValidTokenAsync()) return false;

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
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Could not start upload session for {Name}: {Status}", fileName, response.StatusCode);
                return false;
            }

            // The session URL carries its own credentials, so the chunk PUTs below outlive the access token.
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
                if (read == 0)
                {
                    _logger.LogError("{Name} ended early at {Pos} of {Size} bytes", fileName, bytesUploaded, fileInfo.Length);
                    return false;
                }

                var chunkContent = new ByteArrayContent(buffer, 0, read);
                chunkContent.Headers.ContentRange = new ContentRangeHeaderValue(bytesUploaded, bytesUploaded + read - 1, fileInfo.Length);

                var chunkResponse = await _httpClient.PutAsync(uploadUrl, chunkContent);

                bytesUploaded += read;
                progress.Report(bytesUploaded);

                if (bytesUploaded < fileInfo.Length)
                {
                    if (chunkResponse.StatusCode != (HttpStatusCode)308)
                    {
                        _logger.LogError("Upload interrupted at {Pos}", bytesUploaded);
                        return false;
                    }
                }
                else if (!chunkResponse.IsSuccessStatusCode)
                {
                    // The final PUT is the one that commits the file. Reporting success here would make
                    // the caller delete the local zip, losing the backup entirely.
                    _logger.LogError("Final chunk rejected for {Name}: {Status}", fileName, chunkResponse.StatusCode);
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
        if (!await EnsureValidTokenAsync())
        {
            _logger.LogError("Skipping maintenance: no usable access token.");
            return;
        }

        _logger.LogInformation("Checking for backups older than {Days} days...", retentionDays);

        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
            var cutoffStr = cutoffDate.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var query = $"'{folderId}' in parents and modifiedTime < '{cutoffStr}' and trashed = false";

            // Collect every page before deleting anything: deleting mid-pagination shifts the result set
            // and makes nextPageToken skip entries. Previously only the first page was ever considered.
            var stale = new List<GoogleDriveModels.File>();
            string? pageToken = null;
            do
            {
                var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}"
                          + "&fields=nextPageToken,files(id,name,modifiedTime)&pageSize=1000";
                if (!string.IsNullOrEmpty(pageToken)) url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

                var page = await SendAuthorizedAsync(HttpMethod.Get, url);
                page.EnsureSuccessStatusCode();

                var list = await page.Content.ReadFromJsonAsync(SourceGenerationContext.Default.FileList);
                if (list?.Files != null) stale.AddRange(list.Files);
                pageToken = list?.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            if (stale.Count == 0)
            {
                _logger.LogInformation("No old backups found to delete.");
                return;
            }

            foreach (var file in stale)
            {
                _logger.LogInformation("Deleting old backup: {Name} (Modified: {Time})", file.Name, file.ModifiedTime);
                var delete = await SendAuthorizedAsync(HttpMethod.Delete, $"https://www.googleapis.com/drive/v3/files/{file.Id}");
                if (!delete.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Could not delete {Name}: {Status}", file.Name, delete.StatusCode);
                }
            }

            _logger.LogInformation("Maintenance finished, {Count} backup(s) removed.", stale.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Maintenance failed");
        }
    }

    /// <summary>
    /// Sends a request with a per-call Authorization header. The HttpClient is a singleton shared with the
    /// concurrent uploads, so setting DefaultRequestHeaders on it would race with them.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string url)
    {
        await EnsureValidTokenAsync();
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return await _httpClient.SendAsync(request);
    }
}
