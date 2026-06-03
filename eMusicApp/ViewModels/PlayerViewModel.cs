using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eMusicApp.Models;
using eMusicApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace eMusicApp.ViewModels
{
    public enum RepeatMode { None, One, All }

    public partial class PlayerViewModel : ObservableObject
    {
        private readonly ApiService _apiService;
        private readonly RadioModeService _radioService;
        private readonly IAlbumColorService? _colorService;

        public PlayerViewModel(ApiService apiService, RadioModeService radioService)
        {
            _apiService = apiService;
            _radioService = radioService;
            _colorService = IPlatformApplication.Current?.Services.GetService<IAlbumColorService>();

            NativeAudioController.OnProgressUpdated = (posMs, durMs) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!IsDraggingSlider)
                    {
                        Duration = durMs;
                        Position = posMs;
                    }

                    // Pre-fetch de Last.fm al 50% de la canción para tener resultados listos
                    if (IsRadioMode && durMs > 0 && posMs >= durMs / 2 && CurrentTrack != null)
                    {
                        _ = _radioService.PrefetchSimilarAsync(
                            CurrentTrack.VideoId, CurrentTrack.Uploader, CurrentTrack.Title, _playedIds);
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
        private Track? _currentTrack;

        partial void OnCurrentTrackChanged(Track? value)
        {
            RebuildQueueItems();
            if (_colorService != null && !string.IsNullOrEmpty(value?.ThumbnailUrl))
                _ = UpdateDominantColorAsync(value.ThumbnailUrl);
        }

        private async Task UpdateDominantColorAsync(string imageUrl)
        {
            var color = await _colorService!.GetDominantColorAsync(imageUrl);
            if (color != null)
                MainThread.BeginInvokeOnMainThread(() => DominantColor = color);
        }

        // ─── Dynamic album art color ───
        [ObservableProperty]
        private Color _dominantColor = Color.FromArgb("#282828");

        // ─── Sleep timer ───
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSleepTimerActive))]
        [NotifyPropertyChangedFor(nameof(SleepTimerColor))]
        private string _sleepTimerText = "";

        public bool IsSleepTimerActive => !string.IsNullOrEmpty(SleepTimerText);
        public Color SleepTimerColor   => IsSleepTimerActive ? _colorActive : _colorInactive;

        private CancellationTokenSource? _sleepCts;

        public async Task SetSleepTimerAsync(int minutes)
        {
            _sleepCts?.Cancel();
            _sleepCts = null;
            SleepTimerText = "";

            if (minutes <= 0) return;

            var cts = new CancellationTokenSource();
            _sleepCts = cts;
            var endTime = DateTime.Now.AddMinutes(minutes);

            await Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var remaining = endTime - DateTime.Now;
                    if (remaining.TotalSeconds <= 0)
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            NativeAudioController.RequestPause();
                            SleepTimerText = "";
                        });
                        break;
                    }
                    int mins = (int)remaining.TotalMinutes;
                    int secs = remaining.Seconds;
                    MainThread.BeginInvokeOnMainThread(() =>
                        SleepTimerText = $"⏱ {mins}:{secs:00}");
                    try { await Task.Delay(1000, cts.Token); }
                    catch (TaskCanceledException) { break; }
                }
            }, CancellationToken.None);
        }

        // ─── Queue items with IsNowPlaying flag ───
        public System.Collections.ObjectModel.ObservableCollection<QueueItem> QueueItems { get; }
            = new System.Collections.ObjectModel.ObservableCollection<QueueItem>();

        public bool IsQueueEmpty => QueueItems.Count == 0;

        private void RebuildQueueItems()
        {
            string? currentId = CurrentTrack?.VideoId;
            QueueItems.Clear();
            foreach (var t in PlayQueue)
                QueueItems.Add(new QueueItem { Track = t, IsNowPlaying = t.VideoId == currentId });
            OnPropertyChanged(nameof(IsQueueEmpty));
        }

        [RelayCommand]
        private void RemoveFromQueue(Track track)
        {
            if (track == null) return;
            var item = PlayQueue.FirstOrDefault(t => t.VideoId == track.VideoId);
            if (item == null) return;
            int idx = PlayQueue.IndexOf(item);
            PlayQueue.Remove(item);
            if (_currentQueueIndex > idx) _currentQueueIndex--;
            RebuildQueueItems();
        }

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
            if (!string.IsNullOrEmpty(track.VideoId))
                _playedIds.Add(track.VideoId);
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

        // ─── Crossfade ───
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CrossfadeLabel))]
        [NotifyPropertyChangedFor(nameof(CrossfadeColor))]
        private int _crossfadeDuration = 0; // segundos: 0=off, 3, 5

        public string CrossfadeLabel => CrossfadeDuration == 0 ? "X-Fade" : $"X-Fade {CrossfadeDuration}s";
        public Color  CrossfadeColor => CrossfadeDuration > 0 ? _colorActive : _colorInactive;

        [RelayCommand]
        private void CycleCrossfade()
        {
            CrossfadeDuration = CrossfadeDuration switch
            {
                0 => 3,
                3 => 5,
                _ => 0
            };
            NativeAudioController.CrossfadeDurationMs = CrossfadeDuration * 1000;
        }

        // ─── Modo Radio ───
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RadioColor))]
        private bool _isRadioMode;

        public Color RadioColor => IsRadioMode ? _colorActive : _colorInactive;

        [RelayCommand]
        private void ToggleRadio() => IsRadioMode = !IsRadioMode;

        private static readonly Random _random = new Random();

        // IDs ya reproducidos — evita loops en radio mode
        private readonly HashSet<string> _playedIds = new HashSet<string>();

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
                PlayQueue.Add(t);
            RebuildQueueItems();
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
            if (!string.IsNullOrEmpty(track.VideoId))
                _playedIds.Add(track.VideoId);
            
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
                await FetchAndPlayRelatedAsync();
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
                    else if (IsRadioMode)
                    {
                        // Radio: añadir related tracks y continuar
                        await ExtendQueueWithRelatedAsync();
                        return;
                    }
                    else
                    {
                        await FetchAndPlayRelatedAsync();
                        return;
                    }
                }
                _currentQueueIndex = next;
            }

            await PlayTrack(PlayQueue[_currentQueueIndex]);
        }

        private async Task FetchAndPlayRelatedAsync()
        {
            if (CurrentTrack == null) return;

            // Tier 0: Last.fm — recomendaciones por similitud musical
            var lastFmTracks = _radioService.ConsumePrefetched(CurrentTrack.VideoId, _playedIds);
            if (lastFmTracks.Count == 0)
                lastFmTracks = await _radioService.GetSimilarTracksAsync(
                    CurrentTrack.Uploader, CurrentTrack.Title, _playedIds, 3);
            if (lastFmTracks.Count > 0)
            {
                await PlayTrack(lastFmTracks[0]);
                return;
            }

            // Tier 1: relatedStreams filtrando ya reproducidos
            var streamInfo = await _apiService.GetStreamAsync(CurrentTrack.VideoId);
            var next = streamInfo?.RelatedStreams?
                .FirstOrDefault(r => !string.IsNullOrEmpty(r.VideoId) && !_playedIds.Contains(r.VideoId));
            if (next != null)
            {
                await PlayTrack(next);
                return;
            }

            // Tier 2: buscar por artista
            if (!string.IsNullOrEmpty(CurrentTrack.Uploader))
            {
                var results = await _apiService.SearchTracksAsync(CurrentTrack.Uploader);
                next = results.FirstOrDefault(r => !string.IsNullOrEmpty(r.VideoId) && !_playedIds.Contains(r.VideoId));
                if (next != null)
                {
                    await PlayTrack(next);
                    return;
                }
            }

            // Tier 3: cualquier related (aunque sea repetida)
            next = streamInfo?.RelatedStreams?.FirstOrDefault(r => !string.IsNullOrEmpty(r.VideoId) && r.VideoId != CurrentTrack.VideoId);
            if (next != null) await PlayTrack(next);
        }

        private async Task ExtendQueueWithRelatedAsync()
        {
            if (CurrentTrack == null) return;

            var newTracks = new List<Track>();
            var queueIds = new HashSet<string>(PlayQueue.Select(t => t.VideoId));

            // Tier 0: Last.fm — recomendaciones por similitud musical (pre-fetched o on-demand)
            var lastFmTracks = _radioService.ConsumePrefetched(CurrentTrack.VideoId, _playedIds);
            if (lastFmTracks.Count == 0)
                lastFmTracks = await _radioService.GetSimilarTracksAsync(
                    CurrentTrack.Uploader, CurrentTrack.Title, _playedIds, 5);
            newTracks.AddRange(lastFmTracks.Where(t => !queueIds.Contains(t.VideoId)));

            // Tier 1: relatedStreams filtrando ya reproducidos y ya en cola
            if (newTracks.Count < 3)
            {
                var streamInfo = await _apiService.GetStreamAsync(CurrentTrack.VideoId);
                if (streamInfo?.RelatedStreams?.Count > 0)
                {
                    var related = streamInfo.RelatedStreams
                        .Where(r => !string.IsNullOrEmpty(r.VideoId)
                                 && !_playedIds.Contains(r.VideoId)
                                 && !queueIds.Contains(r.VideoId)
                                 && !newTracks.Any(n => n.VideoId == r.VideoId))
                        .Take(8 - newTracks.Count)
                        .ToList();
                    newTracks.AddRange(related);
                }
            }

            // Tier 2: Búsqueda por artista
            if (newTracks.Count < 3 && !string.IsNullOrEmpty(CurrentTrack.Uploader))
            {
                var results = await _apiService.SearchTracksAsync(CurrentTrack.Uploader);
                var extraTracks = results
                    .Where(r => !string.IsNullOrEmpty(r.VideoId)
                             && !_playedIds.Contains(r.VideoId)
                             && !queueIds.Contains(r.VideoId)
                             && !newTracks.Any(n => n.VideoId == r.VideoId))
                    .Take(8 - newTracks.Count)
                    .ToList();
                newTracks.AddRange(extraTracks);
            }

            if (newTracks.Count > 0)
            {
                foreach (var r in newTracks)
                    PlayQueue.Add(r);
                _currentQueueIndex++;
                RebuildQueueItems();
                await PlayTrack(PlayQueue[_currentQueueIndex]);
            }
            else
            {
                // Tier 3: limpiar historial y reintentar
                _playedIds.Clear();
                var streamInfo = await _apiService.GetStreamAsync(CurrentTrack.VideoId);
                if (streamInfo?.RelatedStreams?.Count > 0)
                {
                    var fallback = streamInfo.RelatedStreams
                        .Where(r => !string.IsNullOrEmpty(r.VideoId) && r.VideoId != CurrentTrack.VideoId)
                        .Take(8).ToList();
                    foreach (var r in fallback)
                        PlayQueue.Add(r);
                    _currentQueueIndex++;
                    RebuildQueueItems();
                    await PlayTrack(PlayQueue[_currentQueueIndex]);
                }
            }
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
