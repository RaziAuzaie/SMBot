using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMBot.Services;

namespace SMBot.Views;

public enum Mood { Neutral, Happy, Curious, Surprised, Sleepy }

public partial class BuddyWindow : Window
{
    Settings _settings = SettingsService.Load();
    Mood _mood = Mood.Neutral;

    // 3D frames: _face is the resting frame for the current mood; a running wave or spin
    // shows its own frames and returns to _face when it ends
    static readonly Dictionary<string, BitmapImage> Frames = [];
    string _face = "neutral";
    int _animationId;
    bool _animating;
    readonly DispatcherTimer _blinkTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    // Dozes off after a minute without interaction
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    readonly DispatcherTimer _emailTimer = new();
    List<PendingEmail> _unread = [], _unreplied = [];
    bool _checking, _checkedOnce, _outlookWarned;

    // Feature 3: reminders
    readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    // Status peek: every 10 minutes while the bubble is closed, or after hovering over the bot
    readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    int _peekId;
    readonly Dictionary<Guid, DateTime> _lastAlert = [];   // when each due reminder last popped up
    Reminder? _dueReminder;
    List<Reminder> _parsed = [];
    ChecklistWindow? _checklist;

    // Typing in the bubble, or a reminder waiting for an answer: don't cover it with something else
    bool IsBusy => NamePanel.Visibility == Visibility.Visible || CapturePanel.Visibility == Visibility.Visible
                   || ConfirmPanel.Visibility == Visibility.Visible || DuePanel.Visibility == Visibility.Visible
                   || MissedPanel.Visibility == Visibility.Visible;

    public BuddyWindow()
    {
        InitializeComponent();
        _reminderTimer.Tick += (_, _) => { CheckDueReminders(); UpdateBadge(); };
        _peekTimer.Tick += (_, _) => PeekStatus(auto: true);
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); PeekStatus(auto: false); };
        SourceInitialized += (_, _) => RegisterCaptureHotkey();
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (NamePanel.Visibility != Visibility.Visible) SetMood(Mood.Sleepy);
        };
        _emailTimer.Tick += async (_, _) => await CheckEmailsAsync(userAsked: false);
        _blinkTimer.Tick += async (_, _) => await Blink();
        ShowFrame("neutral");
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySize(_settings.BotSize, keepAnchor: false);
        ApplyHeadOnly(_settings.HeadOnly);
        PlaceWindow();
        UpdateBadge();
        ReminderService.Changed += UpdateBadge;

        Play("Hover");
        _blinkTimer.Start();
        _ = FileSearchService.BuildAsync(FileSearchService.DefaultFolders());   // file search list, built in the background
        StartMusic();

        if (string.IsNullOrWhiteSpace(_settings.UserName))
            StartOnboarding();
        else
        {
            Greet();
            StartEmailChecks();
            _reminderTimer.Start();
            _peekTimer.Start();
            // "SMBot.exe --checklist" also opens the checklist straight away (e.g. from a pinned shortcut)
            if (Environment.GetCommandLineArgs().Contains("--checklist")) Checklist_Click(this, e);
        }
    }

    // ---- First-run onboarding ----

    void StartOnboarding()
    {
        Wave();
        SetMood(Mood.Curious);
        Say("Hi there! I'm SMBot, your personal assistant. What is your name?");
        NameBox.Text = SettingsService.SuggestName();
        NameHint.Visibility = Visibility.Collapsed;
        NamePanel.Visibility = Visibility.Visible;
        Activate();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    void OkButton_Click(object sender, RoutedEventArgs e) => SubmitName();

    void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SubmitName();
    }

    async void SubmitName()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameHint.Visibility = Visibility.Visible;
            SetMood(Mood.Surprised);
            await Task.Delay(900);
            if (_mood == Mood.Surprised) SetMood(Mood.Curious);
            return;
        }

        _settings.UserName = name.Length > 30 ? name[..30] : name;
        SettingsService.Save(_settings);

        NamePanel.Visibility = Visibility.Collapsed;
        Say($"Nice to meet you, {_settings.UserName}! I'll keep an eye on your emails and meetings.");
        Celebrate();
        Sparkle();
        HappyMoment();
        StartEmailChecks();
        _reminderTimer.Start();
        _peekTimer.Start();
    }

    // ---- Feature 1: unread and unreplied emails ----

    async void StartEmailChecks()
    {
        _emailTimer.Interval = TimeSpan.FromSeconds(Math.Max(15, _settings.EmailCheckSeconds));
        _emailTimer.Start();
        await Task.Delay(TimeSpan.FromSeconds(6));   // let the greeting finish first
        await CheckEmailsAsync(userAsked: false);
    }

    async Task CheckEmailsAsync(bool userAsked)
    {
        if (_checking) return;
        _checking = true;
        if (userAsked) Say("Checking your emails...");
        try
        {
            var found = await OutlookComMailService.CheckAsync(_settings.DismissedEmails);
            var newUnread = found.Unread.Where(e => _unread.All(p => p.EntryId != e.EntryId)).ToList();
            var newUnreplied = found.Unreplied.Any(e => _unreplied.All(p => p.EntryId != e.EntryId));
            _unread = found.Unread;
            _unreplied = found.Unreplied;
            var firstCheck = !_checkedOnce;
            _checkedOnce = true;
            _outlookWarned = false;
            _emailTimer.Start();   // back on after a pause, once a check works again

            if (IsBusy) return;
            if (userAsked || firstCheck)
                ShowEmails(unreadTab: _unread.Count > 0 || _unreplied.Count == 0);
            // After that, only pop up on its own when something new arrived
            else if (newUnread.Count > 0)
            {
                ShowEmails(unreadTab: true, alert: true);
                BubbleText.Text = newUnread.Count == 1
                    ? $"{_settings.UserName}, new email from {newUnread[0].Sender}!"
                    : $"{_settings.UserName}, {newUnread.Count} new emails!";
            }
            else if (newUnreplied)
                ShowEmails(unreadTab: false, alert: true);
            // Nothing new: keep an open list up to date, e.g. after reading an email in New Outlook
            else if (Bubble.Visibility == Visibility.Visible && EmailTabs.Visibility == Visibility.Visible)
                ShowEmails(UnreadTab.IsChecked == true, quiet: true);
        }
        catch (Exception ex)
        {
            // Stop trying on a timer (each try can open Outlook); "Check emails" tries again
            _emailTimer.Stop();
            var reason = ex is OutlookNotReadyException ? ex.Message : $"Something went wrong reading Outlook: {ex.Message}";
            if (userAsked || (!_outlookWarned && !IsBusy))
                Say($"{_settings.UserName}, I can't read your emails, so I've stopped checking. {reason} " +
                    "Click me and choose \"Check emails\" when Outlook is ready.");
            _outlookWarned = true;
        }
        finally
        {
            _checking = false;
        }
    }

    // quiet: refresh the list without changing the bot's mood
    void ShowEmails(bool unreadTab, bool alert = false, bool quiet = false)
    {
        var list = unreadTab ? _unread : _unreplied;
        var count = list.Count == 1 ? "1 email" : $"{list.Count} emails";
        Say((unreadTab, list.Count) switch
        {
            (true, 0) => $"No unread emails, {_settings.UserName}. All caught up!",
            (true, _) => $"{_settings.UserName}, you have {count} unread.",
            (false, 0) => $"Inbox clear, {_settings.UserName}! No emails waiting for your reply.",
            (false, 1) => $"{_settings.UserName}, 1 email is waiting for your reply.",
            _ => $"{_settings.UserName}, {count} are waiting for your reply.",
        });

        UnreadTab.Content = $"Unread ({_unread.Count})";
        UnreadTab.IsChecked = unreadTab;
        UnrepliedTab.Content = $"Unreplied ({_unreplied.Count})";
        UnrepliedTab.IsChecked = !unreadTab;
        EmailTabs.Visibility = Visibility.Visible;

        if (list.Count == 0)
        {
            if (!quiet) HappyMoment();
            return;
        }

        EmailList.ItemTemplate = (DataTemplate)FindResource(unreadTab ? "UnreadTemplate" : "UnrepliedTemplate");
        EmailList.ItemsSource = list.Take(3).ToList();
        EmailList.Visibility = Visibility.Visible;
        if (list.Count > 3)
        {
            MoreEmails.Text = $"+ {list.Count - 3} more";
            MoreEmails.Visibility = Visibility.Visible;
        }
        if (alert) AlertMoment();
    }

    void UnreadTab_Click(object sender, RoutedEventArgs e) => ShowEmails(unreadTab: true);

    void UnrepliedTab_Click(object sender, RoutedEventArgs e) => ShowEmails(unreadTab: false);

    void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        var email = (PendingEmail)((FrameworkElement)sender).DataContext;
        _settings.DismissedEmails.Add(email.EntryId);
        SettingsService.Save(_settings);
        _unreplied.Remove(email);
        ShowEmails(unreadTab: false);
    }

    async void CheckEmails_Click(object sender, RoutedEventArgs e) => await CheckEmailsAsync(userAsked: true);

    // ---- Feature 3: reminders and checklist ----

    void StartCapture(string text = "")
    {
        if (NamePanel.Visibility == Visibility.Visible) return;
        Say($"What should I remind you about, {_settings.UserName}?");
        SetMood(Mood.Curious);
        CaptureBox.Text = text;
        CapturePanel.Visibility = Visibility.Visible;
        Activate();
        CaptureBox.Focus();
        CaptureBox.CaretIndex = text.Length;
    }

    void CaptureBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ParseCapture();
        else if (e.Key == Key.Escape) CloseCapture();
    }

    void CaptureAdd_Click(object sender, RoutedEventArgs e) => ParseCapture();

    void CaptureCancel_Click(object sender, RoutedEventArgs e) => CloseCapture();

    void ParseCapture()
    {
        _parsed = ReminderParser.Parse(CaptureBox.Text, DateTime.Now);
        if (_parsed.Count == 0)
        {
            AlertMoment();
            return;
        }
        // Confirmation step: show what was understood before saving
        var text = CaptureBox.Text;
        Say(_parsed.Count == 1 ? "Got it. Is this right?" : $"I found {_parsed.Count} reminders. Is this right?");
        CaptureBox.Text = text;
        ConfirmList.ItemsSource = _parsed;
        ConfirmPanel.Visibility = Visibility.Visible;
        SetMood(Mood.Neutral);
    }

    void ConfirmEdit_Click(object sender, RoutedEventArgs e) => StartCapture(CaptureBox.Text);

    void ConfirmSave_Click(object sender, RoutedEventArgs e)
    {
        ReminderService.Add(_parsed);
        var next = _parsed.Where(r => r.Due is not null).MinBy(r => r.Due);
        Say(next is null
            ? $"Saved to your checklist, {_settings.UserName}!"
            : $"Saved! I'll remind you {next.When.Split('↻')[0].Trim()}.");
        Sparkle();
        HappyMoment();
    }

    void CloseCapture() => HideBubble();

    // ---- Status peek ----

    void Character_MouseEnter(object sender, MouseEventArgs e) => _hoverTimer.Start();

    void Character_MouseLeave(object sender, MouseEventArgs e) => _hoverTimer.Stop();

    // Shows how many reminders (with a time) and checklist items (no time) are open, and the next one due,
    // for 5 seconds. Only when the bubble is closed; the 10-minute peek stays quiet when nothing is open.
    async void PeekStatus(bool auto)
    {
        if (Bubble.Visibility == Visibility.Visible || IsBusy) return;
        var text = StatusText(out var anyOpen);
        if (auto && !anyOpen) return;

        var id = ++_peekId;
        Say(text);
        await Task.Delay(TimeSpan.FromSeconds(5));
        // Leave it if something else took over the bubble in the meantime
        if (id == _peekId && BubbleText.Text == text && !IsBusy && ActionPanel.Visibility != Visibility.Visible)
            HideBubble();
    }

    string StatusText(out bool anyOpen)
    {
        var open = ReminderService.All.Where(r => !r.IsDone).ToList();
        anyOpen = open.Count > 0;
        if (!anyOpen) return $"All clear, {_settings.UserName}! No open reminders or checklist items.";

        var reminders = open.Count(r => r.Due is not null);
        var checklist = open.Count - reminders;
        var overdue = open.Count(r => r.Due < DateTime.Now);
        static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

        var parts = new List<string>();
        if (reminders > 0) parts.Add(Plural(reminders, "reminder"));
        if (checklist > 0) parts.Add(Plural(checklist, "checklist item"));
        var text = $"{_settings.UserName}, you have {string.Join(" and ", parts)} open";
        text += overdue > 0 ? $" ({overdue} overdue)." : ".";

        // The next one on its own lines; non-breaking spaces keep "11 Nov · 9:00 AM" on one line
        var next = open.Where(r => r.Due >= DateTime.Now).MinBy(r => r.Due);
        if (next?.Due is { } due)
        {
            var today = DateTime.Today;
            var day = due.Date == today ? "Today" : due.Date == today.AddDays(1) ? "Tomorrow"
                : due.Date < today.AddDays(7) ? due.ToString("dddd") : due.ToString("d MMM");
            var when = $"{day} · {due:h:mm tt}".Replace(' ', ' ');
            text += $"\nNext: {next.Text}\n{when}";
        }
        return text;
    }

    void CheckDueReminders()
    {
        if (IsBusy) return;
        // A reminder that was ignored pops up again after 10 minutes
        var pending = ReminderService.DueNow()
            .Where(r => !_lastAlert.TryGetValue(r.Id, out var last) || DateTime.Now - last > TimeSpan.FromMinutes(10)).ToList();
        if (pending.Count == 0) return;

        // Several that went off a while ago (PC was off, or away): one list instead of a pop-up each
        var missed = pending.Where(r => DateTime.Now - r.Due > TimeSpan.FromMinutes(10)).ToList();
        if (missed.Count >= 2)
        {
            ShowMissed(missed);
            return;
        }

        var due = pending[0];
        _lastAlert[due.Id] = DateTime.Now;
        _dueReminder = due;
        var text = due.Text.Length > 1 && char.IsLower(due.Text[1]) ? char.ToLower(due.Text[0]) + due.Text[1..] : due.Text;
        Say($"{_settings.UserName}, time to {text}!");
        DuePanel.Visibility = Visibility.Visible;
        Wave();
        AlertMoment();
    }

    // ---- Missed reminders ----

    List<Reminder> _missed = [];

    void ShowMissed(List<Reminder> missed)
    {
        _missed = missed;
        foreach (var r in missed) _lastAlert[r.Id] = DateTime.Now;
        Say($"{_settings.UserName}, you missed {missed.Count} reminders while you were away:");
        RefreshMissed();
        MissedPanel.Visibility = Visibility.Visible;
        AlertMoment();
    }

    void RefreshMissed()
    {
        if (_missed.Count == 0)
        {
            Say($"All caught up, {_settings.UserName}! ✓");
            Sparkle();
            HappyMoment();
            return;
        }
        MissedList.ItemsSource = _missed.Take(4).ToList();
        MissedMore.Text = $"+ {_missed.Count - 4} more in your checklist";
        MissedMore.Visibility = _missed.Count > 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    void MissedDone_Click(object sender, RoutedEventArgs e)
    {
        var r = (Reminder)((FrameworkElement)sender).DataContext;
        ReminderService.Complete(r);
        _lastAlert.Remove(r.Id);
        _missed.Remove(r);
        RefreshMissed();
    }

    void MissedAllDone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _missed) { ReminderService.Complete(r); _lastAlert.Remove(r.Id); }
        _missed.Clear();
        RefreshMissed();
    }

    void MissedSnoozeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _missed) { ReminderService.Snooze(r, TimeSpan.FromHours(1)); _lastAlert.Remove(r.Id); }
        Say($"Okay, I'll remind you about them again at {DateTime.Now.AddHours(1):h:mm tt}.");
        _missed.Clear();
        SetMood(Mood.Neutral);
    }

    void MissedChecklist_Click(object sender, RoutedEventArgs e)
    {
        HideBubble();
        OpenChecklist();
    }

    void ReminderDone_Click(object sender, RoutedEventArgs e)
    {
        if (_dueReminder is null) return;
        ReminderService.Complete(_dueReminder);
        _lastAlert.Remove(_dueReminder.Id);
        _dueReminder = null;
        Say($"Nice work, {_settings.UserName}! ✓");
        Sparkle();
        HappyMoment();
    }

    void Snooze10_Click(object sender, RoutedEventArgs e) => SnoozeTo(DateTime.Now.AddMinutes(10));

    void Snooze60_Click(object sender, RoutedEventArgs e) => SnoozeTo(DateTime.Now.AddHours(1));

    void SnoozeMore_Click(object sender, RoutedEventArgs e)
    {
        SnoozeMorePanel.Visibility = SnoozeMorePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        SnoozeError.Visibility = Visibility.Collapsed;
        SnoozeBox.Text = "";
        if (SnoozeMorePanel.Visibility == Visibility.Visible) { Activate(); SnoozeBox.Focus(); }
    }

    // 2 PM today, or tomorrow if lunch is already over
    void SnoozeLunch_Click(object sender, RoutedEventArgs e) =>
        SnoozeTo(DateTime.Today.AddHours(DateTime.Now.TimeOfDay < new TimeSpan(13, 30, 0) ? 14 : 24 + 14));

    void SnoozeMorning_Click(object sender, RoutedEventArgs e) => SnoozeTo(DateTime.Today.AddDays(1).AddHours(9));

    void SnoozeCustom_Click(object sender, RoutedEventArgs e) => SnoozeCustom();

    void SnoozeBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SnoozeHint.Visibility = SnoozeBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void SnoozeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SnoozeCustom();
    }

    // Any time the reminder parser understands: "4pm", "tomorrow 10am", "Friday 9am", "15 Dis 2pm"
    void SnoozeCustom()
    {
        var (due, _, leftover) = ReminderParser.ParseWhen(SnoozeBox.Text, DateTime.Now);
        if (due is not { } when || leftover.Length > 0 || when <= DateTime.Now)
        {
            SnoozeError.Text = "I didn't get that time. Try \"4pm\" or \"tomorrow 10am\".";
            SnoozeError.Visibility = Visibility.Visible;
            return;
        }
        SnoozeTo(when);
    }

    void SnoozeTo(DateTime when)
    {
        if (_dueReminder is null) return;
        ReminderService.SnoozeUntil(_dueReminder, when);
        _lastAlert.Remove(_dueReminder.Id);
        var day = when.Date == DateTime.Today ? "" : when.Date == DateTime.Today.AddDays(1) ? "tomorrow " : $"on {when:ddd d MMM} ";
        Say($"Okay, I'll remind you again {day}at {when:h:mm tt}.");
        _dueReminder = null;
        SetMood(Mood.Neutral);
    }

    // ---- Recent files ----

    void RecentFiles_Click(object sender, RoutedEventArgs e)
    {
        var files = RecentFilesService.GetRecent();
        if (files.Count == 0)
        {
            Say($"I can't find any recently opened files, {_settings.UserName}.");
            return;
        }
        Say("Your recent files. Click one to open its folder:");
        RecentList.ItemsSource = files;
        RecentList.Visibility = Visibility.Visible;
        SetMood(Mood.Curious);
    }

    void RecentFile_Click(object sender, RoutedEventArgs e)
    {
        var file = (RecentFile)((FrameworkElement)sender).DataContext;
        try
        {
            RecentFilesService.ShowInFolder(file);
            HideBubble();
        }
        catch (Exception ex)
        {
            Say($"Sorry, I couldn't open that folder: {ex.Message}");
        }
    }

    void Checklist_Click(object sender, RoutedEventArgs e)
    {
        if (_checklist is null)
        {
            _checklist = new ChecklistWindow();
            _checklist.Closed += (_, _) => _checklist = null;
            _checklist.Show();
        }
        _checklist.Activate();
    }

    // Hotkeys from anywhere: Ctrl + Alt + R quick capture (Win + Shift + R belongs to the Snipping Tool recorder on Windows 11),
    // Ctrl + Alt + F file search
    const int HotkeyId = 1, SearchHotkeyId = 2, MusicHotkeyId = 3, WmHotkey = 0x0312;

    void RegisterCaptureHotkey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle).AddHook((IntPtr _, int msg, IntPtr wParam, IntPtr _, ref bool handled) =>
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                StartCapture();
                handled = true;
            }
            else if (msg == WmHotkey && wParam.ToInt32() == SearchHotkeyId)
            {
                OpenSearch();
                handled = true;
            }
            else if (msg == WmHotkey && wParam.ToInt32() == MusicHotkeyId)
            {
                _ = MusicService.PlayPauseAsync();
                handled = true;
            }
            return IntPtr.Zero;
        });
        const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, KeyR = 0x52, KeyF = 0x46;
        // If another app owns one, the action list still works
        RegisterHotKey(handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, KeyR);
        RegisterHotKey(handle, SearchHotkeyId, ModControl | ModAlt | ModNoRepeat, KeyF);
        RegisterHotKey(handle, MusicHotkeyId, ModControl | ModAlt | ModNoRepeat, 0x20 /* Space: play / pause */);
    }

    // ---- Music: what's playing, controls, now-playing peek, dancing ----

    readonly DispatcherTimer _musicTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _danceTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };   // ~40 fps, only while dancing
    DateTime _musicStartedAt;
    const string NowPlayingText = "🎵 Now playing";

    async void StartMusic()
    {
        await MusicService.StartAsync();
        _musicStartedAt = DateTime.Now;
        MusicService.TrackChanged += OnTrackChanged;
        MusicService.Changed += OnMusicChanged;
        _musicTimer.Tick += async (_, _) => await MusicService.RefreshAsync();
        _danceTimer.Tick += (_, _) => DanceStep();
        _musicTimer.Start();
    }

    void OnMusicChanged()
    {
        if (MusicCard.Visibility == Visibility.Visible) UpdateMusicCard();
        if (MusicService.Current?.IsPlaying == true && !_danceTimer.IsEnabled) _danceTimer.Start();
    }

    void UpdateMusicCard()
    {
        if (MusicService.Current is not { } now)
        {
            MusicCard.Visibility = Visibility.Collapsed;
            return;
        }
        MusicTitle.Text = now.Title;
        MusicSubtitle.Text = now.Subtitle;
        MusicCover.Source = now.Cover;
        MusicPlayButton.Tag = now.IsPlaying ? "" : "";   // pause or play glyph
        MusicCard.Visibility = Visibility.Visible;
    }

    // A new song: show it for a few seconds, if the bubble is free. Not for whatever was already playing when SMBot started.
    async void OnTrackChanged(NowPlaying now)
    {
        if (!now.IsPlaying || DateTime.Now - _musicStartedAt < TimeSpan.FromSeconds(5)) return;
        var showingPeek = Bubble.Visibility == Visibility.Visible && BubbleText.Text == NowPlayingText;
        if (IsBusy || (Bubble.Visibility == Visibility.Visible && !showingPeek)) return;

        var id = ++_peekId;
        Say(NowPlayingText);
        UpdateMusicCard();
        await Task.Delay(TimeSpan.FromSeconds(4.5));
        if (id == _peekId && BubbleText.Text == NowPlayingText && ActionPanel.Visibility != Visibility.Visible && !IsBusy)
            HideBubble();
    }

    async void MusicPlayPause_Click(object sender, RoutedEventArgs e) { await MusicService.PlayPauseAsync(); await MusicService.RefreshAsync(); }
    async void MusicNext_Click(object sender, RoutedEventArgs e) { await MusicService.NextAsync(); await MusicService.RefreshAsync(); }
    async void MusicPrevious_Click(object sender, RoutedEventArgs e) { await MusicService.PreviousAsync(); await MusicService.RefreshAsync(); }

    // ---- Head-bob dance (Spotify only) ----
    // While Spotify plays, the bot switches to separate head and body layers: the head nods down on each beat and
    // springs back, with a slight side-to-side sway that alternates each beat; the body grooves a little underneath.
    // Springs (not keyframes) keep it smooth at about 40 frames a second, which also keeps the CPU cost low.
    // Beats: loud, mastered songs barely change level, so instead of single jumps it looks for the repeating
    // pattern in Spotify's level over the last 6 seconds (autocorrelation of the rises), favouring natural
    // head-bob tempos, and keeps time from that. With no clear beat (speech, ads) it just bobs gently.

    const double SampleRate = 50;                       // level history is kept on a fixed 50 Hz grid
    readonly System.Diagnostics.Stopwatch _danceClock = System.Diagnostics.Stopwatch.StartNew();
    readonly Queue<double> _levels = new();              // last 6 s of Spotify's level, 50 per second
    double _lastFrame, _nextSample, _lastTempoCheck, _quietSince = -1;
    double _beatPeriod = 0.55, _pendingPeriod, _lastBeat, _confidence;
    float _loudness;
    int _beatCount;
    bool _dancing, _layersOn;

    // Springs: position, velocity
    double _headY, _headYv, _sway, _swayV, _swayTarget, _bodyY, _bodyYv;

    void DanceStep()
    {
        var t = _danceClock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(t - _lastFrame, 0.001, 0.05);
        _lastFrame = t;

        var playing = MusicService.Current?.IsPlaying == true;
        var level = playing ? MusicService.Level() : 0f;
        _loudness = _loudness * 0.9f + level * 0.1f;
        while (_nextSample <= t)   // fill the fixed-rate history
        {
            _levels.Enqueue(level);
            if (_levels.Count > SampleRate * 6) _levels.Dequeue();
            _nextSample = Math.Max(_nextSample + 1 / SampleRate, t - 1);
        }

        // Dance while Spotify plays and can be heard (not in the silence between songs)
        if (playing && _loudness > 0.02f) _quietSince = -1;
        else if (_quietSince < 0) _quietSince = t;
        var wasDancing = _dancing;
        _dancing = _quietSince < 0 || t - _quietSince < 0.8;
        if (_dancing && !wasDancing) _lastBeat = t;

        if (_dancing)
        {
            if (t - _lastTempoCheck > 1) { _lastTempoCheck = t; FindTempo(t); }
            if (t - _lastBeat >= _beatPeriod) OnBeat(t);
        }

        // Springs: head (stiff, quick bounce), sway (soft glide), body (small dip)
        Spring(ref _headY, ref _headYv, 0, 260, 22, dt);
        Spring(ref _sway, ref _swayV, _dancing ? _swayTarget : 0, 50, 10, dt);
        Spring(ref _bodyY, ref _bodyYv, 0, 300, 30, dt);

        // Show the layers while dancing (a wave or celebrate spin playing on the normal frames takes priority)
        var layers = (_dancing || Math.Abs(_headY) + Math.Abs(_sway) + Math.Abs(_bodyY) > 0.05) && !_animating;
        if (layers != _layersOn) SetDanceLayers(layers);
        HeadBob.Y = _headY;
        HeadSway.Angle = _sway;
        DanceBob.Y = _bodyY;
        DanceTilt.Angle = _sway * 0.3;                    // the body follows the head's sway a little
        DanceScale.ScaleY = 1 - _bodyY * 0.006;           // and squashes slightly as it dips
        DanceScale.ScaleX = 1 + _bodyY * 0.003;

        if (!_dancing && !layers && Math.Abs(_headYv) + Math.Abs(_swayV) + Math.Abs(_bodyYv) < 0.05)
        {
            _danceTimer.Stop();
            (HeadBob.Y, HeadSway.Angle, DanceBob.Y, DanceTilt.Angle, DanceScale.ScaleX, DanceScale.ScaleY) = (0, 0, 0, 0, 1, 1);
        }
    }

    void OnBeat(double t)
    {
        _lastBeat = t;
        _beatCount++;
        // Stronger nods for louder music and a clearer beat; a gentle bob when unsure
        var strength = Math.Clamp(0.55 + _loudness * 1.2, 0.55, 1.1) * (_confidence > 1.25 ? 1 : 0.6);
        _headYv += 210 * strength;                        // nod down
        _bodyYv += 55 * strength;                         // small dip
        if (_beatCount % 2 == 0) _swayTarget = _swayTarget > 0 ? -4 : 4;   // sway to the other side every 2 beats
    }

    // Beat period from the last 6 s: autocorrelation of the level's rises over 0.33-1 s (60-180 BPM),
    // weighted towards head-bob tempos (~110 BPM); then line the beat clock up with the strongest pulses
    void FindTempo(double t)
    {
        if (_levels.Count < SampleRate * 3) return;
        var x = _levels.ToArray();
        var flux = new double[x.Length];
        for (var i = 1; i < x.Length; i++) flux[i] = Math.Max(0, x[i] - x[i - 1]);

        int minLag = (int)(SampleRate * 0.33), maxLag = (int)(SampleRate * 1.0), bestLag = 0;
        double best = 0, total = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double s = 0;
            for (var i = lag; i < flux.Length; i++) s += flux[i] * flux[i - lag];
            s /= flux.Length - lag;
            total += s;
            var prefer = Math.Exp(-Math.Pow(Math.Log(lag / SampleRate / 0.55, 2), 2) / 0.5);   // likes ~0.55 s gaps
            if (s * prefer > best) { best = s * prefer; bestLag = lag; }
        }
        var mean = total / (maxLag - minLag + 1);
        _confidence = mean > 0 ? best / mean : 0;
        if (bestLag == 0 || _confidence < 1.1) return;    // no clear beat: keep the current period

        // Keep the tempo steady: small corrections ease in, but a very different tempo (songs can read as 94 or
        // 143 BPM from one window to the next) is only taken once it shows up twice in a row
        var candidate = bestLag / SampleRate;
        if (Math.Abs(candidate - _beatPeriod) / _beatPeriod < 0.08) _beatPeriod = _beatPeriod * 0.7 + candidate * 0.3;
        else if (_pendingPeriod > 0 && Math.Abs(candidate - _pendingPeriod) / _pendingPeriod < 0.08) { _beatPeriod = candidate; _pendingPeriod = 0; }
        else { _pendingPeriod = candidate; return; }
        // Phase: which offset within one period lines up with the most rises, counting back from now
        double bestPhase = 0, bestSum = -1;
        for (var phase = 0; phase < bestLag; phase++)
        {
            double sum = 0;
            for (var i = flux.Length - 1 - phase; i >= 0; i -= bestLag) sum += flux[i];
            if (sum > bestSum) { bestSum = sum; bestPhase = phase; }
        }
        var beatAgo = bestPhase / SampleRate;
        var expected = t - _lastBeat;
        if (Math.Abs(expected - beatAgo) > 0.08 && Math.Abs(expected - beatAgo) < _beatPeriod - 0.08)
            _lastBeat = t - beatAgo;                      // nudge the clock onto the music's pulse
    }

    static void Spring(ref double x, ref double v, double target, double stiffness, double damping, double dt)
    {
        v += (-stiffness * (x - target) - damping * v) * dt;
        x += v * dt;
    }

    void SetDanceLayers(bool on)
    {
        _layersOn = on;
        if (on)
        {
            DanceBody.Source ??= LoadFrame("dancebody");
            DanceHead.Source ??= LoadFrame("dancehead");
        }
        DanceBody.Visibility = DanceHead.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Sprite.Opacity = on ? 0 : 1;   // still there for clicks and hit testing
        if (!on && !_animating) ShowFrame(_face);
    }

    // ---- File search ----

    SearchWindow? _search;

    void OpenSearch()
    {
        if (_search is null)
        {
            _search = new SearchWindow();
            _search.Closed += (_, _) => _search = null;
            _search.Show();
        }
        _search.Activate();
    }

    void ActionSearch_Click(object sender, RoutedEventArgs e)
    {
        HideBubble();
        OpenSearch();
    }

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    // ---- Character interaction ----

    void Greet()
    {
        Say($"{Greeting()}, {_settings.UserName}!");
        if (_unread.Count > 0 || _unreplied.Count > 0)
        {
            ShowEmails(unreadTab: _unread.Count > 0);
            BubbleText.Text = $"{Greeting()}! {BubbleText.Text}";
        }
        Bounce();
        Wave();
        Sparkle();
        HappyMoment();
    }

    void Character_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var (left, top) = (Left, Top);
        DragMove();

        // Barely moved (a click with a little hand wobble), so it was a click: open the action list,
        // or close it if it's already open (never hide onboarding)
        var clicked = Math.Abs(Left - left) < 5 && Math.Abs(Top - top) < 5;
        if (!clicked) SavePosition();
        if (clicked && NamePanel.Visibility != Visibility.Visible)
        {
            if (Bubble.Visibility == Visibility.Visible && ActionPanel.Visibility == Visibility.Visible)
            {
                HideBubble();
                _idleTimer.Start();
            }
            else
                ShowActions();
        }
    }

    void ShowActions()
    {
        Say($"What would you like to do, {_settings.UserName}?");
        UpdateMusicCard();   // the music card sits on top of the list while something is playing or paused
        ActionPanel.Visibility = Visibility.Visible;
        if (_mood == Mood.Sleepy) Bounce();   // wake up
        HappyMoment();
    }

    // Someone started SMBot.exe again: come back into view and offer the action list instead of a second bot
    public void BringForward()
    {
        if (!OnScreen(Left, Top))
        {
            MoveToCorner();
            SavePosition();
        }
        Activate();
        if (!IsBusy) ShowActions();
    }

    public void OpenChecklist() => Checklist_Click(this, new RoutedEventArgs());

    // ---- Position and size ----

    const double BaseWidth = 500, BaseHeight = 440;   // tall enough for the action list with the music card on top

    // Where it was last dragged to, if that's still on a screen (a monitor may have been unplugged); otherwise the corner
    // Saved positions are for the full-size window; the compact one shares its bottom-right corner
    void PlaceWindow()
    {
        var s = SizeScale.ScaleX;
        if (_settings.BotLeft is { } left && _settings.BotTop is { } top
            && OnScreen(left + BaseWidth * s - Width, top + BaseHeight * s - Height))
            (Left, Top) = (left + BaseWidth * s - Width, top + BaseHeight * s - Height);
        else
            MoveToCorner();
    }

    void MoveToCorner()
    {
        var area = SystemParameters.WorkArea;   // bottom-right of the main screen, above the taskbar
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 8;
    }

    // The bot itself (right-hand part of the window) must be on the desktop, across all monitors
    bool OnScreen(double left, double top)
    {
        var (x, y) = (left + Width - 115 * SizeScale.ScaleX, top + Height - 130 * SizeScale.ScaleY);
        return x >= SystemParameters.VirtualScreenLeft && x <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && y >= SystemParameters.VirtualScreenTop && y <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
    }

    void SavePosition()
    {
        var s = SizeScale.ScaleX;
        (_settings.BotLeft, _settings.BotTop) = (Left + Width - BaseWidth * s, Top + Height - BaseHeight * s);
        SettingsService.Save(_settings);
    }

    // Small / Medium / Large scale the whole window, bubble included. keepAnchor keeps the bot's bottom-right corner in place.
    void ApplySize(string size, bool keepAnchor)
    {
        var scale = size switch { "small" => 0.8, "large" => 1.25, _ => 1.0 };
        var (oldWidth, oldHeight) = (Width, Height);
        SizeScale.ScaleX = SizeScale.ScaleY = scale;
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
        if (keepAnchor)
        {
            Left += oldWidth - Width;
            Top += oldHeight - Height;
        }
        SizeSmall.IsChecked = size == "small";
        SizeMedium.IsChecked = size is not ("small" or "large");
        SizeLarge.IsChecked = size == "large";
    }

    void Size_Click(object sender, RoutedEventArgs e)
    {
        _settings.BotSize = (string)((MenuItem)sender).Tag;
        ApplySize(_settings.BotSize, keepAnchor: true);
        if (!OnScreen(Left, Top)) MoveToCorner();
        SavePosition();
    }

    // ---- Head-only mode ----

    // In the frames the head ends at y 133 and the body starts at y 141 (230 x 260 units), with nothing in between
    const double HeadBottom = 137, HeadDrop = 115;

    // Cut the bot off below the head and lower the head to the body's place. The clip also limits clicks to the head,
    // so the empty space where the body was lets clicks through to the desktop
    void ApplyHeadOnly(bool headOnly)
    {
        BotLayer.Clip = headOnly ? new RectangleGeometry(new Rect(0, 0, 230, HeadBottom)) : null;
        HeadOnlyShift.Y = headOnly ? HeadDrop : 0;
        Character.Background = headOnly ? null : Brushes.Transparent;
        HeadOnlyItem.IsChecked = headOnly;
    }

    void HeadOnly_Click(object sender, RoutedEventArgs e)
    {
        _settings.HeadOnly = HeadOnlyItem.IsChecked;
        ApplyHeadOnly(_settings.HeadOnly);
        SettingsService.Save(_settings);
    }

    // ---- Count badge ----

    // Open items due today, overdue ones included
    void UpdateBadge()
    {
        var count = ReminderService.All.Count(r => !r.IsDone && r.Due < DateTime.Today.AddDays(1));
        Badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BadgeText.Text = count > 9 ? "9+" : count.ToString();
        Badge.ToolTip = count == 1 ? "1 item due today" : $"{count} items due today";
    }

    void ActionAddReminder_Click(object sender, RoutedEventArgs e) => StartCapture();

    void ActionChecklist_Click(object sender, RoutedEventArgs e)
    {
        HideBubble();
        Checklist_Click(sender, e);
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _settings = new Settings();
        SettingsService.Save(_settings);
        StartOnboarding();
    }

    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Expressions ----

    void SetMood(Mood mood)
    {
        _mood = mood;
        _face = mood switch
        {
            Mood.Happy => "happy",
            Mood.Curious => "curious",
            Mood.Surprised => "surprised",
            Mood.Sleepy => "sleepy",
            _ => "neutral",
        };
        if (!_animating && !_dancing) ShowFrame(_face, fade: true);   // while dancing, the dance poses show instead
    }

    // fade: crossfade from the current frame (used for expression changes, not for animation frames)
    void ShowFrame(string name, bool fade = false)
    {
        if (!Frames.TryGetValue(name, out var frame)) Frames[name] = frame = LoadFrame(name);
        if (fade && Sprite.Source is not null && Sprite.Source != frame)
        {
            FadeSprite.Source = Sprite.Source;
            FadeSprite.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
        }
        Sprite.Source = frame;
    }

    // The PNGs are 460 px wide (sharp at 200% display scaling); decoding them only as large as this screen needs,
    // up to the Large size, keeps them small in memory (about 0.4 MB each instead of 1 MB at 100% scaling)
    BitmapImage LoadFrame(string name)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var frame = new BitmapImage();
        frame.BeginInit();
        frame.UriSource = new Uri($"pack://application:,,,/Assets/Bot/{name}.png");
        frame.DecodePixelWidth = (int)Math.Min(460, Math.Ceiling(230 * 1.25 * dpi));
        frame.CacheOption = BitmapCacheOption.OnLoad;
        frame.EndInit();
        frame.Freeze();
        return frame;
    }

    // Plays frames in order, then settles back on the current mood's face. A newer animation cancels this one.
    async void PlayFrames(IEnumerable<string> frames, int msPerFrame)
    {
        var id = ++_animationId;
        _animating = true;
        foreach (var frame in frames)
        {
            if (id != _animationId) return;
            ShowFrame(frame);
            await Task.Delay(msPerFrame);
        }
        if (id != _animationId) return;
        _animating = false;
        ShowFrame(_face);
    }

    void Wave() => PlayFrames(["wave1", "wave2", "wave3", "wave4", "wave3", "wave4", "wave3", "wave2", "wave1"], 85);

    // A full turn with fins up
    void Celebrate() => PlayFrames(Enumerable.Range(0, 12).Select(i => $"spin{i}"), 55);

    // Quick blink every few seconds while the eyes are open
    async Task Blink()
    {
        _blinkTimer.Interval = TimeSpan.FromSeconds(3 + Random.Shared.NextDouble() * 3);
        if (_animating || _dancing || _face is "happy" or "sleepy") return;
        ShowFrame("blink");
        await Task.Delay(110);
        if (!_animating && !_dancing) ShowFrame(_face);
    }

    // Happy eyes for a few seconds, then back to normal and start the idle countdown
    async void HappyMoment()
    {
        _idleTimer.Stop();
        SetMood(Mood.Happy);
        await Task.Delay(3500);
        if (_mood == Mood.Happy) SetMood(Mood.Neutral);
        _idleTimer.Start();
    }

    // Wide eyes and a hop to get attention, then back to normal
    async void AlertMoment()
    {
        _idleTimer.Stop();
        SetMood(Mood.Surprised);
        Bounce();
        await Task.Delay(1500);
        if (_mood == Mood.Surprised) SetMood(Mood.Neutral);
        _idleTimer.Start();
    }

    // Squash, stretch up into a hop, land with a little squash
    void Bounce()
    {
        static KeyTime t(double seconds) => TimeSpan.FromSeconds(seconds);
        var squashY = new DoubleAnimationUsingKeyFrames();
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(0.82, t(0.12)));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, t(0.3)));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, t(0.55)));
        squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, t(0.75)));
        var squashX = new DoubleAnimationUsingKeyFrames();
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, t(0.12)));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, t(0.3)));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, t(0.55)));
        squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, t(0.75)));
        var jump = new DoubleAnimationUsingKeyFrames();
        jump.KeyFrames.Add(new EasingDoubleKeyFrame(0, t(0.12)));
        jump.KeyFrames.Add(new EasingDoubleKeyFrame(-22, t(0.32), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        jump.KeyFrames.Add(new EasingDoubleKeyFrame(0, t(0.55), new QuadraticEase { EasingMode = EasingMode.EaseIn }));

        SquashScale.BeginAnimation(ScaleTransform.ScaleYProperty, squashY);
        SquashScale.BeginAnimation(ScaleTransform.ScaleXProperty, squashX);
        JumpTransform.BeginAnimation(TranslateTransform.YProperty, jump);
    }

    // Little glowing stars that burst out around the head and fade
    void Sparkle()
    {
        var random = new Random();
        for (var i = 0; i < 7; i++)
        {
            var angle = -Math.PI / 2 + (i - 3) * 0.45 + random.NextDouble() * 0.2;
            var distance = 45 + random.Next(15);
            var move = new TranslateTransform();
            var star = new Path
            {
                Data = Geometry.Parse("M 0,-6 L 1.5,-1.5 6,0 1.5,1.5 0,6 -1.5,1.5 -6,0 -1.5,-1.5 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0xEF, 0xFF)),
                Effect = new DropShadowEffect { Color = Color.FromRgb(0xB6, 0x9C, 0xFF), BlurRadius = 8, ShadowDepth = 0 },
                RenderTransform = move,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(star, 112);   // around the head in the 3D frames
            Canvas.SetTop(star, 94);
            Character.Children.Add(star);

            var duration = TimeSpan.FromSeconds(0.9);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Math.Cos(angle) * distance, duration) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Math.Sin(angle) * distance, duration) { EasingFunction = ease });
            var fade = new DoubleAnimation(1, 0, duration) { BeginTime = TimeSpan.FromSeconds(0.2 * random.NextDouble()) };
            fade.Completed += (_, _) => Character.Children.Remove(star);
            star.BeginAnimation(OpacityProperty, fade);
        }
    }

    // ---- Helpers ----

    void Say(string text)
    {
        BubbleText.Text = text;
        EmailTabs.Visibility = Visibility.Collapsed;
        EmailList.Visibility = Visibility.Collapsed;
        MoreEmails.Visibility = Visibility.Collapsed;
        CapturePanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        DuePanel.Visibility = Visibility.Collapsed;
        SnoozeMorePanel.Visibility = Visibility.Collapsed;
        MissedPanel.Visibility = Visibility.Collapsed;
        RecentList.Visibility = Visibility.Collapsed;
        ActionPanel.Visibility = Visibility.Collapsed;
        MusicCard.Visibility = Visibility.Collapsed;
        Bubble.Visibility = Visibility.Visible;
    }

    // Hiding also closes capture or a due reminder (an unanswered one pops up again later)
    void HideBubble()
    {
        Bubble.Visibility = Visibility.Hidden;
        CapturePanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        DuePanel.Visibility = Visibility.Collapsed;
        SnoozeMorePanel.Visibility = Visibility.Collapsed;
        MissedPanel.Visibility = Visibility.Collapsed;
        RecentList.Visibility = Visibility.Collapsed;
        ActionPanel.Visibility = Visibility.Collapsed;
        MusicCard.Visibility = Visibility.Collapsed;
        SetMood(Mood.Neutral);
    }

    void Play(string storyboard) => ((Storyboard)FindResource(storyboard)).Begin(this, true);

    static string Greeting() => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };
}
