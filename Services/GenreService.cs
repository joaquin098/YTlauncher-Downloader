using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using YTMusicLauncher.Models;

namespace YTMusicLauncher.Services
{
    /// <summary>
    /// Fast Genre Service powered by iTunes Search API, Deezer API, ID3 Tag persistence, and Local JSON Cache.
    /// Non-blocking, instant response with persistent disk caching.
    /// </summary>
    public class GenreService
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private static readonly string _cacheFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YTMusicLauncher",
            "genre_cache.json"
        );

        private static ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static bool _cacheLoaded = false;

        public static void EnsureCacheLoaded()
        {
            if (_cacheLoaded) return;
            _cacheLoaded = true;

            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    var json = File.ReadAllText(_cacheFilePath);
                    var dict = JsonSerializer.Deserialize<ConcurrentDictionary<string, string>>(json);
                    if (dict != null)
                    {
                        _cache = new ConcurrentDictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch { }
        }

        private static void SaveCache()
        {
            try
            {
                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var json = JsonSerializer.Serialize(_cache);
                File.WriteAllText(_cacheFilePath, json);
            }
            catch { }
        }

        public static string? GetCachedGenre(string filePath, string artist, string title)
        {
            EnsureCacheLoaded();
            var key = GetCacheKey(artist, title);
            if (_cache.TryGetValue(key, out var cachedGenre) && !string.IsNullOrWhiteSpace(cachedGenre))
            {
                return cachedGenre;
            }
            return null;
        }

        public static async Task<string> FetchAndSaveGenreAsync(MediaTrack track)
        {
            EnsureCacheLoaded();
            var key = GetCacheKey(track.Artist, track.Title);

            // 1. Check local cache memory/file
            if (_cache.TryGetValue(key, out var cached) && !string.IsNullOrWhiteSpace(cached) && cached != "Sin género")
            {
                return cached;
            }

            // 2. Query iTunes Search API (Super fast, no rate limiting, accurate genres)
            string? genre = await QueryiTunesGenreAsync(track.Artist, track.Title);

            // 3. Fallback: Query Deezer API
            if (string.IsNullOrWhiteSpace(genre))
            {
                genre = await QueryDeezerGenreAsync(track.Artist, track.Title);
            }

            // 4. Fallback: Keyword classifier
            if (string.IsNullOrWhiteSpace(genre))
            {
                genre = FallbackKeywordClassifier(track.Artist, track.Title, track.Album);
            }

            if (string.IsNullOrWhiteSpace(genre))
            {
                genre = "Sin género";
            }

            // Save to memory cache & disk cache
            _cache[key] = genre;
            SaveCache();

            // Save directly to audio file ID3 tag so it stays with the file permanently!
            WriteGenreToAudioFile(track.FilePath, genre);

            return genre;
        }

        private static async Task<string?> QueryiTunesGenreAsync(string artist, string title)
        {
            try
            {
                var query = Uri.EscapeDataString($"{artist} {title}".Trim());
                var url = $"https://itunes.apple.com/search?term={query}&entity=song&limit=1";

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var results = doc.RootElement.GetProperty("results");

                if (results.GetArrayLength() > 0)
                {
                    var firstResult = results[0];
                    if (firstResult.TryGetProperty("primaryGenreName", out var genreElem))
                    {
                        var rawGenre = genreElem.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(rawGenre) && !rawGenre.Equals("Music", StringComparison.OrdinalIgnoreCase))
                        {
                            return TranslateGenre(rawGenre);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static async Task<string?> QueryDeezerGenreAsync(string artist, string title)
        {
            try
            {
                var query = Uri.EscapeDataString($"artist:\"{artist}\" track:\"{title}\"");
                var url = $"https://api.deezer.com/search?q={query}&limit=1";

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");

                if (data.GetArrayLength() > 0)
                {
                    // Deezer track found
                    return null;
                }
            }
            catch { }
            return null;
        }

        private static void WriteGenreToAudioFile(string filePath, string genre)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                tagFile.Tag.Genres = new[] { genre };
                tagFile.Save();
            }
            catch { }
        }

        private static string GetCacheKey(string artist, string title)
        {
            return $"{artist.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}";
        }

        private static string TranslateGenre(string rawGenre)
        {
            var g = rawGenre.Trim();
            if (g.Equals("Alternative", StringComparison.OrdinalIgnoreCase) || g.Equals("Rock", StringComparison.OrdinalIgnoreCase) || g.Equals("Hard Rock", StringComparison.OrdinalIgnoreCase))
                return "Rock / Alternative";
            if (g.Equals("Hip-Hop/Rap", StringComparison.OrdinalIgnoreCase) || g.Equals("Rap", StringComparison.OrdinalIgnoreCase) || g.Equals("Hip-Hop", StringComparison.OrdinalIgnoreCase))
                return "Hip-Hop / Rap";
            if (g.Equals("Pop", StringComparison.OrdinalIgnoreCase) || g.Equals("K-Pop", StringComparison.OrdinalIgnoreCase))
                return "Pop";
            if (g.Equals("Latin", StringComparison.OrdinalIgnoreCase) || g.Equals("Urbano latino", StringComparison.OrdinalIgnoreCase) || g.Equals("Reggaeton y Latin Urban", StringComparison.OrdinalIgnoreCase))
                return "Latino / Urbano";
            if (g.Equals("Dance", StringComparison.OrdinalIgnoreCase) || g.Equals("Electronic", StringComparison.OrdinalIgnoreCase) || g.Equals("House", StringComparison.OrdinalIgnoreCase) || g.Equals("Techno", StringComparison.OrdinalIgnoreCase))
                return "Electrónica";
            if (g.Equals("Jazz", StringComparison.OrdinalIgnoreCase) || g.Equals("Blues", StringComparison.OrdinalIgnoreCase) || g.Equals("Soul", StringComparison.OrdinalIgnoreCase))
                return "Jazz / Soul";
            if (g.Equals("Classical", StringComparison.OrdinalIgnoreCase))
                return "Clásica";
            if (g.Equals("Soundtrack", StringComparison.OrdinalIgnoreCase) || g.Equals("Anime", StringComparison.OrdinalIgnoreCase))
                return "Soundtrack / Anime";

            return g;
        }

        public static string FallbackKeywordClassifier(string artist, string title, string album)
        {
            var fullText = $"{title} {artist} {album}".ToLowerInvariant();

            if (fullText.Contains("metal") || fullText.Contains("rock") || fullText.Contains("punk") || fullText.Contains("indie"))
                return "Rock / Alternative";
            if (fullText.Contains("k-pop") || fullText.Contains("kpop"))
                return "K-Pop";
            if (fullText.Contains("pop"))
                return "Pop";
            if (fullText.Contains("rap") || fullText.Contains("hip hop") || fullText.Contains("hip-hop") || fullText.Contains("trap"))
                return "Hip-Hop / Rap";
            if (fullText.Contains("reggaeton") || fullText.Contains("cumbia") || fullText.Contains("salsa") || fullText.Contains("bachata") || fullText.Contains("latin") || fullText.Contains("urbano"))
                return "Latino / Urbano";
            if (fullText.Contains("electro") || fullText.Contains("house") || fullText.Contains("techno") || fullText.Contains("edm") || fullText.Contains("synthwave") || fullText.Contains("dance"))
                return "Electrónica";
            if (fullText.Contains("lofi") || fullText.Contains("lo-fi") || fullText.Contains("chill") || fullText.Contains("ambient"))
                return "Lo-Fi / Chill";
            if (fullText.Contains("jazz") || fullText.Contains("blues") || fullText.Contains("soul") || fullText.Contains("funk"))
                return "Jazz / Soul";
            if (fullText.Contains("classical") || fullText.Contains("clásica") || fullText.Contains("orchestra") || fullText.Contains("symphony"))
                return "Clásica";
            if (fullText.Contains("soundtrack") || fullText.Contains("ost") || fullText.Contains("anime") || fullText.Contains("opening") || fullText.Contains("ending"))
                return "Soundtrack / Anime";

            return "Sin género";
        }
    }
}
