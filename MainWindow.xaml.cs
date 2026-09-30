using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YTMusicLauncher.Models;
using YTMusicLauncher.Services;

namespace YTMusicLauncher
{
    public enum ActivePlayerMode
    {
        Local,
        YouTubeMusic
    }

    public partial class MainWindow : Window
    {
        private readonly AdBlockerService _adBlockerService = new();
        private readonly YtDlpService _ytDlpService = new();
        private readonly LibraryService _libraryService = new();
        private readonly DiscordService _discordService = new();

        private ObservableCollection<DownloadItem> _downloads = new();
        private List<MediaTrack> _allTracks = new();
        private List<MediaTrack> _currentPlaylistTracks = new();
        private ObservableCollection<SelectableTrack> _modalSelectableTracks = new();

        private readonly MediaPlayer _mediaPlayer = new();
        private readonly DispatcherTimer _timer = new();
        private MediaTrack? _currentTrack;
        private bool _isUserSeeking = false;

        private ActivePlayerMode _activePlayerMode = ActivePlayerMode.Local;
        private double _ytmCurrentTime = 0;
        private double _ytmDuration = 0;
        private bool _ytmIsPlaying = false;
        private string _ytmCurrentCoverUrl = "";
        private string _lastDiscordTitle = "";
        private bool _lastDiscordPlaying = false;

        public MainWindow()
        {
            InitializeComponent();
            LstDownloads.ItemsSource = _downloads;

            _timer.Interval = TimeSpan.FromMilliseconds(400);
            _timer.Tick += OnTimerTick;

            _mediaPlayer.MediaEnded += (s, e) => OnPlayerNextClick(this, new RoutedEventArgs());

            Loaded += OnWindowLoaded;
            Closing += (s, e) => { _discordService.ClearPresence(); _discordService.Dispose(); };

            try
            {
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    Icon = new BitmapImage(new Uri(iconPath));
                }
            }
            catch { }

            // Start Discord Rich Presence (silently, Discord may not be running)
            _discordService.Initialize();
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "YTMusicLauncher",
                    "WebView2Data"
                );
                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await YtWebView.EnsureCoreWebView2Async(env);

                await _adBlockerService.InitializeAdBlockerAsync(YtWebView.CoreWebView2);
                YtWebView.WebMessageReceived += OnYtWebMessageReceived;

                YtWebView.Source = new Uri("https://music.youtube.com");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al inicializar WebView2: {ex.Message}", "YT Music Launcher Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            await _ytDlpService.EnsureYtDlpExistsAsync();
            await RefreshLibraryAsync();
        }

        private void OnYtWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.WebMessageAsJson;

                string json = raw;
                if (raw.StartsWith("\""))
                {
                    json = JsonSerializer.Deserialize<string>(raw) ?? raw;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp)) return;
                var msgType = typeProp.GetString();

                if (msgType == "ytm_key_left")
                {
                    SeekBySeconds(-5);
                    return;
                }
                if (msgType == "ytm_key_right")
                {
                    SeekBySeconds(5);
                    return;
                }

                // EVENT 1: User explicitly pressed Play in YouTube Music web player
                if (msgType == "ytm_user_play")
                {
                    if (_activePlayerMode == ActivePlayerMode.Local)
                    {
                        try { _mediaPlayer.Pause(); } catch { }
                    }
                    _activePlayerMode = ActivePlayerMode.YouTubeMusic;
                    return;
                }

                // EVENT 2: Periodic YTM State Update
                if (msgType == "ytm_state")
                {
                    string title       = root.TryGetProperty("title",       out var t)  ? t.GetString()  ?? "" : "";
                    string artist      = root.TryGetProperty("artist",      out var a)  ? a.GetString()  ?? "" : "";
                    string cover       = root.TryGetProperty("cover",       out var c)  ? c.GetString()  ?? "" : "";
                    string songUrl     = root.TryGetProperty("url",         out var u)  ? u.GetString()  ?? "" : "";
                    double currentTime = root.TryGetProperty("currentTime", out var ct) ? ct.GetDouble() : 0;
                    double duration    = root.TryGetProperty("duration",    out var d)  ? d.GetDouble()  : 0;
                    bool isPlaying     = root.TryGetProperty("isPlaying",   out var p)  && p.GetBoolean();

                    _ytmCurrentTime = currentTime;
                    _ytmDuration    = duration;
                    _ytmIsPlaying   = isPlaying;

                    // IF LOCAL PLAYER IS ACTIVE, DO NOT OVERWRITE LOCAL HUD!
                    if (_activePlayerMode == ActivePlayerMode.Local)
                    {
                        return;
                    }

                    // ONLY UPDATE HUD IF ACTIVE MODE IS YOUTUBE MUSIC
                    if (_activePlayerMode == ActivePlayerMode.YouTubeMusic)
                    {
                        PnlBottomPlayer.Visibility = Visibility.Visible;
                        LblPlayerTitle.Text  = string.IsNullOrWhiteSpace(title)  ? "YouTube Music" : title;
                        LblPlayerArtist.Text = string.IsNullOrWhiteSpace(artist) ? "YouTube Music" : artist;

                        UpdateCoverImage(cover, localBitmap: null);

                        BtnPlayerPlayPause.Content = isPlaying ? "⏸" : "▶";

                        if (!_isUserSeeking && duration > 0)
                        {
                            LblPlayerCurrentTime.Text = TimeSpan.FromSeconds(currentTime).ToString(@"mm\:ss");
                            LblPlayerTotalTime.Text   = TimeSpan.FromSeconds(duration).ToString(@"mm\:ss");
                            SldPlayerSeek.Value = (currentTime / duration) * 100.0;
                        }

                        // UPDATE DISCORD RICH PRESENCE
                        if (title != _lastDiscordTitle || isPlaying != _lastDiscordPlaying)
                        {
                            _lastDiscordTitle = title;
                            _lastDiscordPlaying = isPlaying;
                            _discordService.UpdateYtmTrack(title, artist, cover, songUrl, currentTime, duration, isPlaying);
                        }
                    }
                }
            }
            catch { }
        }

        private void UpdateCoverImage(string? coverUrl, BitmapImage? localBitmap = null)
        {
            try
            {
                if (localBitmap != null)
                {
                    ImgPlayerCover.Source = localBitmap;
                    _ytmCurrentCoverUrl = "";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(coverUrl) && coverUrl != _ytmCurrentCoverUrl)
                {
                    _ytmCurrentCoverUrl = coverUrl;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(coverUrl);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    ImgPlayerCover.Source = bmp;
                }
            }
            catch { }
        }

        #region TAB NAVIGATION

        private void OnTabYtMusicClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            ViewYtMusic.Visibility = Visibility.Visible;
            ViewLibrary.Visibility = Visibility.Collapsed;
            ViewDownloads.Visibility = Visibility.Collapsed;

            SetTabStyle(BtnTabYtMusic, true);
            SetTabStyle(BtnTabLibrary, false);
            SetTabStyle(BtnTabDownloads, false);
        }

        private async void OnTabLibraryClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            ViewYtMusic.Visibility = Visibility.Collapsed;
            ViewLibrary.Visibility = Visibility.Visible;
            ViewDownloads.Visibility = Visibility.Collapsed;

            SetTabStyle(BtnTabYtMusic, false);
            SetTabStyle(BtnTabLibrary, true);
            SetTabStyle(BtnTabDownloads, false);

            await RefreshLibraryAsync();
        }

        private void OnTabDownloadsClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            ViewYtMusic.Visibility = Visibility.Collapsed;
            ViewLibrary.Visibility = Visibility.Collapsed;
            ViewDownloads.Visibility = Visibility.Visible;

            SetTabStyle(BtnTabYtMusic, false);
            SetTabStyle(BtnTabLibrary, false);
            SetTabStyle(BtnTabDownloads, true);
        }

        private void SetTabStyle(Button btn, bool isActive)
        {
            if (isActive)
            {
                btn.Style = (Style)FindResource("ActiveNavTabButtonStyle");
            }
            else
            {
                btn.Style = (Style)FindResource("NavTabButtonStyle");
            }
        }

        #endregion

        #region QUICK DOWNLOAD & FOLDER ACTIONS

        private async void OnQuickDownloadSingleClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayAction();
            var currentUrl = GetActiveWebUrl();
            OnTabDownloadsClick(sender, e);
            TxtDownloadUrl.Text = currentUrl;
            TxtDownloadUrl.Foreground = new SolidColorBrush(Colors.White);

            await StartDownloadForUrl(currentUrl, singleTrackOnly: true);
        }

        private async void OnQuickDownloadPlaylistClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayAction();
            var currentUrl = GetActiveWebUrl();
            OnTabDownloadsClick(sender, e);
            TxtDownloadUrl.Text = currentUrl;
            TxtDownloadUrl.Foreground = new SolidColorBrush(Colors.White);

            await StartDownloadForUrl(currentUrl, singleTrackOnly: false);
        }

        private string GetActiveWebUrl()
        {
            var currentUrl = YtWebView.Source?.ToString() ?? "https://music.youtube.com";
            if (string.IsNullOrWhiteSpace(currentUrl) || currentUrl == "about:blank")
            {
                currentUrl = "https://music.youtube.com";
            }
            return currentUrl;
        }

        private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            try
            {
                Process.Start("explorer.exe", _libraryService.LibraryDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo abrir la carpeta: {ex.Message}");
            }
        }

        #endregion

        #region DOWNLOADER VIEW & PLAYLIST TRACK DETAILS

        private async void OnStartDownloadClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayAction();
            var url = TxtDownloadUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("Pega aquí"))
            {
                MessageBox.Show("Por favor ingresa un enlace válido de YouTube o YouTube Music.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await StartDownloadForUrl(url, singleTrackOnly: false);
        }

        private async Task StartDownloadForUrl(string url, bool singleTrackOnly)
        {
            var selectedFormatItem = CmbFormat.SelectedItem as ComboBoxItem;
            var formatStr = selectedFormatItem?.Content?.ToString() ?? "MP3 (320 kbps)";

            var downloadItem = new DownloadItem
            {
                Url = url,
                QualityFormat = formatStr,
                OutputDirectory = _libraryService.LibraryDirectory,
                SingleTrackOnly = singleTrackOnly,
                Title = singleTrackOnly ? "Descargando canción individual..." : "Obteniendo información de la playlist..."
            };

            _downloads.Insert(0, downloadItem);

            await _ytDlpService.DownloadAsync(downloadItem, async () =>
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    await RefreshLibraryAsync();
                });
            });
        }

        private void OnToggleExpandPlaylistClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            if (sender is Button btn && btn.DataContext is DownloadItem item)
            {
                item.IsExpanded = !item.IsExpanded;
            }
        }

        private void OnSkipSubItemClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayCancel();
            if (sender is Button btn && btn.DataContext is SubDownloadItem subItem)
            {
                _ytDlpService.CancelSubItem(subItem);
            }
        }

        private void OnCancelFromHereClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayCancel();
            if (sender is Button btn && btn.DataContext is SubDownloadItem targetSubItem)
            {
                var parentItem = _downloads.FirstOrDefault(d => d.SubItems.Contains(targetSubItem));
                if (parentItem != null)
                {
                    var targetIndex = targetSubItem.Index;
                    var remaining = parentItem.SubItems.Where(s => s.Index >= targetIndex).ToList();

                    foreach (var sub in remaining)
                    {
                        if (sub.CanSkip)
                        {
                            _ytDlpService.CancelSubItem(sub);
                        }
                        sub.Status = DownloadStatus.Canceled;
                        sub.StatusText = "Detenido desde aquí";
                    }

                    parentItem.StatusText = $"Detenido desde la canción #{targetIndex}";
                }
            }
        }

        private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayCancel();
            if (sender is Button btn && btn.DataContext is DownloadItem item)
            {
                _ytDlpService.CancelDownload(item);
            }
        }

        private void OnClearCompletedDownloadsClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            var completed = _downloads.Where(d => d.Status == DownloadStatus.Completed || d.Status == DownloadStatus.Canceled || d.Status == DownloadStatus.Failed).ToList();
            foreach (var item in completed)
            {
                _downloads.Remove(item);
            }
        }

        private void OnUrlGotFocus(object sender, RoutedEventArgs e)
        {
            if (TxtDownloadUrl.Text.StartsWith("Pega aquí"))
            {
                TxtDownloadUrl.Text = "";
                TxtDownloadUrl.Foreground = new SolidColorBrush(Colors.White);
            }
        }

        private void OnUrlLostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtDownloadUrl.Text))
            {
                TxtDownloadUrl.Text = "Pega aquí la URL de la canción o playlist...";
                TxtDownloadUrl.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            }
        }

        #endregion

        #region BIBLIOTECA (LIBRARY) VIEW

        private async Task RefreshLibraryAsync()
        {
            _allTracks = await _libraryService.LoadTracksAsync();
            LblLibraryStats.Text = $"{_allTracks.Count} Canción{(_allTracks.Count == 1 ? "" : "es")}";

            // Populate Playlists Sidebar
            var playlists = new List<string> { "🎵 Todas las canciones" };
            var subfolders = _allTracks.Select(t => t.PlaylistName).Distinct().Where(p => p != "General").OrderBy(p => p);
            playlists.AddRange(subfolders);

            var selectedPlaylistIndex = LstPlaylists?.SelectedIndex ?? 0;
            if (LstPlaylists != null)
            {
                LstPlaylists.ItemsSource = playlists;
                LstPlaylists.SelectedIndex = selectedPlaylistIndex >= 0 && selectedPlaylistIndex < playlists.Count ? selectedPlaylistIndex : 0;
            }

            UpdateGenreComboBox();
            FilterLibrary();

            // Background enrichment using fast iTunes Search API + disk persistence
            _ = Task.Run(async () =>
            {
                await _libraryService.EnrichMissingGenresAsync(_allTracks, track =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        UpdateGenreComboBox();
                        FilterLibrary();
                    });
                });
            });
        }

        private void UpdateGenreComboBox()
        {
            if (CmbGenreFilter == null || _allTracks == null) return;

            var selectedGenre = (CmbGenreFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos los Géneros";
            var genres = new List<string> { "Todos los Géneros" };
            var trackGenres = _allTracks.Select(t => t.Genre).Where(g => !string.IsNullOrWhiteSpace(g) && g != "Sin género").Distinct().OrderBy(g => g);
            genres.AddRange(trackGenres);

            CmbGenreFilter.Items.Clear();
            int selectIdx = 0;
            for (int i = 0; i < genres.Count; i++)
            {
                var item = new ComboBoxItem { Content = genres[i] };
                CmbGenreFilter.Items.Add(item);
                if (genres[i] == selectedGenre) selectIdx = i;
            }
            CmbGenreFilter.SelectedIndex = selectIdx;
        }

        private async void OnRefreshLibraryClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            await RefreshLibraryAsync();
        }

        private void OnPlaylistSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FilterLibrary();
        }

        private void OnGenreFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            FilterLibrary();
        }

        private void OnSortByChanged(object sender, SelectionChangedEventArgs e)
        {
            FilterLibrary();
        }

        private void FilterLibrary()
        {
            if (_allTracks == null || LstPlaylists == null || DgLibrary == null || TxtSearchLibrary == null) return;

            var filtered = _allTracks.AsEnumerable();

            // 1. Filter by playlist
            var selectedPlaylist = LstPlaylists.SelectedItem as string;
            if (!string.IsNullOrEmpty(selectedPlaylist) && selectedPlaylist != "🎵 Todas las canciones")
            {
                filtered = filtered.Where(t => t.PlaylistName.Equals(selectedPlaylist, StringComparison.OrdinalIgnoreCase));
            }

            // 2. Filter by genre
            if (CmbGenreFilter?.SelectedItem is ComboBoxItem genreItem)
            {
                var genreStr = genreItem.Content?.ToString();
                if (!string.IsNullOrEmpty(genreStr) && genreStr != "Todos los Géneros")
                {
                    filtered = filtered.Where(t => t.Genre.Equals(genreStr, StringComparison.OrdinalIgnoreCase));
                }
            }

            // 3. Filter by search string
            var query = TxtSearchLibrary.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(query) && !query.StartsWith("Buscar canción"))
            {
                filtered = filtered.Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                               t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                               t.Album.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                               t.Genre.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            // 4. Apply sorting
            if (CmbSortBy?.SelectedItem is ComboBoxItem sortItem)
            {
                var sortOption = sortItem.Content?.ToString() ?? "";
                filtered = sortOption switch
                {
                    "Fecha (Más Antiguos)" => filtered.OrderBy(t => t.DateAdded),
                    "Título (A-Z)" => filtered.OrderBy(t => t.Title),
                    "Artista (A-Z)" => filtered.OrderBy(t => t.Artist),
                    "Álbum (A-Z)" => filtered.OrderBy(t => t.Album),
                    "Género (A-Z)" => filtered.OrderBy(t => t.Genre),
                    "Duración" => filtered.OrderByDescending(t => t.Duration),
                    _ => filtered.OrderByDescending(t => t.DateAdded) // "Fecha (Más Recientes)"
                };
            }

            _currentPlaylistTracks = filtered.ToList();
            DgLibrary.ItemsSource = _currentPlaylistTracks;

            // Highlight current track if in current view
            if (_currentTrack != null && _currentPlaylistTracks.Contains(_currentTrack))
            {
                DgLibrary.SelectedItem = _currentTrack;
            }
        }

        private void OnSearchGotFocus(object sender, RoutedEventArgs e)
        {
            if (TxtSearchLibrary.Text.StartsWith("Buscar canción"))
            {
                TxtSearchLibrary.Text = "";
                TxtSearchLibrary.Foreground = new SolidColorBrush(Colors.White);
            }
        }

        private void OnSearchLostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtSearchLibrary.Text))
            {
                TxtSearchLibrary.Text = "Buscar canción, artista o álbum...";
                TxtSearchLibrary.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            }
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            FilterLibrary();
        }

        private void OnSongSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void OnSongDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DgLibrary.SelectedItem is MediaTrack track)
            {
                PlayTrack(track);
            }
        }

        private void OnPlayTrackClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is MediaTrack track)
            {
                PlayTrack(track);
            }
        }

        private void OnOpenTrackLocationClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            if (sender is Button btn && btn.DataContext is MediaTrack track)
            {
                try
                {
                    Process.Start("explorer.exe", $"/select,\"{track.FilePath}\"");
                }
                catch { }
            }
        }

        private async void OnDeleteTrackClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayCancel();
            if (sender is Button btn && btn.DataContext is MediaTrack track)
            {
                var result = MessageBox.Show($"¿Estás seguro de eliminar '{track.Title}'?", "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    _libraryService.DeleteTrack(track);
                    await RefreshLibraryAsync();
                }
            }
        }

        #endregion

        #region CREATE PLAYLIST MODAL WITH CHECKBOXES

        private void OnOpenCreatePlaylistModalClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            _modalSelectableTracks.Clear();
            foreach (var track in _allTracks)
            {
                _modalSelectableTracks.Add(new SelectableTrack(track, isSelected: false));
            }

            DgModalTracks.ItemsSource = _modalSelectableTracks;
            PnlCreatePlaylistModal.Visibility = Visibility.Visible;
        }

        private void OnCloseNewPlaylistModalClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            PnlCreatePlaylistModal.Visibility = Visibility.Collapsed;
        }

        private void OnSelectAllModalClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            foreach (var item in _modalSelectableTracks)
            {
                item.IsSelected = true;
            }
        }

        private void OnDeselectAllModalClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();
            foreach (var item in _modalSelectableTracks)
            {
                item.IsSelected = false;
            }
        }

        private void OnModalSearchGotFocus(object sender, RoutedEventArgs e)
        {
            if (TxtModalSearch.Text.StartsWith("Filtrar canciones"))
            {
                TxtModalSearch.Text = "";
                TxtModalSearch.Foreground = new SolidColorBrush(Colors.White);
            }
        }

        private void OnModalSearchLostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtModalSearch.Text))
            {
                TxtModalSearch.Text = "Filtrar canciones...";
                TxtModalSearch.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            }
        }

        private void OnModalSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_modalSelectableTracks == null || DgModalTracks == null) return;
            var query = TxtModalSearch.Text?.Trim();

            if (string.IsNullOrWhiteSpace(query) || query.StartsWith("Filtrar canciones"))
            {
                DgModalTracks.ItemsSource = _modalSelectableTracks;
            }
            else
            {
                var filtered = _modalSelectableTracks.Where(s => s.Track.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                                                 s.Track.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                                                 s.Track.Album.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
                DgModalTracks.ItemsSource = filtered;
            }
        }

        private async void OnSaveNewPlaylistClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayAction();
            var playlistName = TxtNewPlaylistName.Text.Trim();
            if (string.IsNullOrWhiteSpace(playlistName))
            {
                MessageBox.Show("Por favor ingresa un nombre para la playlist.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selected = _modalSelectableTracks.Where(s => s.IsSelected).Select(s => s.Track).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Selecciona al menos una canción para incluir en la playlist.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var success = await _libraryService.CreatePlaylistWithTracksAsync(playlistName, selected);
            if (success)
            {
                PnlCreatePlaylistModal.Visibility = Visibility.Collapsed;
                await RefreshLibraryAsync();
                MessageBox.Show($"Playlist '{playlistName}' creada con éxito con {selected.Count} canciones.", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("No se pudo crear la playlist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region LOCAL PLAYER CONTROLS & UNIFIED HUD SYNC

        private void PlayTrack(MediaTrack track)
        {
            SoundService.PlayAction();

            // 1. Pause YT Music HTML5 videos immediately
            if (YtWebView?.CoreWebView2 != null)
            {
                _ = YtWebView.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('video').forEach(v => { try { v.pause(); } catch(e){} });");
            }

            // 2. Lock player mode strictly to LOCAL
            _activePlayerMode = ActivePlayerMode.Local;
            _ytmIsPlaying = false;
            _currentTrack = track;

            // 3. Immediately set Local track details & cover on the HUD
            PnlBottomPlayer.Visibility = Visibility.Visible;
            LblPlayerTitle.Text = track.Title;
            LblPlayerArtist.Text = track.Artist;
            UpdateCoverImage(coverUrl: null, localBitmap: track.Thumbnail);
            BtnPlayerPlayPause.Content = "⏸";

            // Highlight and scroll to track in DataGrid
            if (DgLibrary != null && _currentPlaylistTracks.Contains(track))
            {
                DgLibrary.SelectedItem = track;
                DgLibrary.ScrollIntoView(track);
            }

            try
            {
                _mediaPlayer.Open(new Uri(track.FilePath));
                _mediaPlayer.Play();
                _timer.Start();

                _lastDiscordTitle = track.Title;
                _lastDiscordPlaying = true;
                _discordService.UpdateLocalTrack(track.Title, track.Artist, 0, track.Duration.TotalSeconds);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al reproducir audio: {ex.Message}");
            }
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (_activePlayerMode == ActivePlayerMode.Local)
            {
                if (_mediaPlayer.NaturalDuration.HasTimeSpan && !_isUserSeeking)
                {
                    var current = _mediaPlayer.Position;
                    var total = _mediaPlayer.NaturalDuration.TimeSpan;

                    LblPlayerCurrentTime.Text = current.ToString(@"mm\:ss");
                    LblPlayerTotalTime.Text = total.ToString(@"mm\:ss");

                    if (total.TotalSeconds > 0)
                    {
                        SldPlayerSeek.Value = (current.TotalSeconds / total.TotalSeconds) * 100.0;
                    }
                }
            }
        }

        private void OnPlayerPlayPauseClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();

            if (_activePlayerMode == ActivePlayerMode.YouTubeMusic)
            {
                if (YtWebView?.CoreWebView2 != null)
                {
                    BtnPlayerPlayPause.Content = BtnPlayerPlayPause.Content.ToString() == "⏸" ? "▶" : "⏸";

                    _ = YtWebView.CoreWebView2.ExecuteScriptAsync(@"
(function() {
    const playBtn = document.querySelector('ytmusic-player-bar #play-pause-button, .ytmusic-player-bar #play-pause-button, #play-pause-button');
    if (playBtn) {
        playBtn.click();
    } else {
        const mp = document.getElementById('movie_player');
        if (mp && typeof mp.playVideo === 'function') {
            const state = mp.getPlayerState ? mp.getPlayerState() : -1;
            if (state === 1) mp.pauseVideo(); else mp.playVideo();
        } else {
            const v = document.querySelector('video');
            if (v) v.paused ? v.play() : v.pause();
        }
    }
})();
");
                }
                return;
            }

            if (_currentTrack == null) return;

            if (BtnPlayerPlayPause.Content.ToString() == "⏸")
            {
                _mediaPlayer.Pause();
                BtnPlayerPlayPause.Content = "▶";

                _lastDiscordPlaying = false;
                _discordService.UpdateLocalPaused(_currentTrack.Title, _currentTrack.Artist);
            }
            else
            {
                _mediaPlayer.Play();
                BtnPlayerPlayPause.Content = "⏸";

                _lastDiscordPlaying = true;
                double cur = _mediaPlayer.Position.TotalSeconds;
                double tot = _mediaPlayer.NaturalDuration.HasTimeSpan ? _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds : _currentTrack.Duration.TotalSeconds;
                _discordService.UpdateLocalTrack(_currentTrack.Title, _currentTrack.Artist, cur, tot);
            }
        }

        private void OnPlayerPrevClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();

            if (_activePlayerMode == ActivePlayerMode.YouTubeMusic)
            {
                if (YtWebView?.CoreWebView2 != null)
                {
                    _ = YtWebView.CoreWebView2.ExecuteScriptAsync(@"
(function() {
    const prevBtn = document.querySelector('ytmusic-player-bar .previous-button, .ytmusic-player-bar .previous-button, .previous-button');
    if (prevBtn) {
        prevBtn.click();
    } else {
        const mp = document.getElementById('movie_player');
        if (mp && typeof mp.previousVideo === 'function') mp.previousVideo();
    }
})();
");
                }
                return;
            }

            if (_currentTrack == null || _currentPlaylistTracks.Count == 0) return;

            var idx = _currentPlaylistTracks.IndexOf(_currentTrack);
            if (idx > 0)
            {
                PlayTrack(_currentPlaylistTracks[idx - 1]);
            }
            else
            {
                PlayTrack(_currentPlaylistTracks[_currentPlaylistTracks.Count - 1]);
            }
        }

        private void OnPlayerNextClick(object sender, RoutedEventArgs e)
        {
            SoundService.PlayClick();

            if (_activePlayerMode == ActivePlayerMode.YouTubeMusic)
            {
                if (YtWebView?.CoreWebView2 != null)
                {
                    _ = YtWebView.CoreWebView2.ExecuteScriptAsync(@"
(function() {
    const nextBtn = document.querySelector('ytmusic-player-bar .next-button, .ytmusic-player-bar .next-button, .next-button');
    if (nextBtn) {
        nextBtn.click();
    } else {
        const mp = document.getElementById('movie_player');
        if (mp && typeof mp.nextVideo === 'function') mp.nextVideo();
    }
})();
");
                }
                return;
            }

            if (_currentTrack == null || _currentPlaylistTracks.Count == 0) return;

            var idx = _currentPlaylistTracks.IndexOf(_currentTrack);
            if (idx >= 0 && idx < _currentPlaylistTracks.Count - 1)
            {
                PlayTrack(_currentPlaylistTracks[idx + 1]);
            }
            else
            {
                PlayTrack(_currentPlaylistTracks[0]);
            }
        }

        private void OnSeekStarted(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = true;
        }

        private void OnSeekCompleted(object sender, MouseButtonEventArgs e)
        {
            PerformSeek();
            _isUserSeeking = false;
        }

        private void PerformSeek()
        {
            if (_activePlayerMode == ActivePlayerMode.YouTubeMusic)
            {
                if (YtWebView?.CoreWebView2 != null && _ytmDuration > 0)
                {
                    var targetSeconds = (SldPlayerSeek.Value / 100.0) * _ytmDuration;
                    var secStr = targetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var js = "(function() { var mp = document.getElementById(\"movie_player\"); if (mp && typeof mp.seekTo === \"function\") { mp.seekTo(" + secStr + ", true); } else { var v = document.querySelector(\"video\"); if (v) v.currentTime = " + secStr + "; } })();";
                    _ = YtWebView.CoreWebView2.ExecuteScriptAsync(js);
                    LblPlayerCurrentTime.Text = TimeSpan.FromSeconds(targetSeconds).ToString(@"mm\:ss");
                }
                return;
            }

            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                var totalSeconds = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                var newSeconds = (SldPlayerSeek.Value / 100.0) * totalSeconds;
                _mediaPlayer.Position = TimeSpan.FromSeconds(newSeconds);
                LblPlayerCurrentTime.Text = TimeSpan.FromSeconds(newSeconds).ToString(@"mm\:ss");
            }
        }

        private void OnSeekSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUserSeeking)
            {
                if (_activePlayerMode == ActivePlayerMode.YouTubeMusic && _ytmDuration > 0)
                {
                    var newSeconds = (SldPlayerSeek.Value / 100.0) * _ytmDuration;
                    LblPlayerCurrentTime.Text = TimeSpan.FromSeconds(newSeconds).ToString(@"mm\:ss");
                }
                else if (_mediaPlayer.NaturalDuration.HasTimeSpan)
                {
                    var totalSeconds = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                    var newSeconds = (SldPlayerSeek.Value / 100.0) * totalSeconds;
                    LblPlayerCurrentTime.Text = TimeSpan.FromSeconds(newSeconds).ToString(@"mm\:ss");
                }
            }
        }

        private void OnVolumeSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _mediaPlayer.Volume = SldPlayerVolume.Value;

            if (_activePlayerMode == ActivePlayerMode.YouTubeMusic && YtWebView?.CoreWebView2 != null)
            {
                _ = YtWebView.CoreWebView2.ExecuteScriptAsync($"const v = document.querySelector('video'); if (v) v.volume = {SldPlayerVolume.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)};");
            }
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;

            if (e.Key == Key.Left)
            {
                SeekBySeconds(-5);
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                SeekBySeconds(5);
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                OnPlayerPlayPauseClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void SeekBySeconds(double seconds)
        {
            if (_activePlayerMode == ActivePlayerMode.YouTubeMusic)
            {
                if (YtWebView?.CoreWebView2 != null)
                {
                    var secStr = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var js = "(function() { var mp = document.getElementById(\"movie_player\"); if (mp && typeof mp.seekBy === \"function\") { mp.seekBy(" + secStr + "); } else { var v = document.querySelector(\"video\"); if (v) v.currentTime = Math.max(0, Math.min(v.duration || 0, v.currentTime + (" + secStr + "))); } })();";
                    _ = YtWebView.CoreWebView2.ExecuteScriptAsync(js);
                    if (_ytmDuration > 0)
                    {
                        var newPos = Math.Max(0, Math.Min(_ytmDuration, _ytmCurrentTime + seconds));
                        LblPlayerCurrentTime.Text = TimeSpan.FromSeconds(newPos).ToString(@"mm\:ss");
                        _isUserSeeking = true;
                        SldPlayerSeek.Value = (newPos / _ytmDuration) * 100.0;
                        _isUserSeeking = false;
                    }
                }
                return;
            }

            if (!_mediaPlayer.NaturalDuration.HasTimeSpan) return;

            var total = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            var current = _mediaPlayer.Position.TotalSeconds;
            var localNewPos = Math.Max(0, Math.Min(total, current + seconds));

            _mediaPlayer.Position = TimeSpan.FromSeconds(localNewPos);
            LblPlayerCurrentTime.Text = TimeSpan.FromSeconds(localNewPos).ToString(@"mm\:ss");

            _isUserSeeking = true;
            SldPlayerSeek.Value = total > 0 ? (localNewPos / total) * 100.0 : 0;
            _isUserSeeking = false;
        }

        #endregion
    }
}