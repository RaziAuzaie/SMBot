using System.IO;
using System.Text.Json;

namespace SMBot.Services;

// SMBot's checklist and settings live in src\SMBot\data, inside the OneDrive project, so both PCs share one checklist.
// Git ignores the folder, so personal tasks and notes never go to GitHub; a fresh copy of the project starts empty.
public static class DataFolder
{
    public static readonly string Path = Find();

    // Where SMBot kept its data before, one copy per PC
    static readonly string OldPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMBot");

    // The project folder (the one with SMBot.csproj) above SMBot.exe, so a build in bin\ uses the same data.
    // An exe copied somewhere else keeps its data next to itself.
    static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "SMBot.csproj")))
                return System.IO.Path.Combine(dir.FullName, "data");
        return System.IO.Path.Combine(AppContext.BaseDirectory, "data");
    }

    public static string PathOf(string name) => System.IO.Path.Combine(Path, name);

    // Once per PC: bring this PC's old checklist and settings in. If the other PC moved its data in first, this PC's
    // tasks are added to the shared list. The old files stay behind, renamed, as a backup.
    public static void MoveOldData()
    {
        Directory.CreateDirectory(Path);
        MoveOld("reminders.json", (old, shared) =>
        {
            var tasks = Read<List<Reminder>>(shared) ?? [];
            tasks.AddRange((Read<List<Reminder>>(old) ?? []).Where(r => tasks.All(t => t.Id != r.Id)));
            return tasks;
        });
        MoveOld("settings.json", (old, shared) =>
        {
            var settings = Read<Settings>(shared) ?? new();
            var mine = Read<Settings>(old);
            if (mine is not null)
            {
                if (mine.BotLeft is { } left && mine.BotTop is { } top)
                    settings.BotPositions.TryAdd(Environment.MachineName, new BotPosition(left, top));
                settings.DismissedEmails.UnionWith(mine.DismissedEmails);
            }
            return settings;
        });
    }

    static void MoveOld<T>(string name, Func<string, string, T> merge)
    {
        var old = System.IO.Path.Combine(OldPath, name);
        if (!System.IO.File.Exists(old)) return;
        var shared = PathOf(name);
        if (!System.IO.File.Exists(shared))
            System.IO.File.Copy(old, shared);
        else
            System.IO.File.WriteAllText(shared, JsonSerializer.Serialize(merge(old, shared), Json));
        System.IO.File.Move(old, old + ".moved-to-project", overwrite: true);
    }

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static T? Read<T>(string path)
    {
        try { return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(path), Json) : default; }
        catch { return default; }
    }
}
