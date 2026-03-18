using Velopack;
using Velopack.Sources;

namespace SqlVersionControl.Services;

public class UpdateService
{
    private readonly UpdateManager _updateManager;
    private UpdateInfo? _pendingUpdate;

    public string? AvailableVersion => _pendingUpdate?.TargetFullRelease?.Version?.ToString();
    public bool HasPendingUpdate => _pendingUpdate != null;
    public bool IsInstalled => _updateManager.IsInstalled;

    public UpdateService()
    {
        var source = new GithubSource(
            "https://github.com/omervaner/SqlVersionControl",
            accessToken: null,
            prerelease: false);
        _updateManager = new UpdateManager(source);
    }

    /// <summary>Check GitHub Releases for a newer version. Works even if not Velopack-installed.</summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        try
        {
            _pendingUpdate = await _updateManager.CheckForUpdatesAsync();
            return _pendingUpdate != null;
        }
        catch
        {
            return false; // Network error, GitHub down — fail silently
        }
    }

    /// <summary>Download the update. Progress callback receives 0-100.</summary>
    public async Task<bool> DownloadUpdateAsync(Action<int>? progressCallback = null)
    {
        if (_pendingUpdate == null) return false;
        try
        {
            await _updateManager.DownloadUpdatesAsync(
                _pendingUpdate,
                progress => progressCallback?.Invoke(progress));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Apply downloaded update and restart the app. Never returns.</summary>
    public void ApplyUpdateAndRestart()
    {
        if (_pendingUpdate == null) return;
        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
