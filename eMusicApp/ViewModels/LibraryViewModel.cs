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
            LikedSongs = new ObservableCollection<Track>();
        }

        [ObservableProperty]
        private ObservableCollection<Track> _likedSongs;

        [ObservableProperty]
        private bool _isBusy;

        [RelayCommand]
        private async Task LoadLibraryAsync()
        {
            IsBusy = true;
            
            // Populate with fake items for skeleton to show
            LikedSongs.Clear();
            for(int i=0; i<4; i++) LikedSongs.Add(new Track()); 

            var favs = await _apiService.GetFavoritesAsync();
            
            LikedSongs.Clear();
            foreach (var f in favs)
            {
                LikedSongs.Add(f);
            }
            
            IsBusy = false;
        }

        [RelayCommand]
        private async Task RemoveFavoriteAsync(Track track)
        {
            if (track == null || string.IsNullOrEmpty(track.Id)) return;
            await _apiService.RemoveFavoriteAsync(track.Id);
            LikedSongs.Remove(track);
        }
    }
}
