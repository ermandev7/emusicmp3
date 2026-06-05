using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
                    while (RecentTracks.Count > 6)
                        RecentTracks.RemoveAt(RecentTracks.Count - 1);

                    OnPropertyChanged(nameof(HasNoHistory));
                    OnPropertyChanged(nameof(HasHistory));

                    // No sobreescribir la cola si el modo radio está activo o si ya hay
                    // tracks en cola (auto-avance nativo de ExoPlayer). Sobreescribir destruye
                    // los tracks que ExtendQueueWithRelatedAsync() añadió.
                    if (!Player.IsRadioMode && Player.PlayQueue.Count <= 1)
                        Player.SetQueue(RecentTracks);

                    // Persistir en la Pi en background
                    await _apiService.AddHistoryAsync(newTrack);
                });
            };

            // Botones prev/next de la notificación MediaStyle
            NativeAudioController.OnSkipToNext = () =>
                MainThread.BeginInvokeOnMainThread(async () => await Player.NextTrackCommand.ExecuteAsync(null));
            NativeAudioController.OnSkipToPrevious = () =>
                MainThread.BeginInvokeOnMainThread(async () => await Player.PreviousTrackCommand.ExecuteAsync(null));
        }

        public string Greeting
        {
            get
            {
                int hour = DateTime.Now.Hour;
                if (hour < 12) return "Buenos dias";
                if (hour < 19) return "Buenas tardes";
                return "Buenas noches";
            }
        }

        // ── Géneros (radio por género) ──
        // 4 géneros fijos + hasta 4 detectados del historial = 8 max
        private static readonly string[] _defaultGenres = { "salsa", "rock", "reggae", "balada" };

        [ObservableProperty]
        private ObservableCollection<string> _genres = new(_defaultGenres);

        public string? ActiveGenre => Player.ActiveGenre;

        [RelayCommand]
        private async Task PlayGenre(string genre)
        {
            await Player.PlayGenreAsync(genre);
            OnPropertyChanged(nameof(ActiveGenre));
        }

        private void UpdateGenresFromHistory(IEnumerable<Track> history)
        {
            // Contar géneros detectados en el historial
            var counts = new Dictionary<string, int>();
            foreach (var t in history)
            {
                var g = PlayerViewModel.DetectGenre(t.Title, t.Uploader);
                if (g != null)
                    counts[g] = counts.TryGetValue(g, out var c) ? c + 1 : 1;
            }

            // Top géneros del historial que no están en los defaults
            var detected = counts
                .Where(kv => !_defaultGenres.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .Take(4)
                .ToList();

            Genres.Clear();
            foreach (var g in _defaultGenres) Genres.Add(g);
            foreach (var g in detected) Genres.Add(g);
        }

        [ObservableProperty]
        private ObservableCollection<Track> _recentTracks;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoHistory))]
        private bool _isBusy;

        public bool HasNoHistory => !IsBusy && RecentTracks.Count == 0;
        public bool HasHistory   => RecentTracks.Count > 0;

        [RelayCommand]
        private async Task LoadRecentTracksAsync()
        {
            IsBusy = true;

            var hist = await _apiService.GetHistoryAsync();
            UpdateGenresFromHistory(hist);
            // Solo mostrar los 6 más recientes en el Home
            var recent = hist.Take(6).ToList();
            RecentTracks = new ObservableCollection<Track>(recent);
            Player.SetQueue(new ObservableCollection<Track>(hist));

            IsBusy = false;
            OnPropertyChanged(nameof(HasNoHistory));
            OnPropertyChanged(nameof(HasHistory));
        }
    }
}
