using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMBot.Services;

public class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public DateTime? Due { get; set; }            // null = anytime, no alert
    public string? Repeat { get; set; }           // daily, weekdays, weekly:Friday, biweekly, monthly, monthday:15, monthend
    public bool High { get; set; }                // typed with "!"
    public DateTime? DoneAt { get; set; }         // set when ticked (repeating ones move to the next date instead)
    public string? Note { get; set; }             // remarks typed under the task in the checklist

    [JsonIgnore] public bool IsDone => DoneAt is not null;

    [JsonIgnore] public string RepeatText => Repeat switch
    {
        null => "",
        "daily" => "every day",
        "weekdays" => "every weekday",
        "biweekly" => "every 2 weeks",
        "monthly" => "every month",
        "monthend" => "every month end",
        _ when Repeat.StartsWith("weekly:") => "every " + Repeat[7..],
        _ when Repeat.StartsWith("monthday:") => $"every month on the {Ordinal(int.Parse(Repeat[9..]))}",
        _ => Repeat,
    };

    // The "when" written so the parser reads it back exactly, to pre-fill the edit box
    [JsonIgnore] public string WhenForEditing
    {
        get
        {
            if (Due is not { } due) return RepeatText;
            var time = due.ToString("h:mmtt").ToLowerInvariant();
            var date = due.ToString("d/M/yyyy");
            return Repeat switch
            {
                null => $"{date} {time}",
                "biweekly" or "monthly" => $"{RepeatText} {date} {time}",   // these need a start date
                _ => $"{RepeatText} {time}",
            };
        }
    }

    static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    [JsonIgnore] public string When
    {
        get
        {
            var repeat = RepeatText;
            if (Due is not { } due) return repeat.Length > 0 ? "↻ " + repeat : "anytime";

            var today = DateTime.Today;
            var day = due.Date == today ? "today" : due.Date == today.AddDays(1) ? "tomorrow"
                : due.Date < today.AddDays(7) && due.Date > today ? due.ToString("dddd") : due.ToString("d MMM");
            var text = $"{day} {due:h:mm tt}";
            return repeat.Length > 0 ? $"{text} ↻ {repeat}" : text;
        }
    }
}

// Reminders live in reminders.json in the shared data folder (see DataFolder), used by both PCs through OneDrive.
// So every change starts from the file as it is now and is applied by id: a tick on this PC never undoes a task
// the other PC just added. When OneDrive brings in the other PC's changes, the list reloads by itself.
public static class ReminderService
{
    static readonly string FilePath = DataFolder.PathOf("reminders.json");
    static string _lastWritten = "";
    static FileSystemWatcher? _watcher;

    public static List<Reminder> All { get; private set; } = Load();

    public static event Action? Changed;

    static List<Reminder> Load() => DataFolder.Read<List<Reminder>>(FilePath) ?? [];

    static void Save()
    {
        Directory.CreateDirectory(DataFolder.Path);
        _lastWritten = JsonSerializer.Serialize(All, DataFolder.Json);
        File.WriteAllText(FilePath, _lastWritten);
        Changed?.Invoke();
    }

    static void Change(Action<List<Reminder>> change)
    {
        All = Load();
        change(All);
        Save();
    }

    static void Change(Reminder r, Action<Reminder> change) =>
        Change(all => { if (all.Find(x => x.Id == r.Id) is { } current) change(current); });

    // Reload when the file changes on disk without this PC writing it (OneDrive syncing the other PC's changes)
    public static void WatchForOtherPc()
    {
        Directory.CreateDirectory(DataFolder.Path);
        _watcher = new FileSystemWatcher(DataFolder.Path, "reminders.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    static void OnFileChanged(object sender, FileSystemEventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(ReloadFromDisk);

    static async void ReloadFromDisk()
    {
        await Task.Delay(500);   // OneDrive may still be writing
        string text;
        try { text = File.ReadAllText(FilePath); } catch (IOException) { return; }
        if (text == _lastWritten) return;
        _lastWritten = text;
        All = Load();
        Changed?.Invoke();
    }

    public static void Add(IEnumerable<Reminder> reminders) => Change(all => all.AddRange(reminders));

    public static void Delete(Reminder r) => Change(all => all.RemoveAll(x => x.Id == r.Id));

    // Repeating reminders move on to their next date; others are ticked off
    public static void Complete(Reminder r) => Change(r, x =>
    {
        if (x.Repeat is not null && x.Due is { } due)
            x.Due = Next(x.Repeat, due);
        else
            x.DoneAt = DateTime.Now;
    });

    public static void Undo(Reminder r) => Change(r, x => x.DoneAt = null);

    public static void Snooze(Reminder r, TimeSpan by) => SnoozeUntil(r, DateTime.Now + by);

    // Moves just this time; a repeating reminder keeps its pattern and carries on from the new time
    public static void SnoozeUntil(Reminder r, DateTime when) => Change(r, x => x.Due = when);

    // Saves changes made to a reminder in the checklist
    public static void Update(Reminder r, string text, DateTime? due, string? repeat, bool high, string? note) => Change(r, x =>
    {
        (x.Text, x.Due, x.Repeat, x.High) = (text, due, repeat, high);
        x.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    });

    // Done in the last 30 days, newest first (today's also show ticked in Today)
    public static IEnumerable<Reminder> Completed() =>
        All.Where(r => r.DoneAt is { } done && done > DateTime.Now.AddDays(-30)).OrderByDescending(r => r.DoneAt);

    public static void ClearCompleted() => Change(all => all.RemoveAll(r => r.IsDone));

    public static IEnumerable<Reminder> DueNow() =>
        All.Where(r => !r.IsDone && r.Due is { } due && due <= DateTime.Now).OrderBy(r => r.Due);

    // Next occurrence strictly after `after`, keeping the time of day
    public static DateTime Next(string repeat, DateTime after)
    {
        var next = after;
        do
        {
            next = repeat switch
            {
                "daily" => next.AddDays(1),
                "weekdays" => next.AddDays(next.DayOfWeek == DayOfWeek.Friday ? 3 : next.DayOfWeek == DayOfWeek.Saturday ? 2 : 1),
                "biweekly" => next.AddDays(14),
                "monthly" => next.AddMonths(1),
                "monthend" => MonthEnd(next.AddDays(1)) + next.TimeOfDay,
                // Same day each month; a short month uses its last day, and the next month goes back to the 31st
                _ when repeat.StartsWith("monthday:") => DayOfMonth(next.AddMonths(1), int.Parse(repeat[9..])) + next.TimeOfDay,
                _ => next.AddDays(7),   // weekly:Day
            };
        } while (next <= DateTime.Now);   // skip dates missed while the PC was off
        return next;
    }

    public static DateTime MonthEnd(DateTime d) => new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    public static DateTime DayOfMonth(DateTime month, int day) =>
        new(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)));
}
