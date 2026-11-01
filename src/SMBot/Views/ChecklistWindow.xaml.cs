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
        if (Sections.Children.Count == 0)
            Sections.Children.Add(new TextBlock
            {
                Text = "No reminders yet. Type one above, or press Ctrl + Alt + R anywhere.",
                Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 14, 0, 0),
            });
    }

    // Section title with a small count pill, e.g. "Upcoming (1)"
    void AddSection(string title, List<Reminder> items, Brush titleBrush)
    {
        if (items.Count == 0) return;
        var count = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xE7, 0xFF)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = items.Count.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AccentBrush },
        };
        Sections.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(2, 14, 0, 4),
            Children = { new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleBrush }, count },
        });
        foreach (var r in items) Sections.Children.Add(Row(r));
    }

    UIElement Row(Reminder r)
    {
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
        var when = new TextBlock { Text = r.IsDone ? "done" : r.When, FontSize = 11.5, Foreground = MutedBrush, Margin = new Thickness(0, 2, 0, 0) };
        var info = new StackPanel { Children = { text, when } };

        var delete = new Button { Style = (Style)FindResource("DeleteButton"), VerticalAlignment = VerticalAlignment.Top };
        delete.Click += (_, _) => ReminderService.Delete(r);

        var row = new DockPanel();
        DockPanel.SetDock(check, Dock.Left);
        DockPanel.SetDock(delete, Dock.Right);
        row.Children.Add(check);
        row.Children.Add(delete);
        row.Children.Add(info);
        return new Border { Style = (Style)FindResource("TaskCard"), Child = row };
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
