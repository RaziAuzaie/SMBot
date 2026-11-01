using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SMBot.Services;

public class Settings
{
    public string UserName { get; set; } = "";
    public int EmailCheckSeconds { get; set; } = 60;
    public HashSet<string> DismissedEmails { get; set; } = [];   // Outlook EntryIDs marked as done
}

public static class SettingsService
{
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMBot");
    static readonly string FilePath = Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch { /* a broken file just means onboarding runs again */ }
        return new();
    }

    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
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
