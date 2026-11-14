using System.Text.Json;

namespace Spotify;

/// <summary>A song that was played, and when (Unix seconds).</summary>
internal sealed record Played(string Title, string Artist, long At);

/// <summary>
/// The songs played lately (newest first, up to 50), kept in a small JSON file between game
/// sessions, plus this session's count and listening time. No game calls.
/// </summary>
internal sealed class History
{
    public const int Max = 50;
    readonly string? file;
    readonly List<Played> songs = new();
    readonly object gate = new();

    public int SessionSongs { get; private set; }
    public double SessionSeconds { get; private set; }

    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JibbersSpotify", "history.json");

    public History(string? file)
    {
        this.file = file;
        if (file is null || !File.Exists(file)) return;
        try
        {
            songs.AddRange((JsonSerializer.Deserialize<List<Played>>(File.ReadAllText(file)) ?? new()).Where(p => p is { Title: not null, Artist: not null }).Take(Max));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
        }
    }

    public IReadOnlyList<Played> Songs { get { lock (gate) return songs.ToArray(); } }

    /// <summary>Notes a song as playing now (once, however long it plays). Returns whether it was new.</summary>
    public bool Add(string title, string artist, long now)
    {
        lock (gate)
        {
            if (songs.Count > 0 && songs[0].Title == title && songs[0].Artist == artist) return false;
            songs.Insert(0, new Played(title, artist, now));
            if (songs.Count > Max) songs.RemoveRange(Max, songs.Count - Max);
            SessionSongs++;
        }
        Save();
        return true;
    }

    public void Listened(double seconds) => SessionSeconds += seconds;

    void Save()
    {
        if (file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            lock (gate) File.WriteAllText(file, JsonSerializer.Serialize(songs));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>"now", "5 min ago", "3 h ago", "2 d ago".</summary>
    public static string Ago(long seconds) => seconds switch
    {
        < 60 => "now",
        < 3600 => $"{seconds / 60} min ago",
        < 86400 => $"{seconds / 3600} h ago",
        _ => $"{seconds / 86400} d ago",
    };
}
