using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace SMBot.Services;

public record NowPlaying(string Title, string Artist, string App, bool IsPlaying, BitmapImage? Cover)
{
    public string Subtitle => Artist.Length > 0 ? $"{Artist} · {App}" : App;
}

// What's playing in Spotify, through Windows' own media controls (the same ones behind the volume pop-up),
// with no Spotify login and nothing leaving the PC. Other players (browsers, streams, VLC) are ignored on purpose.
public static class MusicService
{
    static GlobalSystemMediaTransportControlsSessionManager? _manager;
    static string _lastKey = "";
    static BitmapImage? _cover;

    public static NowPlaying? Current { get; private set; }

    // Raised when the song changes (not on pause/play)
    public static event Action<NowPlaying>? TrackChanged;
    // Raised on any change, including play/pause
    public static event Action? Changed;

    public static async Task StartAsync()
    {
        try { _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception) { _manager = null; }   // media controls not available: music features just stay hidden
    }

    // Spotify's media session, or null when Spotify isn't open
    static GlobalSystemMediaTransportControlsSession? PickSession() =>
        _manager?.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));

    // Called every second by the bot
    public static async Task RefreshAsync()
    {
        NowPlaying? now = null;
        try
        {
            var session = PickSession();
            if (session is not null)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (!string.IsNullOrWhiteSpace(props?.Title))
                {
                    var key = $"{props.Title}|{props.Artist}|{session.SourceAppUserModelId}";
                    if (key != _lastKey) _cover = await LoadCover(props.Thumbnail);
                    now = new NowPlaying(props.Title, props.Artist ?? "", AppName(session.SourceAppUserModelId), playing, _cover);
                }
            }
        }
        catch (Exception) { /* the player closed mid-read */ }

        var newKey = now is null ? "" : $"{now.Title}|{now.Artist}|{now.App}";
        var trackChanged = newKey != _lastKey;
        var stateChanged = trackChanged || now?.IsPlaying != Current?.IsPlaying;
        _lastKey = newKey;
        Current = now;
        if (trackChanged && now is not null) TrackChanged?.Invoke(now);
        if (stateChanged) Changed?.Invoke();
    }

    static async Task<BitmapImage?> LoadCover(Windows.Storage.Streams.IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var winStream = await thumbnail.OpenReadAsync();
            using var source = winStream.AsStreamForRead();
            var copy = new MemoryStream();
            await source.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    static string AppName(string appId)
    {
        var id = appId.ToLowerInvariant();
        return id.Contains("spotify") ? "Spotify" : id.Contains("msedge") ? "Edge" : id.Contains("chrome") ? "Chrome"
            : id.Contains("firefox") ? "Firefox" : id.Contains("vlc") ? "VLC" : id.Contains("zunemusic") ? "Media Player"
            : Path.GetFileNameWithoutExtension(appId.Split('!')[0]);
    }

    public static async Task PlayPauseAsync() { if (PickSession() is { } s) await s.TryTogglePlayPauseAsync(); }
    public static async Task NextAsync() { if (PickSession() is { } s) await s.TrySkipNextAsync(); }
    public static async Task PreviousAsync() { if (PickSession() is { } s) await s.TrySkipPreviousAsync(); }

    // ---- How loud Spotify itself is right now (0 to 1), for dancing ----
    // Windows Core Audio gives each app its own audio session with a peak meter, so a stream or video playing at the
    // same time doesn't count. All local.

    static IAudioMeterInformation? _meter;
    static DateTime _meterAt;

    public static float Level()
    {
        try
        {
            // Spotify can start a new audio session (new song, device change), so look it up again every few seconds
            if (_meter is null || DateTime.Now - _meterAt > TimeSpan.FromSeconds(5))
            {
                _meter = FindSpotifyMeter();
                _meterAt = DateTime.Now;
            }
            if (_meter is null) return 0;
            _meter.GetPeakValue(out var peak);
            return peak;
        }
        catch (Exception)
        {
            _meter = null;
            return 0;
        }
    }

    static IAudioMeterInformation? FindSpotifyMeter()
    {
        var spotify = System.Diagnostics.Process.GetProcessesByName("Spotify").Select(p => (uint)p.Id).ToHashSet();
        if (spotify.Count == 0) return null;

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device);
        var iid = typeof(IAudioSessionManager2).GUID;
        device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var managerObject);
        var manager = (IAudioSessionManager2)managerObject;
        manager.GetSessionEnumerator(out var sessions);
        sessions.GetCount(out var count);

        IAudioMeterInformation? best = null;
        for (var i = 0; i < count; i++)
        {
            sessions.GetSession(i, out var session);
            session.GetProcessId(out var pid);
            if (!spotify.Contains(pid)) continue;
            best = (IAudioMeterInformation)session;   // the session object also answers as its own peak meter
            session.GetState(out var state);
            if (state == 1 /* active */) return best;
        }
        return best;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);   // from IAudioSessionManager
        int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);      // from IAudioSessionManager
        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    // Methods are listed in vtable order: IAudioSessionControl's first, then IAudioSessionControl2's
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(IntPtr name, IntPtr eventContext);
        int GetIconPath(out IntPtr path);
        int SetIconPath(IntPtr path, IntPtr eventContext);
        int GetGroupingParam(out Guid groupingId);
        int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out uint pid);
    }
}
