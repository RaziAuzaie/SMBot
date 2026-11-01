using System.Text.RegularExpressions;

namespace SMBot.Services;

// Local rule-based parser (no AI) for English and Malay, e.g.
// "remind me to call Ali tomorrow 3pm, email Lim and send invoice every Friday"
public static class ReminderParser
{
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static readonly Dictionary<string, DayOfWeek> Days = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monday"] = DayOfWeek.Monday, ["tuesday"] = DayOfWeek.Tuesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["friday"] = DayOfWeek.Friday, ["saturday"] = DayOfWeek.Saturday, ["sunday"] = DayOfWeek.Sunday,
        ["isnin"] = DayOfWeek.Monday, ["selasa"] = DayOfWeek.Tuesday, ["rabu"] = DayOfWeek.Wednesday,
        ["khamis"] = DayOfWeek.Thursday, ["jumaat"] = DayOfWeek.Friday, ["sabtu"] = DayOfWeek.Saturday, ["ahad"] = DayOfWeek.Sunday,
    };
    static readonly string Day = "(" + string.Join("|", Days.Keys) + ")";

    // English and Malay month names and short forms
    static readonly (string Pattern, int Month)[] MonthNames =
    [
        ("january|januari|jan", 1), ("february|februari|feb", 2), ("march|mac|mar", 3), ("april|apr", 4), ("may|mei", 5),
        ("june|jun", 6), ("july|julai|jul", 7), ("august|ogos|aug|ogo", 8), ("september|sept|sep", 9),
        ("october|oktober|okt|oct", 10), ("november|nov", 11), ("december|disember|dis|dec", 12),
    ];
    static readonly string Month = "(" + string.Join("|", MonthNames.Select(m => m.Pattern)) + ")";
    static int MonthNumber(string name) => MonthNames.First(m => Regex.IsMatch(name, $"^({m.Pattern})$", Opts)).Month;
    const string Ordinal = @"(?:st|nd|rd|th|hb| haribulan)?";

    static readonly Regex Prefix = new(@"^\s*(please\s+)?(remind me( to| that| about)?|tolong ingatkan( saya)?( untuk)?|ingatkan saya( untuk)?|reminder:?|todo:?)\s+", Opts);
    static readonly Regex Splitter = new(@"\s*(?:,|;|\n|\band then\b|\bthen\b|\band\b|\bdan\b|\blepas tu\b)\s*", Opts);

    // Word times, checked in order (more specific first)
    static readonly (string Pattern, TimeSpan Time)[] WordTimes =
    [
        (@"\b(after lunch|lepas lunch|lepas makan|selepas makan tengah ?hari)\b", new(14, 0, 0)),
        (@"\b(before lunch|sebelum lunch|sebelum makan tengah ?hari|noon|tengah ?hari)\b", new(12, 0, 0)),
        (@"\b(end of (the )?day|eod|before (i )?(go|leave) home|sebelum balik)\b", new(17, 0, 0)),
        (@"\b(this morning|morning|pagi( ini| ni)?)\b", new(9, 0, 0)),
        (@"\b(this afternoon|afternoon|petang( ini| ni)?)\b", new(15, 0, 0)),
        (@"\b(tonight|this evening|evening|malam( ini| ni)?)\b", new(20, 0, 0)),
    ];

    public static List<Reminder> Parse(string input, DateTime now)
    {
        var text = Prefix.Replace(input.Trim(), "");
        var items = Splitter.Split(text)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => ParseOne(p, now))
            .Where(p => p.Reminder.Text.Length > 0)
            .ToList();

        // "A, B and C tomorrow 3pm": items without a time share the time of the last item that has one
        var shared = items.LastOrDefault(i => i.HasTiming).Reminder;
        if (shared is not null)
            foreach (var (r, _) in items.Where(i => !i.HasTiming))
                (r.Due, r.Repeat) = (shared.Due, shared.Repeat);

        return items.Select(i => i.Reminder).ToList();
    }

    static (Reminder Reminder, bool HasTiming) ParseOne(string piece, DateTime now)
    {
        var s = " " + piece + " ";
        var high = s.Contains('!');
        s = s.Replace("!", " ");

        string? repeat = null;
        DateTime? date = null, exact = null;
        DayOfWeek? weekday = null;
        TimeSpan? time = null;
        Match m;

        // ---- Repeats ----
        int monthDay = 0;
        if (Take(ref s, @"\b(every weekday|weekdays|setiap hari bekerja)\b").Success) repeat = "weekdays";
        else if (Take(ref s, @"\b(every ?day|daily|setiap hari|tiap hari|hari-hari)\b").Success) repeat = "daily";
        else if (Take(ref s, @"\b((every|each) (2|two|other) weeks?|fortnightly|bi-?weekly|(setiap|tiap) (2|dua) minggu)\b").Success) repeat = "biweekly";
        else if (Take(ref s, @"\b((every|each|setiap|tiap) (month ?end|end of (the )?month|hujung bulan)|end of every month)\b").Success) repeat = "monthend";
        // A day of the month: "every month on the 15th", "monthly on 15", "every 15th", "the 15th of every month", "setiap 15 haribulan"
        else if ((m = Take(ref s, $@"\b(?:(?:every|each) month|monthly|(?:setiap|tiap) bulan)(?: on| pada)?(?: the)? (\d{{1,2}}){Ordinal}(?![\d:./])")).Success
              || (m = Take(ref s, @"\b(?:every|each|setiap|tiap) (\d{1,2})(?:st|nd|rd|th|hb| haribulan)\b")).Success
              || (m = Take(ref s, @"\b(?:on )?the (\d{1,2})(?:st|nd|rd|th) of (?:every|each) month\b")).Success)
        {
            monthDay = Math.Clamp(int.Parse(m.Groups[1].Value), 1, 31);
            repeat = "monthday:" + monthDay;
        }
        else if (Take(ref s, @"\b((every|each|setiap|tiap) (month|bulan)|monthly)\b").Success) repeat = "monthly";
        else if ((m = Take(ref s, $@"\b(every|each|setiap|tiap) (hari )?{Day}\b")).Success)
        {
            weekday = Days[m.Groups[3].Value];
            repeat = "weekly:" + weekday;
        }
        else if (Take(ref s, @"\bweekly\b").Success)
        {
            weekday = now.DayOfWeek;
            repeat = "weekly:" + weekday;
        }

        // ---- Relative ("in 30 minutes", "dalam 2 jam") ----
        if (Take(ref s, @"\b(in )?(half an hour|setengah jam)\b").Success) exact = now.AddMinutes(30);
        else if ((m = Take(ref s, @"\b(in|dalam)\s+(\d+|an?|one|satu)\s*(minutes?|mins?|minit|hours?|hrs?|jam|days?|hari)\b")).Success)
        {
            var n = int.TryParse(m.Groups[2].Value, out var v) ? v : 1;
            var unit = m.Groups[3].Value.ToLowerInvariant();
            exact = unit.StartsWith("min") ? now.AddMinutes(n) : unit.StartsWith('h') && unit != "hari" || unit == "jam" ? now.AddHours(n) : now.AddDays(n);
        }

        // ---- Times (before dates, so "3.30pm" isn't read as a date) ----
        if ((m = Take(ref s, @"\b(?:at |pukul |by )?(\d{1,2})(?:[:.](\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)(?!\w)")).Success)
        {
            var h = int.Parse(m.Groups[1].Value) % 12;
            if (m.Groups[3].Value.StartsWith('p')) h += 12;
            time = new TimeSpan(h, m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0, 0);
        }
        else if ((m = Take(ref s, @"\b(?:at |pukul |by )?([01]?\d|2[0-3])[:.]([0-5]\d)\b")).Success)
            time = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
        else if ((m = Take(ref s, @"\b(?:at|pukul)\s+(\d{1,2})\b")).Success)
        {
            var h = int.Parse(m.Groups[1].Value);
            // "pukul 3 petang", or a bare small hour during work: assume afternoon
            if (h < 12 && (Regex.IsMatch(s, @"\b(petang|malam|pm)\b", Opts) || h < 7)) h += 12;
            time = new TimeSpan(h % 24, 0, 0);
        }
        foreach (var (pattern, wordTime) in WordTimes)
            if (Take(ref s, pattern).Success && time is null) time = wordTime;

        // ---- Days ----
        if (Take(ref s, @"\b(day after tomorrow|lusa)\b").Success) date = now.Date.AddDays(2);
        else if (Take(ref s, @"\b(tomorrow|tmrw?|esok|besok)\b").Success) date = now.Date.AddDays(1);
        else if (Take(ref s, @"\b(today|hari ini|hari ni|harini)\b").Success) date = now.Date;
        else if (Take(ref s, @"\b(next week|minggu depan)\b").Success) date = NextWeekday(DayOfWeek.Monday, now.Date.AddDays(1));
        else if (Take(ref s, @"\b(next month|bulan depan)\b").Success) date = new DateTime(now.Year, now.Month, 1).AddMonths(1);
        else if ((m = Take(ref s, $@"\b(on |pada )?(next )?(hari )?{Day}( depan)?\b")).Success)
            weekday = Days[m.Groups[4].Value];
        else if ((m = Take(ref s, @"\b(\d{1,2})[/-](\d{1,2})(?:[/-](\d{2,4}))?\b")).Success)
        {
            var year = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : now.Year;
            if (year < 100) year += 2000;
            try { date = new DateTime(year, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value)); } catch { }
            if (date < now.Date && !m.Groups[3].Success) date = date.Value.AddYears(1);
        }
        // Month names: "15 Disember", "15th of December 2026", "Dec 15", "December 15, 2026"
        else if ((m = Take(ref s, $@"\b(\d{{1,2}}){Ordinal}\s+(?:of\s+)?{Month}(?:,?\s+(\d{{4}}))?\b")).Success)
            date = MonthDate(int.Parse(m.Groups[1].Value), MonthNumber(m.Groups[2].Value), m.Groups[3], now);
        else if ((m = Take(ref s, $@"\b{Month}\s+(\d{{1,2}})(?:st|nd|rd|th)?(?:,?\s+(\d{{4}}))?\b")).Success)
            date = MonthDate(int.Parse(m.Groups[2].Value), MonthNumber(m.Groups[1].Value), m.Groups[3], now);

        if (repeat == "monthend" && date is null) date = ReminderService.MonthEnd(now);
        if (monthDay > 0 && date is null) date = ReminderService.DayOfMonth(now, monthDay);

        // ---- Combine ----
        DateTime? due = exact;
        var hasTiming = exact is not null || date is not null || weekday is not null || time is not null || repeat is not null;
        if (due is null && hasTiming)
        {
            var day = date ?? (weekday is { } wd ? NextWeekday(wd, now.Date) : now.Date);
            due = day + (time ?? new TimeSpan(9, 0, 0));
            if (repeat == "weekdays" && due.Value.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                due = NextWeekday(DayOfWeek.Monday, due.Value.Date) + due.Value.TimeOfDay;
            if (due <= now)
            {
                if (repeat is not null) due = ReminderService.Next(repeat, due.Value);
                else if (weekday is not null) due = due.Value.AddDays(7);
                else if (date is null) due = due.Value.AddDays(1);                       // a time that has passed today means tomorrow
                else if (time is null && date == now.Date) due = now.AddHours(1);         // just "today"
            }
        }

        var reminder = new Reminder { Text = Tidy(s), Due = due, Repeat = repeat, High = high };
        return (reminder, hasTiming);
    }

    // Reads just a "when", for editing a reminder or picking a snooze time: "tomorrow 3pm", "every Friday 9am", "15 Dis".
    // Leftover is whatever wasn't understood (empty when it all made sense).
    public static (DateTime? Due, string? Repeat, string Leftover) ParseWhen(string when, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(when)) return (null, null, "");
        var (r, _) = ParseOne(when, now);
        return (r.Due, r.Repeat, r.Text);
    }

    // A date with no year means the next time that date comes round
    static DateTime? MonthDate(int day, int month, Group year, DateTime now)
    {
        try
        {
            var date = new DateTime(year.Success ? int.Parse(year.Value) : now.Year, month, day);
            return date < now.Date && !year.Success ? date.AddYears(1) : date;
        }
        catch (ArgumentOutOfRangeException) { return null; }   // e.g. 31 Feb
    }

    // Removes the first match from s and returns it
    static Match Take(ref string s, string pattern)
    {
        var m = Regex.Match(s, pattern, Opts);
        if (m.Success) s = s.Remove(m.Index, m.Length).Insert(m.Index, " ");
        return m;
    }

    static DateTime NextWeekday(DayOfWeek target, DateTime from) =>
        from.AddDays(((int)target - (int)from.DayOfWeek + 7) % 7);

    static string Tidy(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        // Leftover joining words, e.g. "call Ali at" after "3pm" was taken out
        s = Regex.Replace(s, @"^(to|untuk)\s+", "", Opts);
        s = Regex.Replace(s, @"(\s+(at|on|by|pada|pukul|nanti|please|tolong))+$", "", Opts);
        s = s.Trim(' ', '.', ',', '-');
        return s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s;
    }
}
