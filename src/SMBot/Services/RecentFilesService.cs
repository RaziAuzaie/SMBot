using System.Diagnostics;
using System.IO;

namespace SMBot.Services;

public record RecentFile(string FilePath, DateTime Opened)
{
    public string Name => Path.GetFileName(FilePath);
    public string Folder => Path.GetDirectoryName(FilePath) ?? "";

    public string Age
    {
        get
        {
            var age = DateTime.Now - Opened;
            return age.TotalDays >= 1 ? $"{(int)age.TotalDays}d ago" : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h ago" : $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        }
    }
}

// Reads Windows' own "Recent items" list (%APPDATA%\Microsoft\Windows\Recent), all local
public static class RecentFilesService
{
    static readonly string RecentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);

    public static List<RecentFile> GetRecent(int count = 5)
    {
        var result = new List<RecentFile>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;

        // Newest shortcuts first; each one points at a file or folder that was opened
        var links = new DirectoryInfo(RecentFolder).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(60);
        foreach (var link in links)
        {
            string target;
            try { target = shell.CreateShortcut(link.FullName).TargetPath; }
            catch { continue; }

            // Files only (folders and deleted files are skipped), each once
            if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
            if (result.Any(r => r.FilePath.Equals(target, StringComparison.OrdinalIgnoreCase))) continue;

            result.Add(new RecentFile(target, link.LastWriteTime));
            if (result.Count == count) break;
        }
        return result;
    }

    // Opens File Explorer at the file's folder with the file selected
    public static void ShowInFolder(RecentFile file) =>
        Process.Start("explorer.exe", $"/select,\"{file.FilePath}\"");
}
