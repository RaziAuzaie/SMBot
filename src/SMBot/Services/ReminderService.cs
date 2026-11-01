using System.IO;
using System.Text.Json;

namespace SMBot.Services;

public class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public DateTime? Due { get; set; }            // null = anytime, no alert
    public string? Repeat { get; set; }           // daily, weekdays, weekly:Friday, monthly, monthend
    public bool High { get; set; }                // typed with "!"
    public DateTime? DoneAt { get; set; }         // set when ticked (repeating ones move to the next date instead)

    public bool IsDone => DoneAt is not null;

    public string When
    {
        get
        {
            var repeat = Repeat switch
            {
                null => "",
                "daily" => "every day",
                "weekdays" => "every weekday",
                "monthly" => "every month",
                "monthend" => "every month end",
                _ when Repeat.StartsWith("weekly:") => "every " + Repeat[7..],
                _ => Repeat,
            };
            if (Due is not { } due) return repeat.Length > 0 ? "↻ " + repeat : "anytime";

            var today = DateTime.Today;
            var day = due.Date == today ? "today" : due.Date == today.AddDays(1) ? "tomorrow"
                : due.Date < today.AddDays(7) && due.Date > today ? due.ToString("dddd") : due.ToString("d MMM");
            var text = $"{day} {due:h:mm tt}";
            return repeat.Length > 0 ? $"{text} ↻ {repeat}" : text;
        }
    }
}

// Reminders live in %LOCALAPPDATA%\SMBot\reminders.json
public static class ReminderService
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMBot", "reminders.json");
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static List<Reminder> All { get; private set; } = Load();

    public static event Action? Changed;

    static List<Reminder> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch { }
        return [];
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(All, Json));
        Changed?.Invoke();
    }

    public static void Add(IEnumerable<Reminder> reminders)
    {
        All.AddRange(reminders);
        Save();
    }

    public static void Delete(Reminder r)
    {
        All.Remove(r);
        Save();
    }

    // Repeating reminders move on to their next date; others are ticked off
    public static void Complete(Reminder r)
    {
        if (r.Repeat is not null && r.Due is { } due)
            r.Due = Next(r.Repeat, due);
        else
            r.DoneAt = DateTime.Now;
        Save();
    }

    public static void Undo(Reminder r)
    {
        r.DoneAt = null;
        Save();
    }

    public static void Snooze(Reminder r, TimeSpan by)
    {
        r.Due = DateTime.Now + by;
        Save();
    }

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
                "monthly" => next.AddMonths(1),
                "monthend" => MonthEnd(next.AddDays(1)) + next.TimeOfDay,
                _ => next.AddDays(7),   // weekly:Day
            };
        } while (next <= DateTime.Now);   // skip dates missed while the PC was off
        return next;
    }

    public static DateTime MonthEnd(DateTime d) => new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
}
