using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eMusicApp.Models;
using eMusicApp.Services;

namespace eMusicApp.ViewModels
{
    [QueryProperty(nameof(PlaylistId), "id")]
    [QueryProperty(nameof(PlaylistName), "name")]
    public partial class PlaylistDetailViewModel : ObservableObject
    {
        private readonly ApiService _apiService;
        public PlayerViewModel Player { get; }

        public PlaylistDetailViewModel(ApiService apiService, PlayerViewModel player)
        {
            _apiService = apiService;
            Player = player;
        }

        [ObservableProperty]
        private int _playlistId;

        [ObservableProperty]
        private string _playlistName = "";

        [ObservableProperty]
        private ObservableCollection<Track> _tracks = new ObservableCollection<Track>();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoTracks))]
        [NotifyPropertyChangedFor(nameof(HasTracks))]
        private bool _isBusy;

        public bool HasNoTracks => !IsBusy && Tracks.Count == 0;
        public bool HasTracks   => Tracks.Count > 0;

        partial void OnPlaylistIdChanged(int value)
        {
            if (value > 0)
                _ = LoadTracksAsync();
        }

        [RelayCommand]
        private async Task LoadTracks()
        {
            await LoadTracksAsync();
        }

        private async Task LoadTracksAsync()
        {
            IsBusy = true;
            var playlists = await _apiService.GetPlaylistsAsync();
            var playlist = playlists.Find(p => p.Id == PlaylistId);
            if (playlist != null)
            {
                if (string.IsNullOrEmpty(PlaylistName))
                    PlaylistName = playlist.Name;

                Tracks.Clear();
                foreach (var t in playlist.Tracks)
                    Tracks.Add(t);

                Player.SetQueue(Tracks);
            }
            OnPropertyChanged(nameof(HasNoTracks));
            OnPropertyChanged(nameof(HasTracks));
            IsBusy = false;
        }

        [RelayCommand]
        private async Task PlayAll()
        {
            if (Tracks.Count == 0) return;
            Player.SetQueue(Tracks);
            await Player.PlayTrackCommand.ExecuteAsync(Tracks[0]);
        }

        [RelayCommand]
        private async Task RemoveTrack(Track track)
        {
            if (track == null) return;
            await _apiService.RemoveTrackFromPlaylistAsync(PlaylistId, track.VideoId);
            Tracks.Remove(track);
            Player.SetQueue(Tracks);
            OnPropertyChanged(nameof(HasNoTracks));
            OnPropertyChanged(nameof(HasTracks));
        }
    }
}
