using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eMusicApp.Models;
using eMusicApp.Services;

namespace eMusicApp.ViewModels
{
    public partial class LibraryViewModel : ObservableObject
    {
        private readonly ApiService _apiService;
        public PlayerViewModel Player { get; }

        public LibraryViewModel(ApiService apiService, PlayerViewModel player)
        {
            _apiService = apiService;
            Player = player;
            DownloadedSongs = new ObservableCollection<Track>();

            DownloadManager.Initialize();

            DownloadManager.OnDownloadProgress = (id, pct) =>
            {
                DownloadingId = id;
                DownloadProgress = pct / 100.0;
            };

            DownloadManager.OnDownloadCompleted = (id, success) =>
            {
                DownloadingId = string.Empty;
                DownloadProgress = 0.0;
                if (success) LoadDownloadedSongs();
            };
        }

        [ObservableProperty]
        private ObservableCollection<Track> _downloadedSongs;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDownloading))]
        private string _downloadingId = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DownloadProgressText))]
        private double _downloadProgress;

        public string DownloadProgressText => $"Descargando... {(int)(DownloadProgress * 100)}%";
        public bool IsDownloading => !string.IsNullOrEmpty(DownloadingId);

        [RelayCommand]
        private Task LoadLibraryAsync()
        {
            LoadDownloadedSongs();
            Player.SetQueue(DownloadedSongs);
            return Task.CompletedTask;
        }

        [RelayCommand]
        private void DeleteDownload(Track track)
        {
            if (track == null) return;
            if (DownloadManager.DeleteTrack(track.VideoId))
                LoadDownloadedSongs();
        }

        public void LoadDownloadedSongs()
        {
            DownloadedSongs.Clear();
            var dlds = DownloadManager.GetDownloadedTracks();
            foreach (var d in dlds)
            {
                DownloadedSongs.Add(new Track
                {
                    Id              = d.Id,
                    VideoIdFromJson = d.Id,
                    Url             = d.LocalPath,
                    Title           = d.Title,
                    Uploader        = d.Artist,
                    ThumbnailUrl    = d.ThumbUrl,
                    Type            = "stream"
                });
            }
        }
    }
}
