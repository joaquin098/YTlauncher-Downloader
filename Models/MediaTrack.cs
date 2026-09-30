using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media.Imaging;

namespace YTMusicLauncher.Models
{
    public class MediaTrack : INotifyPropertyChanged
    {
        public string FilePath { get; set; } = string.Empty;
        public string Title { get; set; } = "Desconocido";
        public string Artist { get; set; } = "Artista Desconocido";
        public string Album { get; set; } = "Álbum Desconocido";
        public string Genre { get; set; } = "Sin género";
        public TimeSpan Duration { get; set; }
        public string DurationFormatted => Duration.ToString(@"mm\:ss");
        public BitmapImage? Thumbnail { get; set; }
        public string PlaylistName { get; set; } = "General";
        public long FileSizeBytes { get; set; }
        public string FileSizeFormatted => $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB";
        public DateTime DateAdded { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class SelectableTrack : INotifyPropertyChanged
    {
        private bool _isSelected;

        public MediaTrack Track { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public SelectableTrack(MediaTrack track, bool isSelected = false)
        {
            Track = track;
            _isSelected = isSelected;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum DownloadStatus
    {
        Queued,
        Downloading,
        Converting,
        Completed,
        Failed,
        Canceled,
        Skipped
    }

    public class SubDownloadItem : INotifyPropertyChanged
    {
        private double _progress;
        private string _statusText = "En cola";
        private DownloadStatus _status = DownloadStatus.Queued;
        private string _title = "Cargando...";

        public int Index { get; set; }
        public string Url { get; set; } = string.Empty;
        public string OutputFilePath { get; set; } = string.Empty;

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public DownloadStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSkip));
            }
        }

        public bool CanSkip => Status == DownloadStatus.Downloading || Status == DownloadStatus.Queued || Status == DownloadStatus.Converting;

        public Process? ActiveProcess { get; set; }
        public CancellationTokenSource? Cts { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class DownloadItem : INotifyPropertyChanged
    {
        private double _progress;
        private string _statusText = "En cola";
        private DownloadStatus _status = DownloadStatus.Queued;
        private string _speed = "";
        private string _eta = "";
        private string _title = "Obteniendo información...";
        private bool _isExpanded = false;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Url { get; set; } = string.Empty;
        public string QualityFormat { get; set; } = "MP3 (320kbps)";
        public string OutputDirectory { get; set; } = string.Empty;
        public bool SingleTrackOnly { get; set; } = false;
        public bool IsPlaylist { get; set; } = false;

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public ObservableCollection<SubDownloadItem> SubItems { get; set; } = new();

        public Process? ActiveProcess { get; set; }
        public CancellationTokenSource? Cts { get; set; }

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public DownloadStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanCancel));
            }
        }

        public bool CanCancel => Status == DownloadStatus.Downloading || Status == DownloadStatus.Queued || Status == DownloadStatus.Converting;

        public string Speed
        {
            get => _speed;
            set { _speed = value; OnPropertyChanged(); }
        }

        public string Eta
        {
            get => _eta;
            set { _eta = value; OnPropertyChanged(); }
        }

        public string LogOutput { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
