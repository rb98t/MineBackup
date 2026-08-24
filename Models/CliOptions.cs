namespace MineBackup.Models;

/// <summary>
/// Command line overrides for a single run. With no arguments every property is empty and the app
/// behaves exactly as it always has: the full pipeline described by config.json.
/// </summary>
public class CliOptions
{
    public List<string> Sources { get; } = new();
    public List<string> Databases { get; } = new();
    public string? Prefix { get; set; }
    public bool NoPurge { get; set; }
    public string? DriveFolderId { get; set; }
    public bool ShowHelp { get; set; }

    /// <summary>
    /// True when the caller named what to back up. The two lists then replace the configured ones
    /// wholesale -- <c>--source X</c> on its own means "that folder and nothing else", databases
    /// included. Anything not named is skipped.
    /// </summary>
    public bool IsTargetedRun => Sources.Count > 0 || Databases.Count > 0;

    public const string HelpText = """
        MineBackup -- Minecraft es weboldal biztonsagi mentes

        Hasznalat:
          MineBackup.exe                      A teljes napi mentes a config.json alapjan.
          MineBackup.exe [kapcsolok]          Celzott, egyszeri mentes.

        Kapcsolok:
          --source <utvonal>       Ezt a mappat mentse (tobbszor is megadhato).
          --database <nev>         Ezt az adatbazist mentse (tobbszor is megadhato).
          --prefix <szoveg>        Elotag minden letrejovo zip nevehez (pl. RELEASE).
          --drive-folder <id>      Mas Google Drive mappaba toltson fel.
          --no-purge               Ne fusson le a retencios takaritas.
          -h, --help               Ez a sugo.

        Ha van --source vagy --database, akkor CSAK a felsoroltak mentodnek -- a config.json
        backup_sources es mysql.databases listai erre a futasra nem ervenyesek.

        Pelda (release elotti mentes):
          MineBackup.exe --source "D:\Backups\minesite\predeploy_2026-08-24" --prefix RELEASE --no-purge
        """;

    /// <summary>
    /// Hand-rolled because the app is published with PublishAot, which rules out reflection-based
    /// argument binders.
    /// </summary>
    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        options = new CliOptions();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    return true;

                case "--no-purge":
                    options.NoPurge = true;
                    break;

                case "--source":
                    if (!TryTakeValue(args, ref i, arg, out var source, out error)) return false;
                    options.Sources.Add(source);
                    break;

                case "--database":
                    if (!TryTakeValue(args, ref i, arg, out var database, out error)) return false;
                    options.Databases.Add(database);
                    break;

                case "--prefix":
                    if (!TryTakeValue(args, ref i, arg, out var prefix, out error)) return false;
                    options.Prefix = prefix;
                    break;

                case "--drive-folder":
                    if (!TryTakeValue(args, ref i, arg, out var folder, out error)) return false;
                    options.DriveFolderId = folder;
                    break;

                default:
                    error = $"Ismeretlen kapcsolo: {arg}";
                    return false;
            }
        }

        return true;
    }

    private static bool TryTakeValue(string[] args, ref int i, string name, out string value, out string? error)
    {
        if (i + 1 >= args.Length)
        {
            value = string.Empty;
            error = $"A(z) {name} kapcsolo utan ertek kell.";
            return false;
        }

        value = args[++i];
        error = null;
        return true;
    }
}
