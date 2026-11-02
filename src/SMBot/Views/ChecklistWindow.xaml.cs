using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SMBot.Services;

namespace SMBot.Views;

public partial class ChecklistWindow : Window
{
    static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x2F, 0x66));
    static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x7D, 0xB8));
    static readonly Brush OverdueBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x6F, 0xE0));
    static readonly Brush NoteBackground = new SolidColorBrush(Color.FromRgb(0xF6, 0xF2, 0xFF));

    public ChecklistWindow()
    {
        InitializeComponent();
        ReminderService.Changed += Refresh;
        Closed += (_, _) => ReminderService.Changed -= Refresh;
        Refresh();
    }

    void Refresh()
    {
        var now = DateTime.Now;
        var today = now.Date;
        var open = ReminderService.All.Where(r => !r.IsDone).OrderByDescending(r => r.High).ThenBy(r => r.Due).ToList();
        var doneToday = ReminderService.All.Where(r => r.DoneAt?.Date == today).ToList();

        var overdue = open.Where(r => r.Due < now).ToList();
        var dueToday = open.Where(r => r.Due >= now && r.Due < today.AddDays(1)).Concat(doneToday).ToList();
        var tomorrow = open.Where(r => r.Due >= today.AddDays(1) && r.Due < today.AddDays(2)).ToList();
        var upcoming = open.Where(r => r.Due >= today.AddDays(2)).ToList();
        var anytime = open.Where(r => r.Due is null).ToList();

        var total = overdue.Count + dueToday.Count;
        Progress.Value = total == 0 ? 1 : (double)doneToday.Count / total;
        ProgressText.Text = total == 0 ? "Nothing due today" : $"Today: {doneToday.Count} of {total} done";

        Sections.Children.Clear();
        AddSection("Overdue", overdue, OverdueBrush);
        AddSection("Today", dueToday, TextBrush);
        AddSection("Tomorrow", tomorrow, TextBrush);
        AddSection("Upcoming", upcoming, TextBrush);
        AddSection("Anytime", anytime, TextBrush);
        AddCompletedSection(ReminderService.Completed().Where(r => r.DoneAt!.Value.Date < today).ToList());
        if (Sections.Children.Count == 0)
            Sections.Children.Add(new TextBlock
            {
                Text = "No reminders yet. Type one above, or press Ctrl + Alt + R anywhere.",
                Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 14, 0, 0),
            });
    }

    // ---- Completed (earlier days; today's stay ticked under Today) ----

    bool _showCompleted;

    void AddCompletedSection(List<Reminder> done)
    {
        if (done.Count == 0) return;
        var toggle = new Button
        {
            Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = _showCompleted ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                                    FontSize = 10, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                    new TextBlock { Text = "Completed", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = MutedBrush },
                    Pill(done.Count),
                },
            },
        };
        toggle.Click += (_, _) => { _showCompleted = !_showCompleted; Refresh(); };

        var clear = new Button
        {
            Content = "Clear all", Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = AccentBrush, FontSize = 12, Visibility = _showCompleted ? Visibility.Visible : Visibility.Collapsed,
        };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Delete all {done.Count + ReminderService.Completed().Count(r => r.DoneAt!.Value.Date == DateTime.Today)} completed items?",
                                "SMBot checklist", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                ReminderService.ClearCompleted();
        };

        var header = new DockPanel { Margin = new Thickness(2, 18, 0, 4) };
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(toggle);
        Sections.Children.Add(header);
        if (_showCompleted)
            foreach (var r in done) Sections.Children.Add(Row(r));
    }

    Border Pill(int count) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xE7, 0xFF)), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = count.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AccentBrush },
    };

    // Section title with a small count pill, e.g. "Upcoming (1)"
    void AddSection(string title, List<Reminder> items, Brush titleBrush)
    {
        if (items.Count == 0) return;
        Sections.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(2, 14, 0, 4),
            Children = { new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleBrush }, Pill(items.Count) },
        });
        foreach (var r in items) Sections.Children.Add(Row(r));
    }

    // ---- Editing: click a task's text to change what it says, when, and its notes ----

    Guid? _editingId;
    bool _focusNote;   // opened from "Add note", so the cursor starts in the notes box

    UIElement EditRow(Reminder r)
    {
        var textBox = new TextBox { Text = (r.High ? "! " : "") + r.Text, FontSize = 13.5, Foreground = TextBrush, Padding = new Thickness(6, 4, 6, 4),
                                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xB8, 0xF5)) };
        var whenBox = new TextBox { Text = r.WhenForEditing, FontSize = 12.5, Foreground = TextBrush, Padding = new Thickness(6, 4, 6, 4),
                                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xB8, 0xF5)), Margin = new Thickness(0, 6, 0, 0) };
        var hint = new TextBlock { Text = "When: e.g. tomorrow 3pm, 15 Dis 10am, every Friday 9am. Leave empty for anytime. Start with ! for important.",
                                   FontSize = 11, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0) };
        var noteLabel = new TextBlock { Text = "Notes", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = MutedBrush,
                                        Margin = new Thickness(2, 10, 0, 3) };
        var noteBox = new TextBox { Text = r.Note ?? "", FontSize = 12.5, Foreground = TextBrush, Padding = new Thickness(6, 4, 6, 4),
                                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xB8, 0xF5)), AcceptsReturn = true,
                                    TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 160,
                                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                    ToolTip = "Remarks for this task. Enter starts a new line; Ctrl + Enter saves." };
        var error = new TextBlock { FontSize = 11.5, Foreground = OverdueBrush, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
                                    Margin = new Thickness(2, 4, 0, 0) };

        void Save()
        {
            var text = textBox.Text.Trim();
            var high = text.StartsWith('!');
            text = text.TrimStart('!', ' ');
            var (due, repeat, leftover) = ReminderParser.ParseWhen(whenBox.Text, DateTime.Now);
            if (text.Length == 0) { Show("Type what the task is."); return; }
            if (leftover.Length > 0) { Show($"I didn't understand \"{leftover}\". Try something like \"tomorrow 3pm\"."); return; }
            _editingId = null;
            ReminderService.Update(r, text, due, repeat, high, noteBox.Text);   // saving refreshes the list
        }
        void Show(string message) { error.Text = message; error.Visibility = Visibility.Visible; }
        void Cancel() { _editingId = null; Refresh(); }
        void Keys(object s, KeyEventArgs e) { if (e.Key == Key.Enter) Save(); else if (e.Key == Key.Escape) Cancel(); }
        textBox.KeyDown += Keys;
        whenBox.KeyDown += Keys;
        // Notes take several lines, so plain Enter is a new line there
        noteBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Save(); }
            else if (e.Key == Key.Escape) Cancel();
        };

        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand,
                                  Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xCC, 0xFA)), Foreground = TextBrush };
        cancel.Click += (_, _) => Cancel();
        var save = new Button { Content = "Save", Style = (Style)FindResource("PurpleButton"), Height = 28 };
        save.Click += (_, _) => Save();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0),
                                       Children = { cancel, save } };
        var focus = _focusNote ? noteBox : textBox;
        _focusNote = false;
        Dispatcher.BeginInvoke(() => { focus.Focus(); focus.CaretIndex = focus.Text.Length; });
        return new Border { Style = (Style)FindResource("TaskCard"), BorderBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xA6, 0xF2)),
                            Child = new StackPanel { Children = { textBox, whenBox, hint, noteLabel, noteBox, error, buttons } } };
    }

    UIElement Row(Reminder r)
    {
        if (r.Id == _editingId) return EditRow(r);

        var check = new CheckBox
        {
            IsChecked = r.IsDone, Style = (Style)FindResource("RoundCheck"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0),
        };
        check.Click += (_, _) =>
        {
            if (r.IsDone) ReminderService.Undo(r);
            else ReminderService.Complete(r);
        };

        var text = new TextBlock { Text = (r.High ? "! " : "") + r.Text, FontSize = 13.5, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap };
        if (r.IsDone)
        {
            text.TextDecorations = TextDecorations.Strikethrough;
            text.Foreground = MutedBrush;
        }
        var doneWhen = r.DoneAt is { } d ? (d.Date == DateTime.Today ? "done" : $"done {d:d MMM}") : "";
        var when = new TextBlock { Text = r.IsDone ? doneWhen : r.When, FontSize = 11.5, Foreground = MutedBrush, Margin = new Thickness(0, 2, 0, 0) };
        var info = new StackPanel { Children = { text, when }, Background = Brushes.Transparent };
        if (!r.IsDone)
        {
            info.Cursor = Cursors.IBeam;
            info.ToolTip = "Click to edit";
            info.MouseLeftButtonUp += (_, _) => { _editingId = r.Id; Refresh(); };
        }

        // Notes under the task: shown when there are some; open tasks without notes get an "Add note" link
        var row = new DockPanel();
        var card = new StackPanel { Children = { row } };
        FrameworkElement? notes = null;
        if (r.Note is { Length: > 0 } note)
            notes = new Border
            {
                Background = NoteBackground, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 6),
                Margin = new Thickness(30, 6, 0, 0), ToolTip = r.IsDone ? null : "Click to edit the notes",
                Child = new TextBlock { Text = note, FontSize = 12, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap },
            };
        else if (!r.IsDone)
            notes = new TextBlock { Text = "+ Add note", FontSize = 11.5, Foreground = AccentBrush,
                                    Margin = new Thickness(30, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        if (notes is not null)
        {
            if (!r.IsDone)
            {
                notes.Cursor = Cursors.Hand;
                notes.MouseLeftButtonUp += (_, _) => { _editingId = r.Id; _focusNote = true; Refresh(); };
            }
            card.Children.Add(notes);
        }

        var delete = new Button { Style = (Style)FindResource("DeleteButton"), VerticalAlignment = VerticalAlignment.Top };
        delete.Click += (_, _) => ReminderService.Delete(r);

        DockPanel.SetDock(check, Dock.Left);
        DockPanel.SetDock(delete, Dock.Right);
        row.Children.Add(check);
        row.Children.Add(delete);
        row.Children.Add(info);
        return new Border { Style = (Style)FindResource("TaskCard"), Child = card };
    }

    // Light title bar that matches the window (Windows 11; ignored on older versions)
    void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int caption = 0x00FFFFFF, text = 0x00662F3B, border = 0x00F5B8C9;   // COLORREF is 0x00BBGGRR: white, #3B2F66, #C9B8F5
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    void Add_Click(object sender, RoutedEventArgs e) => AddFromBox();

    void AddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddFromBox();
    }

    // Added straight away: the list itself shows what was understood, and ✕ removes a wrong one
    void AddFromBox()
    {
        var parsed = ReminderParser.Parse(AddBox.Text, DateTime.Now);
        if (parsed.Count == 0) return;
        ReminderService.Add(parsed);
        AddBox.Clear();
    }
}
