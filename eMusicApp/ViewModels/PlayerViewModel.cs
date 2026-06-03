using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eMusicApp.Models;
using eMusicApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace eMusicApp.ViewModels
{
    public enum RepeatMode { None, One, All }

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

            NativeAudioController.OnBufferingChanged = (isBuffering) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsBuffering = isBuffering;
                });
            };
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCurrentTrack))]
        private Track _currentTrack;

        // Actualizado por HomeViewModel cuando ExoPlayer auto-avanza al siguiente track
        public void NotifyTrackStarted(Track track)
        {
            // Sincronizar índice de cola para que Next/Prev funcionen tras auto-avance
            for (int i = 0; i < PlayQueue.Count; i++)
            {
                if (PlayQueue[i].VideoId == track.VideoId)
                {
                    _currentQueueIndex = i;
                    break;
                }
            }
            CurrentTrack = track;
            IsPlaying = true;
            IsBuffering = false;
            Position = 0;
            IsFavorite = false; // reset optimista; se corrige con la llamada al API
            _ = CheckIsFavoriteAsync(track.VideoId);
        }

        private async Task CheckIsFavoriteAsync(string videoId)
        {
            IsFavorite = await _apiService.IsFavoriteAsync(videoId);
        }

        public bool HasCurrentTrack => CurrentTrack != null;

        [ObservableProperty]
        private bool _isDraggingSlider;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
        [NotifyPropertyChangedFor(nameof(PlayPauseImage))]
        private bool _isPlaying;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowLoadingSpinner))]
        [NotifyPropertyChangedFor(nameof(ShowPlayButton))]
        private bool _isBuffering;

        // Muestra spinner cuando está buscando la URL de stream O cuando ExoPlayer está en buffering
        public bool ShowLoadingSpinner => IsBuffering;
        public bool ShowPlayButton     => !IsBuffering;

        public string PlayPauseIcon => IsPlaying ? "⏸️" : "▶️";
        public string PlayPauseImage => IsPlaying ? "pause.png" : "play.png";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FavoriteIcon))]
        private bool _isFavorite;

        public string FavoriteIcon => IsFavorite ? "heart_filled.png" : "heart_outline.png";

        // ─── Shuffle ───
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShuffleColor))]
        private bool _isShuffled;
        private static readonly Color _colorActive   = Color.FromArgb("#1DB954");
        private static readonly Color _colorInactive = Color.FromArgb("#B3B3B3");
        public Color ShuffleColor => IsShuffled ? _colorActive : _colorInactive;

        [RelayCommand]
        private void ToggleShuffle() => IsShuffled = !IsShuffled;

        // ─── Repeat ───
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RepeatIcon))]
        [NotifyPropertyChangedFor(nameof(RepeatColor))]
        private RepeatMode _currentRepeatMode = RepeatMode.None;
        public string RepeatIcon  => CurrentRepeatMode == RepeatMode.One ? "❶" : "↻";
        public Color  RepeatColor => CurrentRepeatMode != RepeatMode.None ? _colorActive : _colorInactive;

        [RelayCommand]
        private void CycleRepeat() => CurrentRepeatMode = CurrentRepeatMode switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All  => RepeatMode.One,
            _               => RepeatMode.None
        };

        private static readonly Random _random = new Random();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PositionText))]
        [NotifyPropertyChangedFor(nameof(RemainingTimeText))]
        [NotifyPropertyChangedFor(nameof(ProgressFraction))]
        private int _position;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DurationText))]
        [NotifyPropertyChangedFor(nameof(SliderMaximum))]
        [NotifyPropertyChangedFor(nameof(RemainingTimeText))]
        [NotifyPropertyChangedFor(nameof(ProgressFraction))]
        private int _duration;

        public string PositionText => FormatTime(Position);
        public string DurationText => FormatTime(Duration);
        public int SliderMaximum => Duration > 0 ? Duration : 1;
        public double ProgressFraction => SliderMaximum > 1 ? (double)Position / SliderMaximum : 0.0;

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
            // Dejar que el nativo confirme el estado real vía OnPlaybackStateChanged
            if (IsPlaying)
                NativeAudioController.RequestPause();
            else
                NativeAudioController.RequestResume();
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
            IsBuffering = true; // Mostrar spinner mientras se obtiene la URL
            IsFavorite = false; // Se restablece; se puede verificar contra API si se quiere
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
                    streamUrl = streamInfo.AudioStreams.OrderByDescending(s => s.Bitrate).First().Url;
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
            // Repeat One: volver a reproducir la canción actual
            if (CurrentRepeatMode == RepeatMode.One && CurrentTrack != null)
            {
                await PlayTrack(CurrentTrack);
                return;
            }

            if (PlayQueue.Count <= 1 || _currentQueueIndex == -1)
            {
                // Sin cola: buscar relacionadas como siguiente
                if (CurrentTrack != null)
                {
                    var streamInfo = await _apiService.GetStreamAsync(CurrentTrack.VideoId);
                    if (streamInfo?.RelatedStreams?.Count > 0)
                        await PlayTrack(streamInfo.RelatedStreams[0]);
                }
                return;
            }

            if (IsShuffled)
            {
                // Shuffle: índice aleatorio diferente al actual
                int next;
                do { next = _random.Next(PlayQueue.Count); }
                while (next == _currentQueueIndex && PlayQueue.Count > 1);
                _currentQueueIndex = next;
            }
            else
            {
                int next = _currentQueueIndex + 1;
                if (next >= PlayQueue.Count)
                {
                    if (CurrentRepeatMode == RepeatMode.All)
                    {
                        next = 0;
                    }
                    else
                    {
                        // Fin de cola sin repeat: buscar relacionadas
                        if (CurrentTrack != null)
                        {
                            var streamInfo = await _apiService.GetStreamAsync(CurrentTrack.VideoId);
                            if (streamInfo?.RelatedStreams?.Count > 0)
                                await PlayTrack(streamInfo.RelatedStreams[0]);
                        }
                        return;
                    }
                }
                _currentQueueIndex = next;
            }

            await PlayTrack(PlayQueue[_currentQueueIndex]);
        }

        [RelayCommand]
        private async Task PreviousTrack()
        {
            // Si llevamos más de 3s: reiniciar la canción actual (comportamiento Spotify)
            if (Position > 3000)
            {
                NativeAudioController.RequestSeek(0);
                return;
            }

            if (PlayQueue.Count <= 1 || _currentQueueIndex == -1) return;

            _currentQueueIndex--;
            if (_currentQueueIndex < 0) _currentQueueIndex = PlayQueue.Count - 1;

            await PlayTrack(PlayQueue[_currentQueueIndex]);
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
                    streamUrl = streamInfo.AudioStreams.OrderByDescending(s => s.Bitrate).First().Url;
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
            if (IsFavorite)
            {
                IsFavorite = false;
                await _apiService.RemoveFavoriteAsync(CurrentTrack.VideoId);
            }
            else
            {
                IsFavorite = true;
                await _apiService.AddFavoriteAsync(CurrentTrack);
            }
        }
    }
}
