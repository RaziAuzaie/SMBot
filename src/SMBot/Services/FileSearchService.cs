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
// Stored compactly (about 200 bytes a file): each folder's path is kept once and shared by its files,
// and each file has one short lowercase key of its name's words. Everything else is worked out while searching.
public static class FileSearchService
{
    // Key = the name's words in lowercase, split on separators and camelCase: "ReminderParser2.cs" -> "reminder parser 2"
    sealed record Entry(FolderNode Folder, string Name, string Key, bool IsFolder, DateTime Modified)
    {
        public string FullPath => Path.Join(Folder.Path, Name);
    }

    sealed class FolderNode(string path)
    {
        public readonly string Path = path;
        public readonly Dictionary<string, Entry> Items = new(StringComparer.OrdinalIgnoreCase);
    }

    static readonly object Gate = new();
    static Dictionary<string, FolderNode> _folders = new(StringComparer.OrdinalIgnoreCase);
    static int _count;
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
    public static int Count { get { lock (Gate) return _count; } }
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
        var built = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var root in folders) count += Scan(root, built);
        lock (Gate) (_folders, _count) = (built, count);
        Ready = true;
        foreach (var root in folders) Watch(root);
        // Scanning creates lots of short-lived objects; give that memory back to Windows now
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        IndexChanged?.Invoke();
    });

    static int Scan(string root, Dictionary<string, FolderNode> into)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            var node = new FolderNode(dir);
            try
            {
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    var isFolder = info is DirectoryInfo;
                    if (isFolder && Skip(info.Name)) continue;
                    node.Items[info.Name] = MakeEntry(node, info.Name, isFolder, info.LastWriteTime);
                    count++;
                    if (isFolder) pending.Push(info.FullName);
                }
            }
            catch (Exception) { /* folder vanished or can't be read */ }
            if (node.Items.Count > 0) into[dir] = node;
        }
        return count;
    }

    static bool Skip(string folderName) => SkipFolders.Contains(folderName.ToLowerInvariant());

    static Entry MakeEntry(FolderNode folder, string name, bool isFolder, DateTime modified)
    {
        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var words = stem.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => CamelSplit.Split(part)).Where(p => p.Length > 0).Select(p => p.ToLowerInvariant());
        var key = string.Join(' ', words);
        return new(folder, name, key.Length > 0 ? key : stem.ToLowerInvariant(), isFolder, modified);
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
            var folder = Path.GetDirectoryName(path)!;
            lock (Gate)
            {
                if (!_folders.TryGetValue(folder, out var node)) _folders[folder] = node = new FolderNode(folder);
                if (!node.Items.ContainsKey(info.Name)) _count++;
                node.Items[info.Name] = MakeEntry(node, info.Name, isFolder, info.LastWriteTime);
            }
        }
        catch (Exception) { }
    }

    static void Remove(string path)
    {
        lock (Gate)
        {
            if (_folders.TryGetValue(Path.GetDirectoryName(path) ?? "", out var node) && node.Items.Remove(Path.GetFileName(path))) _count--;
            // A deleted folder takes its contents with it
            var prefix = path + "\\";
            foreach (var key in _folders.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)
                                                       || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _count -= _folders[key].Items.Count;
                _folders.Remove(key);
            }
        }
    }

    // A query word, with " word" ready for "starts a word in the key" checks
    readonly record struct Word(string Text, string AfterSpace);

    // Query words must all match somewhere. Filters: ".pdf" or "type:excel" (kind of file), "in:downloads" (folder).
    public static List<FileHit> Search(string query, ISet<string> recentPaths, int limit = 12)
    {
        var words = new List<Word>();
        string[]? exts = null;
        string? inFolder = null;
        foreach (var raw in query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("type:") && Types.TryGetValue(raw[5..], out var t)) exts = t;
            else if (raw.StartsWith("in:") && raw.Length > 3) inFolder = raw[3..];
            else if (raw.Length > 1 && raw[0] == '.' && !raw[1..].Contains('.')) exts = [raw];
            else words.Add(new Word(raw, " " + raw));
        }
        if (words.Count == 0 && exts is null && inFolder is null) return [];

        var hits = new List<FileHit>();
        var partial = new List<FileHit>();   // used only when nothing matches every word
        lock (Gate)
        {
            foreach (var node in _folders.Values)
            {
                if (inFolder is not null && !node.Path.Contains(inFolder, StringComparison.OrdinalIgnoreCase)) continue;
                var depth = node.Path.Count(c => c == '\\') + 1;
                foreach (var e in node.Items.Values)
                {
                    if (exts is not null && (e.IsFolder || !exts.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))) continue;

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

                    // Recent and recently changed files first; folders a little lower than files; shallower paths slightly first
                    var fullPath = e.FullPath;
                    if (recentPaths.Contains(fullPath)) score += 25;
                    var age = (DateTime.Now - e.Modified).TotalDays;
                    score += age < 1 ? 15 : age < 7 ? 10 : age < 30 ? 5 : 0;
                    if (e.IsFolder) score -= 5;
                    score -= Math.Min(10, depth - 3);
                    target.Add(new FileHit(fullPath, e.IsFolder, e.Modified, score));
                }
            }
        }
        return (hits.Count > 0 ? hits : partial).OrderByDescending(h => h.Score).ThenByDescending(h => h.Modified).Take(limit).ToList();
    }

    // How well one query word matches: whole name > start of name > start of a word > anywhere in the name
    // > initials ("qr" -> "Quarterly Report") > folder path > one typo > letters in order ("bdywnd" -> "BuddyWindow")
    static int WordScore(Word word, Entry e)
    {
        var w = word.Text;
        var name = e.Name.AsSpan();
        var stem = e.IsFolder ? name : Path.GetFileNameWithoutExtension(name);
        const StringComparison Ci = StringComparison.OrdinalIgnoreCase;
        if (stem.Equals(w, Ci) || name.Equals(w, Ci)) return 100;
        if (name.StartsWith(w, Ci)) return 70;
        if (name.Contains(w, Ci))
            return e.Key.StartsWith(w, StringComparison.Ordinal) || e.Key.Contains(word.AfterSpace, StringComparison.Ordinal) ? 55 : 35;
        if (w.Length >= 2 && InitialsStartWith(e.Key, w)) return 40;
        if (e.Folder.Path.Contains(w, Ci)) return 12;
        if (w.Length >= 4)
        {
            var typo = TypoScore(e.Key, w);
            if (typo > 0) return typo;
        }
        if (w.Length >= 3 && HasWordStartingWith(e.Key, w[0]) && InOrder(stem, w)) return 10;
        return 0;
    }

    static readonly char[] Separators = [' ', '_', '-', '.', '(', ')', '[', ']', ',', '&'];
    static readonly System.Text.RegularExpressions.Regex CamelSplit =
        new("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])");

    // The first letters of the key's words start with w ("qr" for "quarterly report")
    static bool InitialsStartWith(string key, string w)
    {
        var i = 0;
        for (var j = 0; j < key.Length && i < w.Length; j++)
            if ((j == 0 || key[j - 1] == ' ') && key[j] != ' ')
            {
                if (key[j] != w[i]) return false;
                i++;
            }
        return i == w.Length;
    }

    static bool HasWordStartingWith(string key, char c)
    {
        for (var j = 0; j < key.Length; j++)
            if (key[j] == c && (j == 0 || key[j - 1] == ' ')) return true;
        return false;
    }

    // One typo: a missed letter ("remnder" -> reminder) is likelier than an extra one ("remnder" -> render)
    static int TypoScore(string key, string w)
    {
        var best = 0;
        foreach (var range in key.AsSpan().Split(' '))
        {
            var part = key.AsSpan(range);
            if (part.Length == 0) continue;
            if (part.Length >= w.Length && (OneTypoAway(part, w) || OneTypoAway(part[..w.Length], w))) return 22;
            if (best == 0 && OneTypoAway(part, w)) best = 8;
        }
        return best;
    }

    // Letters of w appear in s in the same order, not too spread out (case-insensitive)
    static bool InOrder(ReadOnlySpan<char> s, string w)
    {
        int i = 0, first = -1, last = -1;
        for (var j = 0; j < s.Length && i < w.Length; j++)
            if (char.ToLowerInvariant(s[j]) == w[i]) { if (first < 0) first = j; last = j; i++; }
        return i == w.Length && last - first < w.Length * 2;
    }

    // Damerau-Levenshtein distance of at most 1 (one missing, extra, wrong or swapped letter)
    static bool OneTypoAway(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
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
        var s = a.Length < b.Length ? a : b;
        var l = a.Length < b.Length ? b : a;
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
