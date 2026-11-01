using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SMBot.Services;

namespace SMBot.Views;

// Spotlight-style file search: type, arrow keys to pick, Enter opens, Ctrl+Enter opens the folder, Esc closes
public partial class SearchWindow : Window
{
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly HashSet<string> _recent;

    public SearchWindow()
    {
        InitializeComponent();
        // Files opened recently (Windows' Recent items) rank first
        _recent = new HashSet<string>(RecentFilesService.GetRecent(40).Select(r => r.FilePath), StringComparer.OrdinalIgnoreCase);
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
        FileSearchService.IndexChanged += OnIndexChanged;
        Closed += (_, _) => FileSearchService.IndexChanged -= OnIndexChanged;
        Loaded += (_, _) => { Activate(); QueryBox.Focus(); };
        ShowIdleStatus();
    }

    void OnIndexChanged() => Dispatcher.BeginInvoke(() => { if (QueryBox.Text.Length > 0) RunSearch(); else ShowIdleStatus(); });

    void ShowIdleStatus() => Status.Text = FileSearchService.Ready
        ? $"{FileSearchService.Count:N0} files and folders  ·  Enter open  ·  Ctrl+Enter open folder  ·  Esc close"
        : "Getting your files ready… one moment";

    void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Hint.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    void RunSearch()
    {
        var query = QueryBox.Text.Trim();
        if (query.Length == 0)
        {
            Results.ItemsSource = null;
            Results.Visibility = Visibility.Collapsed;
            ShowIdleStatus();
            return;
        }
        var hits = FileSearchService.Search(query, _recent);
        Results.ItemsSource = hits;
        Results.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hits.Count > 0) Results.SelectedIndex = 0;
        Status.Text = !FileSearchService.Ready ? "Still getting your files ready… results may be incomplete"
            : hits.Count == 0 ? $"Nothing found for \"{query}\""
            : "Enter open  ·  Ctrl+Enter open folder  ·  Esc close";
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Down when Results.Items.Count > 0:
                Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.Items.Count - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                break;
            case Key.Up when Results.Items.Count > 0:
                Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0);
                Results.ScrollIntoView(Results.SelectedItem);
                break;
            case Key.Enter when Results.SelectedItem is FileHit hit:
                Launch(hit, folder: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is FileHit hit) Launch(hit, folder: false);
    }

    void FolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FileHit hit) Launch(hit, folder: true);
    }

    void Launch(FileHit hit, bool folder)
    {
        try
        {
            if (folder) FileSearchService.ShowInFolder(hit);
            else FileSearchService.Open(hit);
            Close();
        }
        catch (Exception ex)
        {
            Status.Text = $"Couldn't open it: {ex.Message}";
        }
    }

    // Clicking anywhere else closes it, like the Start menu
    bool _closing;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsLoaded && !_closing) Close();
    }
}
