using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eMusicApp.Models;
using eMusicApp.Services;
using System.Threading.Tasks;

namespace eMusicApp.ViewModels
{
    public partial class PlayerViewModel : ObservableObject
    {
        private readonly ApiService _apiService;

        public PlayerViewModel(ApiService apiService)
        {
            _apiService = apiService;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCurrentTrack))]
        private Track _currentTrack;

        public bool HasCurrentTrack => CurrentTrack != null;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
        private bool _isPlaying;

        public string PlayPauseIcon => IsPlaying ? "⏸️" : "▶️";

        [RelayCommand]
        private void TogglePlayback()
        {
            if (IsPlaying)
            {
                NativeAudioController.RequestPause();
            }
            else
            {
                NativeAudioController.RequestResume();
            }
            IsPlaying = !IsPlaying;
        }

        [RelayCommand]
        private async Task PlayTrack(Track track)
        {
            if (track == null) return;
            
            CurrentTrack = track;
            IsPlaying = true;
            
            // Add to History asynchronously
            _ = _apiService.AddHistoryAsync(track);
            
            // Call NativeAudioController
            NativeAudioController.RequestPlay(track.Url, track.Title, track.Uploader, track.ThumbnailUrl);
        }

        [RelayCommand]
        private void NextTrack()
        {
            // Placeholder: Needs a queue or playlist context
        }

        [RelayCommand]
        private void PreviousTrack()
        {
            // Placeholder: Needs a queue or playlist context
        }
    }
}
