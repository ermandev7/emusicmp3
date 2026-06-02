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
            
            // Sincronización Reactiva de Historial desde Capa Nativa (Punto 4)
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

                    // Optimistic UI Update: Empujar el ítem al principio visualmente
                    RecentTracks.Insert(0, newTrack);
                    if (RecentTracks.Count > 10)
                    {
                        RecentTracks.RemoveAt(RecentTracks.Count - 1);
                    }

                    Player.SetQueue(RecentTracks);

                    // Sincronización en Background hacia la API Docker
                    await _apiService.AddHistoryAsync(newTrack);
                });
            };
        }

        [ObservableProperty]
        private ObservableCollection<Track> _recentTracks;

        [ObservableProperty]
        private bool _isBusy;

        [RelayCommand]
        private async Task LoadRecentTracksAsync()
        {
            IsBusy = true;
            
            // Populate with fake items for skeleton to show
            RecentTracks.Clear();
            for(int i=0; i<6; i++) RecentTracks.Add(new Track()); 

            var hist = await _apiService.GetHistoryAsync();
            
            RecentTracks.Clear();
            foreach (var h in hist)
            {
                RecentTracks.Add(h);
            }

            Player.SetQueue(RecentTracks);
            
            IsBusy = false;
        }
    }
}
