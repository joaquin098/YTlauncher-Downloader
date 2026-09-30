using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using YTMusicLauncher.Models;

namespace YTMusicLauncher.Services
{
    public class LibraryService
    {
        private string _libraryDirectory;

        public LibraryService()
        {
            var userMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            _libraryDirectory = Path.Combine(userMusic, "YTMusicLibrary");
            if (!Directory.Exists(_libraryDirectory))
            {
                Directory.CreateDirectory(_libraryDirectory);
            }
        }

        public string LibraryDirectory => _libraryDirectory;

        /// <summary>
        /// Loads tracks from disk instantly. Returns in 0ms without waiting for network API requests.
        /// </summary>
        public async Task<List<MediaTrack>> LoadTracksAsync()
        {
            return await Task.Run(() => LoadTracksFromDisk());
        }

        private List<MediaTrack> LoadTracksFromDisk()
        {
            var tracks = new List<MediaTrack>();
            if (!Directory.Exists(_libraryDirectory)) return tracks;

            var supportedExtensions = new[] { ".mp3", ".m4a", ".flac", ".wav", ".ogg" };
            var files = Directory.GetFiles(_libraryDirectory, "*.*", SearchOption.AllDirectories)
                                 .Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                 .ToList();

            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    var relPath = Path.GetRelativePath(_libraryDirectory, file);
                    var parts = relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var playlist = parts.Length > 1 ? parts[0] : "General";

                    var track = new MediaTrack
                    {
                        FilePath = file,
                        FileSizeBytes = fileInfo.Length,
                        DateAdded = fileInfo.CreationTime,
                        PlaylistName = playlist,
                        Title = Path.GetFileNameWithoutExtension(file),
                        Album = playlist != "General" ? playlist : "Sencillo",
                        Genre = "Sin género"
                    };

                    // Clean title if ending with [videoID]
                    if (track.Title.Contains('[') && track.Title.EndsWith("]"))
                    {
                        var idx = track.Title.LastIndexOf('[');
                        if (idx > 0)
                            track.Title = track.Title.Substring(0, idx).Trim();
                    }

                    // Read ID3/tags
                    string rawGenre = "";
                    try
                    {
                        using var tagFile = TagLib.File.Create(file);

                        if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title))
                            track.Title = tagFile.Tag.Title;

                        if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer))
                            track.Artist = tagFile.Tag.FirstPerformer;
                        else if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstAlbumArtist))
                            track.Artist = tagFile.Tag.FirstAlbumArtist;

                        if (!string.IsNullOrWhiteSpace(tagFile.Tag.Album))
                            track.Album = tagFile.Tag.Album;
                        else if (playlist != "General")
                            track.Album = playlist;

                        if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstGenre))
                            rawGenre = tagFile.Tag.FirstGenre.Trim();

                        track.Duration = tagFile.Properties.Duration;

                        if (tagFile.Tag.Pictures.Length > 0)
                        {
                            var pic = tagFile.Tag.Pictures[0];
                            var stream = new MemoryStream(pic.Data.Data);
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.StreamSource = stream;
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.EndInit();
                            bmp.Freeze();
                            track.Thumbnail = bmp;
                        }
                    }
                    catch { }

                    // Check fast local cache or embedded tag first
                    var cached = GenreService.GetCachedGenre(file, track.Artist, track.Title);
                    if (!string.IsNullOrWhiteSpace(cached))
                    {
                        track.Genre = cached;
                    }
                    else if (!string.IsNullOrWhiteSpace(rawGenre) &&
                             !rawGenre.Equals("Music", StringComparison.OrdinalIgnoreCase) &&
                             !rawGenre.Equals("General", StringComparison.OrdinalIgnoreCase) &&
                             !rawGenre.Equals("Other", StringComparison.OrdinalIgnoreCase) &&
                             !rawGenre.StartsWith("Mix", StringComparison.OrdinalIgnoreCase))
                    {
                        track.Genre = rawGenre;
                    }

                    tracks.Add(track);
                }
                catch { }
            }

            return tracks.OrderByDescending(t => t.DateAdded).ToList();
        }

        /// <summary>
        /// Non-blocking background task that fetches missing genres using iTunes API and updates the UI live.
        /// Saves fetched genres directly to disk and memory cache.
        /// </summary>
        public async Task EnrichMissingGenresAsync(List<MediaTrack> tracks, Action<MediaTrack>? onTrackUpdated = null)
        {
            var unclassified = tracks.Where(t => string.IsNullOrWhiteSpace(t.Genre) || t.Genre == "Sin género").ToList();
            if (unclassified.Count == 0) return;

            foreach (var track in unclassified)
            {
                try
                {
                    var genre = await GenreService.FetchAndSaveGenreAsync(track);
                    if (!string.IsNullOrWhiteSpace(genre) && genre != track.Genre)
                    {
                        track.Genre = genre;
                        onTrackUpdated?.Invoke(track);
                    }
                }
                catch { }
            }
        }

        public async Task<bool> CreatePlaylistWithTracksAsync(string playlistName, List<MediaTrack> selectedTracks)
        {
            if (string.IsNullOrWhiteSpace(playlistName)) return false;

            return await Task.Run(() =>
            {
                try
                {
                    // Clean playlist folder name
                    var cleanName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
                    var playlistDir = Path.Combine(_libraryDirectory, cleanName);
                    if (!Directory.Exists(playlistDir))
                    {
                        Directory.CreateDirectory(playlistDir);
                    }

                    foreach (var track in selectedTracks)
                    {
                        if (File.Exists(track.FilePath))
                        {
                            var destFileName = Path.GetFileName(track.FilePath);
                            var destPath = Path.Combine(playlistDir, destFileName);
                            if (!File.Exists(destPath))
                            {
                                File.Copy(track.FilePath, destPath, overwrite: true);
                            }
                        }
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }

        public bool DeleteTrack(MediaTrack track)
        {
            try
            {
                if (File.Exists(track.FilePath))
                {
                    File.Delete(track.FilePath);
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
