using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eMusicApp.Models;
using eMusicApp.Services;

namespace eMusicApp.ViewModels
{
    public partial class SearchViewModel : ObservableObject
    {
        private readonly ApiService _apiService;
        public PlayerViewModel Player { get; }

        public SearchViewModel(ApiService apiService, PlayerViewModel playerViewModel)
        {
            _apiService = apiService;
            Player = playerViewModel;
            SearchResults = new ObservableCollection<Track>();
        }

        [ObservableProperty]
        private ObservableCollection<Track> _searchResults;

        [ObservableProperty]
        private string _searchQuery;

        partial void OnSearchQueryChanged(string value)
        {
            // No disparamos búsqueda automática - el usuario debe pulsar Enter o el botón de buscar
            // Esto evita que se lancen múltiples peticiones al servidor mientras el usuario escribe
        }

        private System.Threading.CancellationTokenSource _searchCts;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoResults))]
        private bool _isBusy;

        public bool HasNoResults    => !IsBusy && SearchResults.Count == 0 && !string.IsNullOrWhiteSpace(SearchQuery);
        public bool HasResults      => SearchResults.Count > 0;
        public bool ShowInitialPrompt => !IsBusy && string.IsNullOrWhiteSpace(SearchQuery);

        [RelayCommand]
        private async Task SearchAsync()
        {
            if (string.IsNullOrWhiteSpace(SearchQuery))
                return;

            IsBusy = true;
            SearchResults.Clear();

            var results = await _apiService.SearchTracksAsync(SearchQuery);

            SearchResults = new ObservableCollection<Track>(results);
            Player.SetQueue(SearchResults);

            IsBusy = false;
            OnPropertyChanged(nameof(HasNoResults));
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(ShowInitialPrompt));
        }

        [RelayCommand]
        private async Task ToggleFavorite(Track track)
        {
            if (track == null || string.IsNullOrEmpty(track.Title)) return;
            await _apiService.AddFavoriteAsync(track);
        }
    }
}
