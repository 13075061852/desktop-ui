using System.IO;

namespace DeskNest.App.Services;

internal sealed class DesktopWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Threading.Timer _debounceTimer;
    private bool _disposed;

    public DesktopWatcher()
    {
        _debounceTimer = new System.Threading.Timer(_ => Changed?.Invoke(this, EventArgs.Empty));

        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
            };
            watcher.Created += OnFileSystemChanged;
            watcher.Deleted += OnFileSystemChanged;
            watcher.Changed += OnFileSystemChanged;
            watcher.Renamed += OnFileSystemChanged;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public event EventHandler? Changed;

    private void OnFileSystemChanged(object sender, FileSystemEventArgs args)
    {
        if (!_disposed)
        {
            _debounceTimer.Change(TimeSpan.FromMilliseconds(450), Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _debounceTimer.Dispose();
    }
}
