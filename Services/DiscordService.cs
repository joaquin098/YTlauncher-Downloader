using DiscordRPC;
using DiscordRPC.Logging;
using System;

namespace YTMusicLauncher.Services
{
    public class DiscordService : IDisposable
    {
        private const string AppId = "1554887559425822840";
        private DiscordRpcClient? _client;
        private bool _isReady = false;
        private RichPresence? _queuedPresence;

        public void Initialize()
        {
            try
            {
                if (_client != null && !_client.IsDisposed) return;

                _client = new DiscordRpcClient(AppId, autoEvents: true)
                {
                    Logger = new NullLogger()
                };

                _client.OnReady += (sender, e) =>
                {
                    _isReady = true;
                    if (_queuedPresence != null)
                    {
                        try { _client.SetPresence(_queuedPresence); } catch { }
                    }
                };

                _client.OnConnectionFailed += (sender, e) => { _isReady = false; };
                _client.OnError += (sender, e) => { };

                _client.Initialize();
            }
            catch { }
        }

        public void UpdateLocalTrack(string title, string artist, double currentSeconds, double totalSeconds)
        {
            if (string.IsNullOrWhiteSpace(title)) return;

            var startTime = DateTime.UtcNow.AddSeconds(-currentSeconds);
            var endTime = totalSeconds > 0 ? DateTime.UtcNow.AddSeconds(totalSeconds - currentSeconds) : (DateTime?)null;

            var presence = new RichPresence
            {
                Type = ActivityType.Listening,
                Details = TrimText(title, 128),
                State = TrimText(string.IsNullOrWhiteSpace(artist) ? "Biblioteca Local" : $"de {artist}", 128),
                Timestamps = endTime.HasValue
                    ? new Timestamps(startTime, endTime.Value)
                    : new Timestamps(startTime),
                Assets = new Assets
                {
                    LargeImageText = "YT Music Launcher - Biblioteca Local"
                }
            };

            SetOrQueuePresence(presence);
        }

        public void UpdateLocalPaused(string title, string artist)
        {
            if (string.IsNullOrWhiteSpace(title)) return;

            var presence = new RichPresence
            {
                Type = ActivityType.Listening,
                Details = TrimText(title, 128),
                State = TrimText(string.IsNullOrWhiteSpace(artist) ? "En pausa" : $"de {artist} (En pausa)", 128),
                Assets = new Assets
                {
                    LargeImageText = "YT Music Launcher"
                }
            };

            SetOrQueuePresence(presence);
        }

        public void UpdateYtmTrack(string title, string artist, string coverUrl, string songUrl, double currentSeconds, double totalSeconds, bool isPlaying)
        {
            if (string.IsNullOrWhiteSpace(title)) return;

            if (!isPlaying)
            {
                UpdateLocalPaused(title, artist);
                return;
            }

            var startTime = DateTime.UtcNow.AddSeconds(-currentSeconds);
            var endTime = totalSeconds > 0 ? DateTime.UtcNow.AddSeconds(totalSeconds - currentSeconds) : (DateTime?)null;

            Button[]? buttons = null;
            if (!string.IsNullOrWhiteSpace(songUrl) && songUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                buttons = new Button[]
                {
                    new Button { Label = "Escuchar Cancion", Url = songUrl }
                };
            }

            var presence = new RichPresence
            {
                Type = ActivityType.Listening,
                Details = TrimText(title, 128),
                State = TrimText(string.IsNullOrWhiteSpace(artist) ? "YouTube Music" : $"de {artist}", 128),
                Timestamps = endTime.HasValue
                    ? new Timestamps(startTime, endTime.Value)
                    : new Timestamps(startTime),
                Assets = new Assets
                {
                    LargeImageKey = string.IsNullOrWhiteSpace(coverUrl) ? null : coverUrl,
                    LargeImageText = TrimText(title, 128)
                },
                Buttons = buttons
            };

            SetOrQueuePresence(presence);
        }

        private void SetOrQueuePresence(RichPresence presence)
        {
            _queuedPresence = presence;

            if (_client == null || _client.IsDisposed)
            {
                Initialize();
            }

            if (_client != null && _isReady)
            {
                try
                {
                    _client.SetPresence(presence);
                }
                catch { }
            }
        }

        public void ClearPresence()
        {
            _queuedPresence = null;
            if (_client != null && !_client.IsDisposed)
            {
                try { _client.ClearPresence(); } catch { }
            }
        }

        public void Dispose()
        {
            try
            {
                _client?.ClearPresence();
                _client?.Dispose();
            }
            catch { }
        }

        private static string TrimText(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Length <= maxLen ? text : text[..maxLen];
        }
    }
}
