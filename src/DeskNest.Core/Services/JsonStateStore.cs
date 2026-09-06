using System.Text.Json;
using DeskNest.Core.Models;

namespace DeskNest.Core.Services;

public sealed class JsonStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _statePath;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public JsonStateStore(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskNest",
            "state.json");
        _backupPath = _statePath + ".bak";
    }

    public string StatePath => _statePath;

    public async Task<AppState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var primary = await TryLoadAsync(_statePath, cancellationToken).ConfigureAwait(false);
        if (primary is not null)
        {
            return Normalize(primary);
        }

        var backup = await TryLoadAsync(_backupPath, cancellationToken).ConfigureAwait(false);
        return backup is null ? AppState.CreateDefault() : Normalize(backup);
    }

    public async Task SaveAsync(AppState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Capture on the caller's thread before yielding: UI edits must not mutate
        // collections while an asynchronous serializer is enumerating them.
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_statePath))
            {
                // Do not replace the last healthy backup with a corrupt primary.
                var validPrimary = await TryLoadAsync(_statePath, cancellationToken).ConfigureAwait(false);
                File.Replace(temporaryPath, _statePath, validPrimary is null ? null : _backupPath);
            }
            else
            {
                File.Move(temporaryPath, _statePath);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _saveGate.Release();
        }
    }

    private static async Task<AppState?> TryLoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<AppState>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static AppState Normalize(AppState state)
    {
        state.SchemaVersion = Math.Max(1, state.SchemaVersion);
        state.Theme = state.Theme is "Light" or "Dark" or "System" ? state.Theme : "System";
        state.PanelOpacity = Math.Clamp(state.PanelOpacity, 0.55, 0.98);
        state.IconSize = Math.Clamp(state.IconSize, 32, 72);
        state.WallpaperSelection ??= string.Empty;
        state.ToolbarAlignment = state.ToolbarAlignment is "Left" or "Center" or "Right"
            ? state.ToolbarAlignment
            : "Center";
        state.Zones ??= [];

        state.Zones.RemoveAll(zone => zone is null);
        var zoneIds = new HashSet<Guid>();
        var itemIds = new HashSet<Guid>();
        foreach (var zone in state.Zones)
        {
            if (zone.Id == Guid.Empty || !zoneIds.Add(zone.Id))
            {
                zone.Id = Guid.NewGuid();
                zoneIds.Add(zone.Id);
            }
            zone.CategoryKey = string.IsNullOrWhiteSpace(zone.CategoryKey) ? "other" : zone.CategoryKey;
            zone.Name = string.IsNullOrWhiteSpace(zone.Name) ? "未命名分区" : zone.Name.Trim();
            zone.AccentColor = string.IsNullOrWhiteSpace(zone.AccentColor) ? "#7DD3FC" : zone.AccentColor;
            zone.Width = Math.Max(150, zone.Width);
            // The expand clamp may legitimately store a height below the 150
            // resize minimum: expanding into a narrow gap stops at the zone
            // below instead of covering it. Keep those heights on load, so a
            // re-expanded zone does not silently overlap its neighbour.
            zone.Height = Math.Max(52, zone.Height);
            // States saved before the rest-bounds feature have no home position;
            // treat the saved layout as the zone's resting place.
            if (!zone.HasRestBounds)
            {
                zone.CaptureRestBounds();
            }
            zone.ViewMode = zone.ViewMode is "List" or "Icons" ? zone.ViewMode : "Icons";
            zone.Items ??= [];

            zone.Items.RemoveAll(item => item is null);
            foreach (var item in zone.Items)
            {
                if (item.Id == Guid.Empty || !itemIds.Add(item.Id))
                {
                    item.Id = Guid.NewGuid();
                    itemIds.Add(item.Id);
                }
                item.Path ??= string.Empty;
                item.DisplayName = string.IsNullOrWhiteSpace(item.DisplayName)
                    ? Path.GetFileNameWithoutExtension(item.Path)
                    : item.DisplayName.Trim();
                item.CategoryKey = string.IsNullOrWhiteSpace(item.CategoryKey)
                    ? zone.CategoryKey
                    : item.CategoryKey;
            }
        }

        return state;
    }
}
