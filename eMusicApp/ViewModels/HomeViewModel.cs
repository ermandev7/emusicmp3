using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eMusicApp.Models;
using eMusicApp.Services;
using Microsoft.Maui.ApplicationModel;

namespace eMusicApp.ViewModels
{
    public partial class HomeViewModel : ObservableObject
    {
        private readonly ApiService _apiService;
        public PlayerViewModel Player { get; }

        public HomeViewModel(ApiService apiService, PlayerViewModel player)
        {
            _apiService = apiService;
            Player = player;
            RecentTracks = new ObservableCollection<Track>();
            
            // Sincronización Reactiva de Historial desde Capa Nativa
            // También actualiza PlayerViewModel.CurrentTrack para auto-avance de ExoPlayer
            NativeAudioController.OnTrackStarted = (videoId, title, artist, thumb, duration) =>
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var newTrack = new Track
                    {
                        VideoIdFromJson = videoId,
                        Title = title,
                        Uploader = artist,
                        ThumbnailUrl = thumb,
                        Duration = duration
                    };

                    // Actualizar CurrentTrack en el Player (cubre auto-avance nativo de ExoPlayer)
                    Player.NotifyTrackStarted(newTrack);

                    // Evitar duplicados en historial visual
                    for (int i = RecentTracks.Count - 1; i >= 0; i--)
                    {
                        if (RecentTracks[i].VideoId == newTrack.VideoId || RecentTracks[i].VideoIdFromJson == newTrack.VideoIdFromJson)
                            RecentTracks.RemoveAt(i);
                    }

                    RecentTracks.Insert(0, newTrack);
                    if (RecentTracks.Count > 20)
                        RecentTracks.RemoveAt(RecentTracks.Count - 1);

                    OnPropertyChanged(nameof(HasNoHistory));
                    OnPropertyChanged(nameof(HasHistory));

                    Player.SetQueue(RecentTracks);

                    // Persistir en la Pi en background
                    await _apiService.AddHistoryAsync(newTrack);
                });
            };
        }

        [ObservableProperty]
        private ObservableCollection<Track> _recentTracks;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoHistory))]
        private bool _isBusy;

        public bool HasNoHistory => !IsBusy && RecentTracks.Count == 0;
        public bool HasHistory   => !HasNoHistory;

        [RelayCommand]
        private async Task LoadRecentTracksAsync()
        {
            IsBusy = true;

            var hist = await _apiService.GetHistoryAsync();

            RecentTracks = new ObservableCollection<Track>(hist);
            Player.SetQueue(RecentTracks);

            IsBusy = false;
            OnPropertyChanged(nameof(HasNoHistory));
            OnPropertyChanged(nameof(HasHistory));
        }
    }
}
