using System.IO;

namespace DeskNest.App.Services;

internal sealed class DesktopWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Threading.Timer _debounceTimer;
    private readonly object _sync = new();
    private bool _disposed;

    public DesktopWatcher()
    {
        _debounceTimer = new System.Threading.Timer(_ =>
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        });

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
            watcher.Error += (_, _) => ScheduleRefresh();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public event EventHandler? Changed;

    private void OnFileSystemChanged(object sender, FileSystemEventArgs args)
    {
        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _debounceTimer.Change(TimeSpan.FromMilliseconds(450), Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounceTimer.Dispose();
        }
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

    }
}
