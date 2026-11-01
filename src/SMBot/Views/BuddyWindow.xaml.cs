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
                   || ConfirmPanel.Visibility == Visibility.Visible || DuePanel.Visibility == Visibility.Visible;

    public BuddyWindow()
    {
        InitializeComponent();
        _reminderTimer.Tick += (_, _) => CheckDueReminders();
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
        // Bottom-right corner of the screen, above the taskbar
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 8;

        Play("Hover");
        _blinkTimer.Start();
        _ = FileSearchService.BuildAsync(FileSearchService.DefaultFolders());   // file search list, built in the background

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
        var due = ReminderService.DueNow()
            .FirstOrDefault(r => !_lastAlert.TryGetValue(r.Id, out var last) || DateTime.Now - last > TimeSpan.FromMinutes(10));
        if (due is null) return;

        _lastAlert[due.Id] = DateTime.Now;
        _dueReminder = due;
        var text = due.Text.Length > 1 && char.IsLower(due.Text[1]) ? char.ToLower(due.Text[0]) + due.Text[1..] : due.Text;
        Say($"{_settings.UserName}, time to {text}!");
        DuePanel.Visibility = Visibility.Visible;
        Wave();
        AlertMoment();
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

    void Snooze10_Click(object sender, RoutedEventArgs e) => Snooze(TimeSpan.FromMinutes(10));

    void Snooze60_Click(object sender, RoutedEventArgs e) => Snooze(TimeSpan.FromHours(1));

    void Snooze(TimeSpan by)
    {
        if (_dueReminder is null) return;
        ReminderService.Snooze(_dueReminder, by);
        _lastAlert.Remove(_dueReminder.Id);
        Say($"Okay, I'll remind you again at {_dueReminder.Due:h:mm tt}.");
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
    const int HotkeyId = 1, SearchHotkeyId = 2, WmHotkey = 0x0312;

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
            return IntPtr.Zero;
        });
        const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, KeyR = 0x52, KeyF = 0x46;
        // If another app owns one, the action list still works
        RegisterHotKey(handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, KeyR);
        RegisterHotKey(handle, SearchHotkeyId, ModControl | ModAlt | ModNoRepeat, KeyF);
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
        ActionPanel.Visibility = Visibility.Visible;
        if (_mood == Mood.Sleepy) Bounce();   // wake up
        HappyMoment();
    }

    // Someone started SMBot.exe again: come back into view and offer the action list instead of a second bot
    public void BringForward()
    {
        var area = SystemParameters.WorkArea;
        if (Left + Width / 2 < area.Left || Left + Width / 2 > area.Right || Top + Height / 2 < area.Top || Top + Height / 2 > area.Bottom)
        {
            Left = area.Right - Width - 16;
            Top = area.Bottom - Height - 8;
        }
        Activate();
        if (!IsBusy) ShowActions();
    }

    public void OpenChecklist() => Checklist_Click(this, new RoutedEventArgs());

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
        if (!_animating) ShowFrame(_face, fade: true);
    }

    // fade: crossfade from the current frame (used for expression changes, not for animation frames)
    void ShowFrame(string name, bool fade = false)
    {
        if (!Frames.TryGetValue(name, out var frame))
        {
            frame = new BitmapImage(new Uri($"pack://application:,,,/Assets/Bot/{name}.png"));
            frame.Freeze();
            Frames[name] = frame;
        }
        if (fade && Sprite.Source is not null && Sprite.Source != frame)
        {
            FadeSprite.Source = Sprite.Source;
            FadeSprite.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
        }
        Sprite.Source = frame;
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
        if (_animating || _face is "happy" or "sleepy") return;
        ShowFrame("blink");
        await Task.Delay(110);
        if (!_animating) ShowFrame(_face);
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
        RecentList.Visibility = Visibility.Collapsed;
        ActionPanel.Visibility = Visibility.Collapsed;
        Bubble.Visibility = Visibility.Visible;
    }

    // Hiding also closes capture or a due reminder (an unanswered one pops up again later)
    void HideBubble()
    {
        Bubble.Visibility = Visibility.Hidden;
        CapturePanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        DuePanel.Visibility = Visibility.Collapsed;
        RecentList.Visibility = Visibility.Collapsed;
        ActionPanel.Visibility = Visibility.Collapsed;
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
