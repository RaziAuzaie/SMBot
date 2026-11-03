using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMBot.Services;

public record BotPosition(double Left, double Top);

public class Settings
{
    public string UserName { get; set; } = "";
    public HashSet<string> DismissedEmails { get; set; } = [];   // Outlook EntryIDs marked as done
    // Where the bot was last dragged to, per PC (their screens differ); none = bottom-right corner
    public Dictionary<string, BotPosition> BotPositions { get; set; } = [];
    public string BotSize { get; set; } = "medium";             // small, medium, large
    public bool HeadOnly { get; set; }                           // show just the floating head, without the body

    // Position from before it was kept per PC; only read when moving old settings in
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? BotLeft { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? BotTop { get; set; }

    [JsonIgnore] public BotPosition? MyPosition
    {
        get => BotPositions.GetValueOrDefault(Environment.MachineName);
        set { if (value is null) BotPositions.Remove(Environment.MachineName); else BotPositions[Environment.MachineName] = value; }
    }
}

// Settings are shared by both PCs (see DataFolder)
public static class SettingsService
{
    static readonly string FilePath = DataFolder.PathOf("settings.json");

    // A broken or missing file just means onboarding runs again
    public static Settings Load()
    {
        var settings = DataFolder.Read<Settings>(FilePath) ?? new();
        // An old single position becomes this PC's
        if (settings.BotLeft is { } left && settings.BotTop is { } top)
        {
            settings.MyPosition ??= new BotPosition(left, top);
            (settings.BotLeft, settings.BotTop) = (null, null);
        }
        return settings;
    }

    public static void Save(Settings settings)
    {
        // The other PC may have saved since this one loaded: keep its bot position and its dismissed emails
        if (DataFolder.Read<Settings>(FilePath) is { } onDisk)
        {
            foreach (var (pc, position) in onDisk.BotPositions)
                if (pc != Environment.MachineName) settings.BotPositions[pc] = position;
            settings.DismissedEmails.UnionWith(onDisk.DismissedEmails);
        }
        Directory.CreateDirectory(DataFolder.Path);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, DataFolder.Json));
    }

    // First name from the Windows account's display name, read locally. Empty if there's nothing useful.
    public static string SuggestName()
    {
        var size = 256u;
        var buffer = new StringBuilder((int)size);
        var name = GetUserNameEx(NameDisplay, buffer, ref size) ? buffer.ToString() : Environment.UserName;

        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        string[] generic = ["admin", "administrator", "user", "owner"];
        return generic.Contains(first.ToLowerInvariant()) ? "" : first;
    }

    const int NameDisplay = 3;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserNameEx(int nameFormat, StringBuilder buffer, ref uint size);
}
