namespace MineBackup.Models;

/// <summary>What a run produced, in the form the notifier needs to describe it.</summary>
public class BackupOutcome
{
    public bool Success { get; set; }
    public string Description { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string? Error { get; set; }
    public List<BackupItemResult> Items { get; } = [];

    public int Succeeded => Items.Count(i => i.Success);
    public int Failed => Items.Count(i => !i.Success);
}

public class BackupItemResult
{
    public required string Name { get; init; }
    public required bool Success { get; init; }
}
