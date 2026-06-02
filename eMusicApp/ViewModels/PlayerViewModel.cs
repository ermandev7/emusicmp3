using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eMusicApp.Models;
using eMusicApp.Services;
using Microsoft.Maui.ApplicationModel;
using System;
using System.Threading.Tasks;

namespace eMusicApp.ViewModels
{
    public partial class PlayerViewModel : ObservableObject
    {
        private readonly ApiService _apiService;

        public PlayerViewModel(ApiService apiService)
        {
            _apiService = apiService;

            NativeAudioController.OnProgressUpdated = (posMs, durMs) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!IsDraggingSlider)
                    {
                        // IMPORTANTE: Primero actualizar la duración (SliderMaximum) para evitar que el control 
                        // Slider de MAUI trunque la posición al valor máximo anterior (1).
                        Duration = durMs;
                        Position = posMs;
                    }
                });
            };

            NativeAudioController.OnPlaybackStateChanged = (isPlaying) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsPlaying = isPlaying;
                });
            };

            NativeAudioController.OnCrossfadeCompleted = (title, artist, thumb) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (CurrentTrack != null)
                    {
                        CurrentTrack = new Track
                        {
                            Title = title,
                            Uploader = artist,
                            ThumbnailUrl = thumb,
                            Url = CurrentTrack.Url, // Keep url/id context
                            Id = CurrentTrack.Id,
                            Type = CurrentTrack.Type
                        };
                    }
                });
            };

            NativeAudioController.OnTrackEnded = () =>
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await NextTrack();
                });
            };
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCurrentTrack))]
        private Track _currentTrack;

        public bool HasCurrentTrack => CurrentTrack != null;

        [ObservableProperty]
        private bool _isDraggingSlider;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
        [NotifyPropertyChangedFor(nameof(PlayPauseImage))]
        private bool _isPlaying;

        public string PlayPauseIcon => IsPlaying ? "⏸️" : "▶️";
        public string PlayPauseImage => IsPlaying ? "pause.png" : "play.png";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PositionText))]
        [NotifyPropertyChangedFor(nameof(RemainingTimeText))]
        private int _position;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DurationText))]
        [NotifyPropertyChangedFor(nameof(SliderMaximum))]
        [NotifyPropertyChangedFor(nameof(RemainingTimeText))]
        private int _duration;

        public string PositionText => FormatTime(Position);
        public string DurationText => FormatTime(Duration);
        public int SliderMaximum => Duration > 0 ? Duration : 1;

        public string RemainingTimeText
        {
            get
            {
                int remainingMs = Duration - Position;
                if (remainingMs < 0) remainingMs = 0;
                return "-" + FormatTime(remainingMs);
            }
        }

        private string FormatTime(int ms)
        {
            if (ms <= 0) return "0:00";
            var time = TimeSpan.FromMilliseconds(ms);
            if (time.Hours > 0)
            {
                return time.ToString(@"h\:mm\:ss");
            }
            return time.ToString(@"m\:ss");
        }

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

        public System.Collections.ObjectModel.ObservableCollection<Track> PlayQueue { get; } = new System.Collections.ObjectModel.ObservableCollection<Track>();
        private int _currentQueueIndex = -1;

        public void SetQueue(System.Collections.Generic.IEnumerable<Track> tracks)
        {
            PlayQueue.Clear();
            foreach (var t in tracks)
            {
                PlayQueue.Add(t);
            }
        }

        [RelayCommand]
        private async Task PlayTrack(Track track)
        {
            if (track == null) return;

            int idx = -1;
            for (int i = 0; i < PlayQueue.Count; i++)
            {
                if (PlayQueue[i].VideoId == track.VideoId)
                {
                    idx = i;
                    break;
                }
            }

            if (idx != -1)
            {
                _currentQueueIndex = idx;
            }
            else
            {
                PlayQueue.Clear();
                PlayQueue.Add(track);
                _currentQueueIndex = 0;
            }
            
            CurrentTrack = track;
            IsPlaying = true;
            Position = 0;
            Duration = 0;
            
            // Try playing local downloaded file first
            DownloadManager.Initialize();
            string? localPath = DownloadManager.GetLocalPath(track.VideoId);
            if (!string.IsNullOrEmpty(localPath))
            {
                NativeAudioController.RequestPlay(localPath, track.Title, track.Uploader, track.ThumbnailUrl, track.VideoId);
                return;
            }

            // Otherwise, get stream URL
            string streamUrl = track.Url;
            if (string.IsNullOrEmpty(streamUrl) || !streamUrl.StartsWith("http") || streamUrl.Contains("youtube.com") || streamUrl.Contains("watch?v="))
            {
                var streamInfo = await _apiService.GetStreamAsync(track.VideoId);
                if (streamInfo != null && streamInfo.AudioStreams != null && streamInfo.AudioStreams.Count > 0)
                {
                    streamUrl = streamInfo.AudioStreams[0].Url;
                }
            }

            if (!string.IsNullOrEmpty(streamUrl) && streamUrl.StartsWith("http") && !streamUrl.Contains("watch?v="))
            {
                NativeAudioController.RequestPlay(streamUrl, track.Title, track.Uploader, track.ThumbnailUrl, track.VideoId);
            }
            else
            {
                IsPlaying = false;
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (Application.Current?.MainPage != null)
                    {
                        await Application.Current.MainPage.DisplayAlert("Stream no disponible", "Los servidores de extracción han bloqueado temporalmente esta canción por copyright. Intenta con otra.", "OK");
                    }
                    await NextTrack(); // Saltar a la siguiente si esta falla
                });
            }
        }

        [RelayCommand]
        private void Seek(double value)
        {
            NativeAudioController.RequestSeek((int)value);
        }

        [RelayCommand]
        private async Task NextTrack()
        {
            if (PlayQueue.Count <= 1 || _currentQueueIndex == -1)
            {
                if (CurrentTrack != null)
                {
                    var streamInfo = await _apiService.GetStreamAsync(CurrentTrack.VideoId);
                    if (streamInfo != null && streamInfo.RelatedStreams != null && streamInfo.RelatedStreams.Count > 0)
                    {
                        var next = streamInfo.RelatedStreams[0];
                        await PlayTrack(next);
                    }
                }
                return;
            }

            _currentQueueIndex = (_currentQueueIndex + 1) % PlayQueue.Count;
            var nextTrack = PlayQueue[_currentQueueIndex];
            await PlayTrack(nextTrack);
        }

        [RelayCommand]
        private async Task PreviousTrack()
        {
            if (PlayQueue.Count <= 1 || _currentQueueIndex == -1) return;

            _currentQueueIndex--;
            if (_currentQueueIndex < 0) _currentQueueIndex = PlayQueue.Count - 1;
            
            var prevTrack = PlayQueue[_currentQueueIndex];
            await PlayTrack(prevTrack);
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

            DownloadManager.Initialize();
            await DownloadManager.DownloadTrackAsync(
                track.VideoId, 
                streamUrl, 
                track.Title, 
                track.Uploader, 
                track.ThumbnailUrl
            );
        }

        [RelayCommand]
        private async Task ToggleFavorite()
        {
            if (CurrentTrack == null) return;
            await _apiService.AddFavoriteAsync(CurrentTrack);
        }
    }
}
