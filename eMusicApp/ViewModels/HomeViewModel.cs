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
                    try
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
                    while (RecentTracks.Count > 12)
                        RecentTracks.RemoveAt(RecentTracks.Count - 1);

                    OnPropertyChanged(nameof(HasNoHistory));
                    OnPropertyChanged(nameof(HasHistory));

                    // No sobreescribir la cola si el modo radio está activo o si ya hay
                    // tracks en cola (auto-avance nativo de ExoPlayer). Sobreescribir destruye
                    // los tracks que ExtendQueueWithRelatedAsync() añadió.
                    if (!Player.IsRadioMode && !Player.IsGenreRadioActive && Player.PlayQueue.Count <= 1)
                        Player.SetQueue(RecentTracks);

                    // Persistir en la Pi en background (con flag de descarga)
                    DownloadManager.Initialize();
                    bool downloaded = DownloadManager.IsTrackDownloaded(newTrack.VideoId);
                    await _apiService.AddHistoryAsync(newTrack, downloaded);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Home] OnTrackStarted error: {ex.Message}"); }
                });
            };

            // Botones prev/next de la notificación MediaStyle
            NativeAudioController.OnSkipToNext = () =>
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try { await Player.NextTrackCommand.ExecuteAsync(null); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Home] SkipNext error: {ex.Message}"); }
                });
            NativeAudioController.OnSkipToPrevious = () =>
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try { await Player.PreviousTrackCommand.ExecuteAsync(null); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Home] SkipPrev error: {ex.Message}"); }
                });
        }

        public string Greeting
        {
            get
            {
                var name = Preferences.Default.Get("user_name", "");
                int hour = DateTime.Now.Hour;
                var saludo = hour < 12 ? "Buenos dias" : hour < 19 ? "Buenas tardes" : "Buenas noches";
                return string.IsNullOrEmpty(name) ? saludo : $"{saludo}, {name}";
            }
        }

        // ── Géneros (radio por género) ──
        // 4 géneros fijos + hasta 4 detectados del historial = 8 max
        private static readonly string[] _defaultGenres = { "salsa", "rock", "reggae", "balada", "bachata", "merengue" };

        [ObservableProperty]
        private ObservableCollection<string> _genres = new(_defaultGenres);

        public string? ActiveGenre => Player.ActiveGenre;

        [RelayCommand]
        private async Task PlayGenre(string genre)
        {
            await Player.PlayGenreAsync(genre);
            OnPropertyChanged(nameof(ActiveGenre));
        }

        private async Task UpdateGenresFromApiAsync()
        {
            var topGenres = await _apiService.GetTopGenresAsync();

            // Empezar con los géneros más escuchados del API
            var fromApi = topGenres
                .Select(g => g.Genre)
                .Where(g => PlayerViewModel.AvailableGenres.Contains(g))
                .Take(12)
                .ToList();

            // Completar con defaults si no llegamos a 12
            var result = new List<string>(fromApi);
            foreach (var g in _defaultGenres)
            {
                if (result.Count >= 12) break;
                if (!result.Contains(g)) result.Add(g);
            }

            // Rellenar con otros disponibles si aún faltan
            foreach (var g in PlayerViewModel.AvailableGenres)
            {
                if (result.Count >= 12) break;
                if (!result.Contains(g)) result.Add(g);
            }

            Genres.Clear();
            foreach (var g in result) Genres.Add(g);
        }

        [ObservableProperty]
        private ObservableCollection<Track> _recentTracks;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasRecommendations))]
        private ObservableCollection<Track> _recommendedTracks = new();

        public bool HasRecommendations => RecommendedTracks.Count > 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoHistory))]
        [NotifyPropertyChangedFor(nameof(IsNotBusy))]
        private bool _isBusy;

        public bool IsNotBusy => !IsBusy;

        public bool HasNoHistory => !IsBusy && RecentTracks.Count == 0;
        public bool HasHistory   => RecentTracks.Count > 0;

        [RelayCommand]
        private async Task LoadRecentTracksAsync()
        {
            IsBusy = true;

            // Paralelo: historial + géneros + recomendaciones al mismo tiempo
            var histTask = _apiService.GetHistoryAsync();
            var genresTask = UpdateGenresFromApiAsync();
            var recosTask = LoadRecommendationsAsync();

            var hist = await histTask;
            RecentTracks.Clear();
            foreach (var t in hist.Take(12))
                RecentTracks.Add(t);
            Player.SetQueue(hist);

            IsBusy = false;
            OnPropertyChanged(nameof(HasNoHistory));
            OnPropertyChanged(nameof(HasHistory));

            await genresTask;
            await recosTask;
        }

        private async Task LoadRecommendationsAsync()
        {
            try
            {
                var recs = await _apiService.GetRecommendationsAsync(15);
                RecommendedTracks.Clear();
                foreach (var t in recs)
                    RecommendedTracks.Add(t);
                OnPropertyChanged(nameof(HasRecommendations));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Home] LoadRecommendations error: {ex.Message}");
            }
        }
    }
}
