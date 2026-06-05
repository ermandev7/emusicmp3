using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eMusicApp.Models;
using eMusicApp.Services;
using Microsoft.Maui.Storage;

namespace eMusicApp.ViewModels
{
    public partial class SearchViewModel : ObservableObject
    {
        private readonly ApiService _apiService;
        public PlayerViewModel Player { get; }

        private const string RecentQueriesKey = "recent_search_queries";
        private const int    MaxRecentQueries = 8;

        public ObservableCollection<string> RecentQueries { get; } = new ObservableCollection<string>();

        public SearchViewModel(ApiService apiService, PlayerViewModel playerViewModel)
        {
            _apiService = apiService;
            Player = playerViewModel;
            SearchResults = new ObservableCollection<Track>();
            LoadRecentQueries();
        }

        private void LoadRecentQueries()
        {
            var saved = Preferences.Default.Get(RecentQueriesKey, string.Empty);
            if (string.IsNullOrEmpty(saved)) return;
            foreach (var q in saved.Split('|'))
            {
                if (!string.IsNullOrEmpty(q) && RecentQueries.Count < MaxRecentQueries)
                    RecentQueries.Add(q);
            }
        }

        private void SaveRecentQuery(string query)
        {
            RecentQueries.Remove(query); // eliminar duplicado previo
            RecentQueries.Insert(0, query);
            while (RecentQueries.Count > MaxRecentQueries)
                RecentQueries.RemoveAt(RecentQueries.Count - 1);
            Preferences.Default.Set(RecentQueriesKey, string.Join("|", RecentQueries));
            OnPropertyChanged(nameof(HasRecentQueries));
            OnPropertyChanged(nameof(HasNoRecentQueries));
        }

        [RelayCommand]
        private void ClearRecentQueries()
        {
            RecentQueries.Clear();
            Preferences.Default.Set(RecentQueriesKey, string.Empty);
            OnPropertyChanged(nameof(HasRecentQueries));
            OnPropertyChanged(nameof(HasNoRecentQueries));
        }

        [RelayCommand]
        private async Task SelectRecentQuery(string query)
        {
            SearchQuery = query;
            await SearchAsync();
        }

        [ObservableProperty]
        private ObservableCollection<Track> _searchResults;

        [ObservableProperty]
        private string _searchQuery;

        partial void OnSearchQueryChanged(string value)
        {
            OnPropertyChanged(nameof(ShowInitialPrompt));
            OnPropertyChanged(nameof(HasRecentQueries));
            OnPropertyChanged(nameof(HasNoRecentQueries));
        }

        private System.Threading.CancellationTokenSource _searchCts;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoResults))]
        private bool _isBusy;

        public bool HasNoResults      => !IsBusy && SearchResults.Count == 0 && !string.IsNullOrWhiteSpace(SearchQuery);
        public bool HasResults        => SearchResults.Count > 0;
        public bool ShowInitialPrompt => !IsBusy && string.IsNullOrWhiteSpace(SearchQuery);
        public bool HasRecentQueries  => ShowInitialPrompt && RecentQueries.Count > 0;
        public bool HasNoRecentQueries => ShowInitialPrompt && RecentQueries.Count == 0;

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

            SaveRecentQuery(SearchQuery.Trim());

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
