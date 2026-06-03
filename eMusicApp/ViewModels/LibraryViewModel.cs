using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eMusicApp.Models;
using eMusicApp.Services;
using System.Collections.Generic;

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
            LikedSongs = new ObservableCollection<Track>();
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
        private ObservableCollection<Track> _likedSongs;

        [ObservableProperty]
        private ObservableCollection<Track> _DownloadedSongs;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowFavorites))]
        [NotifyPropertyChangedFor(nameof(ShowDownloads))]
        [NotifyPropertyChangedFor(nameof(HasNoFavorites))]
        [NotifyPropertyChangedFor(nameof(HasNoDownloads))]
        private string _selectedTab = "Favoritos";

        public bool ShowFavorites => SelectedTab == "Favoritos";
        public bool ShowDownloads => SelectedTab == "Descargas";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDownloading))]
        private string _downloadingId = string.Empty;

        // 0.0-1.0 para ProgressBar; texto calculado con DownloadProgressText
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DownloadProgressText))]
        private double _downloadProgress;

        public string DownloadProgressText => $"Descargando... {(int)(DownloadProgress * 100)}%";
        public bool IsDownloading => !string.IsNullOrEmpty(DownloadingId);

        public bool HasNoFavorites        => ShowFavorites && !IsBusy && LikedSongs.Count == 0;
        public bool HasFavorites          => LikedSongs.Count > 0;
        public bool HasNoDownloads        => ShowDownloads && DownloadedSongs.Count == 0;
        public bool HasDownloads          => DownloadedSongs.Count > 0;

        [RelayCommand]
        private void SetTab(string tab)
        {
            SelectedTab = tab;
            if (tab == "Descargas")
            {
                LoadDownloadedSongs();
                Player.SetQueue(DownloadedSongs);
            }
            else
            {
                Player.SetQueue(LikedSongs);
            }
        }

        [ObservableProperty]
        private bool _isBusy;

        [RelayCommand]
        private async Task LoadLibraryAsync()
        {
            IsBusy = true;
            
            // Liked songs skeleton
            LikedSongs.Clear();
            for(int i=0; i<4; i++) LikedSongs.Add(new Track()); 

            var favs = await _apiService.GetFavoritesAsync();
            
            LikedSongs.Clear();
            foreach (var f in favs)
                LikedSongs.Add(f);

            OnPropertyChanged(nameof(HasNoFavorites));

            LoadDownloadedSongs();

            if (SelectedTab == "Descargas")
                Player.SetQueue(DownloadedSongs);
            else
                Player.SetQueue(LikedSongs);

            IsBusy = false;
        }

        [RelayCommand]
        private async Task RemoveFavoriteAsync(Track track)
        {
            if (track == null || string.IsNullOrEmpty(track.Id)) return;
            await _apiService.RemoveFavoriteAsync(track.Id);
            LikedSongs.Remove(track);
        }

        [RelayCommand]
        private async Task DownloadTrackAsync(Track track)
        {
            if (track == null) return;

            string streamUrl = track.Url;
            if (string.IsNullOrEmpty(streamUrl) || !streamUrl.StartsWith("http") || streamUrl.Contains("youtube.com") || streamUrl.Contains("watch?v="))
            {
                var streamInfo = await _apiService.GetStreamAsync(track.VideoId);
                if (streamInfo != null && streamInfo.AudioStreams != null && streamInfo.AudioStreams.Count > 0)
                {
                    streamUrl = streamInfo.AudioStreams[0].Url;
                }
            }

            if (string.IsNullOrEmpty(streamUrl) || !streamUrl.StartsWith("http")) return;

            bool success = await DownloadManager.DownloadTrackAsync(
                track.VideoId, 
                streamUrl, 
                track.Title, 
                track.Uploader, 
                track.ThumbnailUrl
            );

            if (success)
            {
                LoadDownloadedSongs();
            }
        }

        [RelayCommand]
        private void DeleteDownload(Track track)
        {
            if (track == null) return;
            bool deleted = DownloadManager.DeleteTrack(track.VideoId);
            if (deleted)
            {
                LoadDownloadedSongs();
            }
        }

        public void LoadDownloadedSongs()
        {
            DownloadedSongs.Clear();
            var dlds = DownloadManager.GetDownloadedTracks();
            foreach (var d in dlds)
            {
                DownloadedSongs.Add(new Track
                {
                    Id           = d.Id,
                    Url          = d.LocalPath,
                    Title        = d.Title,
                    Uploader     = d.Artist,
                    ThumbnailUrl = d.ThumbUrl,
                    Type         = "stream"
                });
            }
            OnPropertyChanged(nameof(HasNoDownloads));
        }

        public bool IsTrackDownloaded(string videoId)
        {
            return DownloadManager.IsTrackDownloaded(videoId);
        }
    }
}
