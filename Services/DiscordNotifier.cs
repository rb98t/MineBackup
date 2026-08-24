using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using MineBackup.Models;

namespace MineBackup.Services;

/// <summary>
/// Posts the outcome of a run to a Discord webhook.
///
/// This exists because the failures that mattered were the silent ones. Retention had been failing
/// with a 401 on every single nightly run for months, and the 65 GB world archive was quietly not
/// reaching Drive, and nothing anywhere said so: the console output scrolled past in a scheduled
/// task nobody watches, and the log file only gets read once somebody already suspects a problem.
/// </summary>
public class DiscordNotifier(ILogger<DiscordNotifier> logger, HttpClient httpClient)
{
    private const int ColourSuccess = 0x2ECC71;
    private const int ColourFailure = 0xE74C3C;

    public async Task NotifyAsync(string? webhookUrl, BackupOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;

        try
        {
            var fields = new List<DiscordModels.EmbedField>
            {
                new() { Name = "Időtartam", Value = FormatDuration(outcome.Duration), Inline = true },
                new() { Name = "Feltöltve", Value = $"{outcome.Succeeded} db", Inline = true }
            };

            if (outcome.Failed > 0)
            {
                fields.Add(new DiscordModels.EmbedField { Name = "Hibás", Value = $"{outcome.Failed} db", Inline = true });
            }

            if (outcome.Items.Count > 0)
            {
                // Discord rejects embed field values over 1024 characters outright, which would turn
                // a failure notification into a silent failure of its own.
                var lines = outcome.Items.Select(i => (i.Success ? "✅ " : "❌ ") + i.Name);
                fields.Add(new DiscordModels.EmbedField
                {
                    Name = "Elemek",
                    Value = Truncate(string.Join("\n", lines), 1024),
                    Inline = false
                });
            }

            if (!string.IsNullOrEmpty(outcome.Error))
            {
                fields.Add(new DiscordModels.EmbedField { Name = "Hiba", Value = Truncate(outcome.Error, 1024), Inline = false });
            }

            var payload = new DiscordModels.WebhookPayload
            {
                Username = "MineBackup",
                Embeds =
                [
                    new DiscordModels.Embed
                    {
                        Title = outcome.Success ? "Biztonsági mentés kész" : "Biztonsági mentés HIBÁVAL állt le",
                        Description = outcome.Description,
                        Color = outcome.Success ? ColourSuccess : ColourFailure,
                        Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                        Fields = fields
                    }
                ]
            };

            var response = await httpClient.PostAsJsonAsync(
                webhookUrl, payload, SourceGenerationContext.Default.WebhookPayload);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("A Discord ertesites nem ment ki: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // A backup that worked must not be reported as failed because the notification did not
            // go out, so this never propagates.
            logger.LogWarning(ex, "A Discord ertesites kikuldese nem sikerult.");
        }
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} ó {d.Minutes} p" : $"{d.Minutes} p {d.Seconds} mp";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 3)] + "...";
}
