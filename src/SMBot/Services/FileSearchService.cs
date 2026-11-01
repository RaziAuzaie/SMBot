using System.Diagnostics;
using System.IO;

namespace SMBot.Services;

public record FileHit(string FullPath, bool IsFolder, DateTime Modified, int Score)
{
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";

    public string When
    {
        get
        {
            var days = (DateTime.Today - Modified.Date).Days;
            return days switch { 0 => $"today {Modified:h:mm tt}", 1 => "yesterday", < 7 => Modified.ToString("dddd"), _ => Modified.ToString("d MMM yyyy") };
        }
    }

    // Segoe Fluent Icons glyph by kind of file
    public string Glyph => IsFolder ? "" : Path.GetExtension(FullPath).ToLowerInvariant() switch
    {
        ".xls" or ".xlsx" or ".xlsm" or ".csv" => "",
        ".doc" or ".docx" or ".rtf" => "",
        ".pdf" => "",
        ".ppt" or ".pptx" => "",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".heic" => "",
        ".mp4" or ".mov" or ".avi" or ".mkv" => "",
        ".zip" or ".rar" or ".7z" => "",
        ".txt" or ".log" or ".md" or ".json" or ".xml" or ".ini" or ".cfg" => "",
        ".exe" or ".msi" or ".bat" or ".ps1" or ".cmd" => "",
        _ => "",
    };
}

// Keeps its own in-memory list of the user's files (names only, all local) so search is instant,
// forgiving about typos and partial words, and ranks recent files first.
public static class FileSearchService
{
    // Words and initials of the name are worked out once here, not on every keystroke
    record Entry(string FullPath, string NameLower, string PathLower, string Ext, bool IsFolder, DateTime Modified,
                 string Stem, string[] Words, string Initials);

    static readonly object Gate = new();
    static Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<FileSystemWatcher> Watchers = [];
    static readonly string[] SkipFolders = ["node_modules", ".git", "bin", "obj", "$recycle.bin", ".vs", "__pycache__", ".cache"];

    static readonly Dictionary<string, string[]> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["excel"] = [".xls", ".xlsx", ".xlsm", ".csv"],
        ["word"] = [".doc", ".docx", ".rtf"],
        ["pdf"] = [".pdf"],
        ["ppt"] = [".ppt", ".pptx"], ["powerpoint"] = [".ppt", ".pptx"],
        ["image"] = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic"], ["pic"] = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic"],
        ["video"] = [".mp4", ".mov", ".avi", ".mkv"],
        ["zip"] = [".zip", ".rar", ".7z"],
        ["text"] = [".txt", ".log", ".md", ".csv", ".json", ".xml"],
    };

    public static bool Ready { get; private set; }
    public static int Count { get { lock (Gate) return _entries.Count; } }
    public static event Action? IndexChanged;

    public static IReadOnlyList<string> DefaultFolders()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetEnvironmentVariable("OneDriveCommercial") ?? "",
            Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "",
        }.Where(f => f.Length > 0 && Directory.Exists(f)).Select(f => f.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Drop folders inside another one in the list (e.g. Documents inside OneDrive)
        return folders.Where(f => !folders.Any(o => o != f && f.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // Builds the list in the background, then keeps it current with folder watchers
    public static Task BuildAsync(IReadOnlyList<string> folders) => Task.Run(() =>
    {
        var sw = Stopwatch.StartNew();
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in folders) Scan(root, entries);
        lock (Gate) _entries = entries;
        Ready = true;
        Debug.WriteLine($"Indexed {entries.Count} entries in {sw.ElapsedMilliseconds} ms");
        foreach (var root in folders) Watch(root);
        IndexChanged?.Invoke();
    });

    static void Scan(string root, Dictionary<string, Entry> into)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    var isFolder = info is DirectoryInfo;
                    if (isFolder && Skip(info.Name)) continue;
                    into[info.FullName] = MakeEntry(info.FullName, isFolder, info.LastWriteTime);
                    if (isFolder) pending.Push(info.FullName);
                }
            }
            catch (Exception) { /* folder vanished or can't be read */ }
        }
    }

    static bool Skip(string folderName) => SkipFolders.Contains(folderName.ToLowerInvariant());

    static Entry MakeEntry(string path, bool isFolder, DateTime modified)
    {
        var original = Path.GetFileName(path);
        var name = original.ToLowerInvariant();
        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        // Split on separators and on camelCase / letter-digit boundaries: "ReminderParser2" -> reminder, parser, 2
        var words = Words(isFolder ? original : Path.GetFileNameWithoutExtension(original))
            .SelectMany(part => CamelSplit.Split(part)).Where(p => p.Length > 0).Select(p => p.ToLowerInvariant()).ToArray();
        if (words.Length == 0) words = [stem];
        return new(path, name, path.ToLowerInvariant(), isFolder ? "" : Path.GetExtension(name), isFolder, modified,
                   stem, words, string.Concat(words.Select(p => p[0])));
    }

    static void Watch(string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            watcher.Created += (_, e) => Update(e.FullPath);
            watcher.Changed += (_, e) => Update(e.FullPath);
            watcher.Deleted += (_, e) => Remove(e.FullPath);
            watcher.Renamed += (_, e) => { Remove(e.OldFullPath); Update(e.FullPath); };
            watcher.EnableRaisingEvents = true;
            lock (Gate) Watchers.Add(watcher);
        }
        catch (Exception) { /* can't watch: the list just won't update until next start */ }
    }

    static void Update(string path)
    {
        if (path.Split('\\').Any(Skip)) return;
        try
        {
            var isFolder = Directory.Exists(path);
            if (!isFolder && !File.Exists(path)) return;
            var info = isFolder ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return;
            lock (Gate) _entries[path] = MakeEntry(path, isFolder, info.LastWriteTime);
        }
        catch (Exception) { }
    }

    static void Remove(string path)
    {
        lock (Gate)
        {
            _entries.Remove(path);
            // A deleted folder takes its contents with it
            var prefix = path + "\\";
            foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _entries.Remove(key);
        }
    }

    // Query words must all match somewhere. Filters: ".pdf" or "type:excel" (kind of file), "in:downloads" (folder).
    public static List<FileHit> Search(string query, ISet<string> recentPaths, int limit = 12)
    {
        var words = new List<string>();
        string[]? exts = null;
        string? inFolder = null;
        foreach (var raw in query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("type:") && Types.TryGetValue(raw[5..], out var t)) exts = t;
            else if (raw.StartsWith("in:") && raw.Length > 3) inFolder = raw[3..];
            else if (raw.Length > 1 && raw[0] == '.' && !raw[1..].Contains('.')) exts = [raw];
            else words.Add(raw);
        }
        if (words.Count == 0 && exts is null && inFolder is null) return [];

        Entry[] snapshot;
        lock (Gate) snapshot = [.. _entries.Values];

        var hits = new List<FileHit>();
        var partial = new List<FileHit>();   // used only when nothing matches every word
        foreach (var e in snapshot)
        {
            if (exts is not null && !exts.Contains(e.Ext)) continue;
            if (inFolder is not null && !FolderMatches(e.PathLower, inFolder)) continue;

            var score = 0;
            var matched = 0;
            foreach (var w in words)
            {
                var s = WordScore(w, e);
                if (s > 0) matched++;
                score += s;
            }
            var target = matched == words.Count ? hits : matched > 0 ? partial : null;
            if (target is null) continue;

            // Recent and recently changed files first; folders a little lower than files
            if (recentPaths.Contains(e.FullPath)) score += 25;
            var age = (DateTime.Now - e.Modified).TotalDays;
            score += age < 1 ? 15 : age < 7 ? 10 : age < 30 ? 5 : 0;
            if (e.IsFolder) score -= 5;
            score -= Math.Min(10, e.FullPath.Count(c => c == '\\') - 3);   // shallower paths slightly first

            target.Add(new FileHit(e.FullPath, e.IsFolder, e.Modified, score));
        }
        return (hits.Count > 0 ? hits : partial).OrderByDescending(h => h.Score).ThenByDescending(h => h.Modified).Take(limit).ToList();
    }

    static bool FolderMatches(string pathLower, string folder) =>
        pathLower.Split('\\').SkipLast(1).Any(part => part.Contains(folder));

    // How well one query word matches: whole name > start of name > start of a word > anywhere in the name
    // > in-order letters ("qr" -> "Quarterly Report") > folder path > one typo
    static int WordScore(string w, Entry e)
    {
        if (e.Stem == w || e.NameLower == w) return 100;
        if (e.NameLower.StartsWith(w, StringComparison.Ordinal)) return 70;
        if (e.NameLower.Contains(w, StringComparison.Ordinal))
            return e.Words.Any(part => part.StartsWith(w, StringComparison.Ordinal)) ? 55 : 35;
        if (w.Length >= 2 && e.Initials.StartsWith(w, StringComparison.Ordinal)) return 40;
        if (e.PathLower.Contains(w, StringComparison.Ordinal)) return 12;
        // One typo: a missed letter ("remnder" -> reminder) is likelier than an extra one ("remnder" -> render)
        if (w.Length >= 4 && e.Words.Any(part => part.Length >= w.Length && (OneTypoAway(part, w) || OneTypoAway(part[..w.Length], w)))) return 22;
        if (w.Length >= 4 && e.Words.Any(part => OneTypoAway(part, w))) return 8;
        if (w.Length >= 3 && e.Words.Any(part => part[0] == w[0]) && InOrder(e.Stem, w)) return 10;
        return 0;
    }

    static readonly char[] Separators = [' ', '_', '-', '.', '(', ')', '[', ']', ',', '&'];
    static readonly System.Text.RegularExpressions.Regex CamelSplit =
        new("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])");

    static IEnumerable<string> Words(string s) => s.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    // Letters of w appear in s in the same order ("bdywnd" -> "buddywindow"), not too spread out.
    // WordScore only tries this when w starts with the first letter of one of the name's words
    static bool InOrder(string s, string w)
    {
        int i = 0, first = -1, last = -1;
        for (var j = 0; j < s.Length && i < w.Length; j++)
            if (s[j] == w[i]) { if (first < 0) first = j; last = j; i++; }
        return i == w.Length && last - first < w.Length * 2;
    }

    // Damerau-Levenshtein distance of at most 1 (one missing, extra, wrong or swapped letter)
    static bool OneTypoAway(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1 || a[0] != b[0] && a.Length > 1 && a[1] != b[0]) return false;
        if (a.Length == b.Length)
        {
            int first = -1, second = -1, count = 0;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) { if (++count > 2) return false; if (first < 0) first = i; else second = i; }
            return count <= 1 || (second == first + 1 && a[first] == b[second] && a[second] == b[first]);
        }
        // One extra letter in the longer word: skip it once and the rest must line up
        var (s, l) = a.Length < b.Length ? (a, b) : (b, a);
        int si = 0, li = 0;
        var skipped = false;
        while (si < s.Length && li < l.Length)
        {
            if (s[si] == l[li]) { si++; li++; }
            else if (skipped) return false;
            else { skipped = true; li++; }
        }
        return true;
    }

    public static void Open(FileHit hit) => Process.Start(new ProcessStartInfo(hit.FullPath) { UseShellExecute = true });

    public static void ShowInFolder(FileHit hit) => Process.Start("explorer.exe", $"/select,\"{hit.FullPath}\"");
}
