using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace StoreZip.App;

/// <summary>One line of the results list.</summary>
public sealed record ResultRow(string Mark, IBrush? Brush, string Text);

/// <summary>
/// Drop zips or folders → the zips are converted into the output folder,
/// several at a time (Workers). Drops while a run is going are queued behind it.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly ObservableCollection<ResultRow> _rows = [];
    private readonly Queue<string> _queue = new();
    // Worker loops resume on the UI thread, so these need no locking
    private readonly List<string> _active = [];
    private readonly HashSet<string> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancel;
    private bool _running;
    private int _done, _total, _failed;

    public MainWindow()
    {
        InitializeComponent();
        OutputBox.Text = _settings.OutputFolder;
        RenameBox.IsChecked = _settings.RenameExtension;
        WorkersBox.Value = Math.Clamp(_settings.Workers, 1, 16);
        Results.ItemsSource = _rows;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropRect.Stroke = Brush("AppBorderBrush"));
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closing += (_, _) => SaveSettings();
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var b) ? b as IBrush : null;

    private void SaveSettings()
    {
        _settings.OutputFolder = OutputBox.Text?.Trim() ?? "";
        _settings.RenameExtension = RenameBox.IsChecked == true;
        _settings.Workers = (int)Math.Clamp(WorkersBox.Value ?? 2, 1, 16);
        _settings.Save();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        DropRect.Stroke = Brush("DropBrush");
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropRect.Stroke = Brush("AppBorderBrush");
        var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        await EnqueueAsync(paths);
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select MAME set zips",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Zip files") { Patterns = ["*.zip", "*.zip_"] }],
        });
        await EnqueueAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a folder of MAME set zips (its subfolders are searched too)",
        });
        await EnqueueAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e) => await PickOutputAsync();

    private async Task<bool> PickOutputAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select the output folder for the StoreZips" });
        string? path = folders.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
        if (path is null)
            return false;
        OutputBox.Text = path;
        SaveSettings();
        return true;
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        string folder = OutputBox.Text?.Trim() ?? "";
        if (Directory.Exists(folder))
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
    }

    private async Task EnqueueAsync(IEnumerable<string> paths)
    {
        var inputs = paths.ToList();
        if (inputs.Count == 0)
            return;
        if (string.IsNullOrWhiteSpace(OutputBox.Text) && !await PickOutputAsync())
        {
            StatusText.Text = "Pick an output folder first.";
            return;
        }
        foreach (string skipped in inputs.Where(p => File.Exists(p) && !StoreZipJob.IsZip(p)))
            _rows.Add(new ResultRow("✗", Brush("ErrorBrush"), $"{Path.GetFileName(skipped)}: not a .zip — only zip files can be converted"));
        var files = StoreZipJob.Expand(inputs).Where(StoreZipJob.IsZip).ToList();
        if (files.Count == 0)
        {
            StatusText.Text = "No zips found in what was added.";
            return;
        }
        foreach (string f in files)
            _queue.Enqueue(f);
        _total += files.Count;
        if (!_running)
            await RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        _running = true;
        _cancel = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        ClearButton.IsEnabled = false;
        Progress.IsVisible = true;
        SaveSettings();
        string output = _settings.OutputFolder;
        bool rename = _settings.RenameExtension;
        var token = _cancel.Token;

        // Each worker takes the next zip off the queue, so drops during the run join in
        async Task Worker()
        {
            while (_queue.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string source = _queue.Dequeue();
                string target = StoreZipJob.OutputPath(source, output, rename);
                if (!_outputs.Add(target))
                {
                    // Two sources with the same name would write the same file
                    Finish(source, new StoreZipJobResult(source, target, null,
                        $"another zip in this run already writes {Path.GetFileName(target)} — skipped"));
                    continue;
                }
                _active.Add(Path.GetFileName(source));
                ShowStatus();
                try
                {
                    Finish(source, await Task.Run(() => StoreZipJob.ConvertOne(source, output, rename, token), token));
                }
                finally
                {
                    _active.Remove(Path.GetFileName(source));
                }
            }
        }

        try
        {
            int workers = Math.Clamp(_settings.Workers, 1, 16);
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker()));
            StatusText.Text = $"Done: {_done - _failed} of {_done} converted" + (_failed > 0 ? $", {_failed} failed." : ".") + $"  Output: {output}";
        }
        catch (OperationCanceledException)
        {
            _queue.Clear();
            StatusText.Text = $"Cancelled after {_done} of {_total}.";
        }
        finally
        {
            Progress.IsVisible = false;
            CancelButton.IsEnabled = false;
            ClearButton.IsEnabled = true;
            _running = false;
            _done = _total = _failed = 0;
            _active.Clear();
            _outputs.Clear();
        }
    }

    private void ShowStatus()
    {
        StatusText.Text = $"Converting ({_done} of {_total} done): {string.Join(", ", _active)}";
        Progress.Value = (double)_done / Math.Max(1, _total);
    }

    private void Finish(string source, StoreZipJobResult r)
    {
        _done++;
        if (r.Error is null)
        {
            _rows.Add(new ResultRow("✓", Brush("OkBrush"),
                $"{Path.GetFileName(r.Output)}  {r.Result!.Members.Count} files  {r.Result.Size:N0} bytes  crc {r.Result.CrcHex}  sha1 {r.Result.Sha1}"));
        }
        else
        {
            _failed++;
            _rows.Add(new ResultRow("✗", Brush("ErrorBrush"), $"{Path.GetFileName(source)}: {r.Error}"));
        }
        Results.ScrollIntoView(_rows.Count - 1);
        ShowStatus();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _cancel?.Cancel();

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        _rows.Clear();
        StatusText.Text = "Ready.";
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not null)
            await Clipboard.SetTextAsync(string.Join(Environment.NewLine, _rows.Select(r => r.Mark + " " + r.Text)));
    }
}
