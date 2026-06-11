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
        private readonly IAlbumColorService? _colorService;

        public PlayerViewModel(ApiService apiService)
        {
            _apiService = apiService;
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
                    try { await NextTrack(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Player] OnTrackEnded error: {ex.Message}"); }
                });
            };

            NativeAudioController.OnBufferingChanged = (isBuffering) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    // No dejar que el nativo quite el buffering mientras estamos obteniendo la URL
                    if (_isFetchingStream && !isBuffering) return;
                    IsBuffering = isBuffering;
                });
            };
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCurrentTrack))]
        private Track? _currentTrack;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DownloadButtonColor))]
        private bool _isCurrentTrackDownloaded;

        public Color DownloadButtonColor => IsCurrentTrackDownloaded ? _colorActive : _colorInactive;

        private void RefreshDownloadState()
        {
            DownloadManager.Initialize();
            IsCurrentTrackDownloaded = CurrentTrack != null
                && !string.IsNullOrEmpty(CurrentTrack.VideoId)
                && DownloadManager.IsTrackDownloaded(CurrentTrack.VideoId);
        }

        partial void OnCurrentTrackChanged(Track? value)
        {
            RebuildQueueItems();
            RefreshDownloadState();
            if (_colorService != null && !string.IsNullOrEmpty(value?.HqThumbnailUrl))
                _ = UpdateDominantColorAsync(value.HqThumbnailUrl);
        }

        private async Task UpdateDominantColorAsync(string imageUrl)
        {
            try
            {
                var color = await _colorService?.GetDominantColorAsync(imageUrl);
                if (color != null)
                    MainThread.BeginInvokeOnMainThread(() => DominantColor = color);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Player] Error getting dominant color: {ex.Message}");
            }
        }

        // ─── Modo radio por género ───
        private bool _isPlayingFromGenre;
        private string? _activeGenre;
        public string? ActiveGenre
        {
            get => _activeGenre;
            private set { if (SetProperty(ref _activeGenre, value)) OnPropertyChanged(nameof(IsGenreRadioActive)); }
        }
        public bool IsGenreRadioActive => _activeGenre != null;

        /// <summary>
        /// Lista de géneros disponibles (keys del diccionario _genreKeywords).
        /// </summary>
        public static IReadOnlyList<string> AvailableGenres { get; } = new[]
        {
            "salsa", "bachata", "reggaeton", "cumbia", "merengue", "vallenato",
            "rock", "pop", "rap", "trap", "balada", "ranchera", "corrido",
            "electronic", "jazz", "blues", "reggae", "clasica", "kpop", "r&b"
        };

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
            _sleepCts?.Dispose();
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
            // No forzar IsBuffering=false aquí; dejar que el nativo lo reporte
            // cuando realmente empiece a sonar (STATE_READY)
            Position = 0;
            if (!string.IsNullOrEmpty(track.VideoId))
                MarkAsPlayed(track);
        }

        public bool HasCurrentTrack => CurrentTrack != null;

        [ObservableProperty]
        private bool _isDraggingSlider;

        // Flag: estamos obteniendo la URL del stream → no dejar que el nativo quite IsBuffering
        private bool _isFetchingStream;

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

        // Favoritos eliminados — solo se mantiene descargas locales

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

            // Si el usuario elige un track manualmente (no viene del género radio), desactivar modo género
            if (_activeGenre != null && !_isPlayingFromGenre)
                ActiveGenre = null;

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
                // Añadir al final en vez de borrar toda la cola
                PlayQueue.Add(track);
                _currentQueueIndex = PlayQueue.Count - 1;
            }
            
            CurrentTrack = track;
            IsPlaying = true;
            IsBuffering = true;
            _isFetchingStream = true; // Bloquear que el nativo quite IsBuffering
            Position = 0;
            Duration = 0;
            if (!string.IsNullOrEmpty(track.VideoId))
                MarkAsPlayed(track);

            try
            {
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
                    IsBuffering = false;
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        try
                        {
                            if (Application.Current?.MainPage != null)
                                await Application.Current.MainPage.DisplayAlert("Stream no disponible", "Los servidores de extracción han bloqueado temporalmente esta canción por copyright. Intenta con otra.", "OK");
                            await NextTrack();
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Player] Error handling failed stream: {ex.Message}"); }
                    });
                }
            }
            finally
            {
                _isFetchingStream = false;
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

        // ── Filtro inteligente de tracks ──

        // Títulos ya reproducidos (normalizados) para evitar covers/remixes
        private readonly HashSet<string> _playedTitles = new HashSet<string>();

        /// <summary>
        /// Normaliza un título para comparar duplicados: quita paréntesis, "official video",
        /// "live", "cover", "karaoke", "remix", "lyrics", etc.
        /// "Devuélveme La Vida (Official Video)" y "Devuélveme La Vida - Cover" → "devuelveme la vida"
        /// </summary>
        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            var t = title.ToLowerInvariant();
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\([^)]*\)", "");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\[[^\]]*\]", "");
            var dashIdx = t.IndexOf(" - ");
            if (dashIdx > 3) t = t.Substring(0, dashIdx);
            foreach (var kw in new[] {
                "official", "video", "audio", "lyric", "lyrics", "live",
                "cover", "karaoke", "remix", "acoustic", "version", "hd", "4k", "ft.", "feat.",
                "remaster", "remastered", "explicit",
                "letra", "letras", "en vivo", "vivo", "directo", "en directo",
                "completa", "completo", "concierto", "acustico", "acústico",
                "tema original", "sencillo", "estreno" })
                t = t.Replace(kw, "");
            t = t.Replace("á", "a").Replace("é", "e").Replace("í", "i").Replace("ó", "o").Replace("ú", "u").Replace("ñ", "n");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[^a-z0-9]", "");
            return t;
        }

        /// <summary>
        /// Contains-match: "nuncamefaltes" matchea "nuncamefaltesenvivo" y viceversa.
        /// </summary>
        private bool IsTitleAlreadyPlayed(string norm)
        {
            if (string.IsNullOrEmpty(norm)) return false;
            foreach (var played in _playedTitles)
            {
                if (norm.Contains(played) || played.Contains(norm))
                    return true;
            }
            return false;
        }

        private bool IsGoodTrack(Track r, HashSet<string>? extraIds = null)
        {
            if (string.IsNullOrEmpty(r.VideoId)) return false;
            if (_playedIds.Contains(r.VideoId)) return false;
            if (extraIds != null && extraIds.Contains(r.VideoId)) return false;
            var norm = NormalizeTitle(r.Title);
            if (string.IsNullOrEmpty(norm)) return false;
            if (IsTitleAlreadyPlayed(norm)) return false;
            var lower = r.Title.ToLowerInvariant();
            if (lower.Contains("karaoke") || lower.Contains("instrumental")
                || lower.Contains("cover") || lower.Contains("tribute")
                || lower.Contains("pista") || lower.Contains("backing"))
                return false;
            return true;
        }

        private void MarkAsPlayed(Track track)
        {
            _playedIds.Add(track.VideoId);
            _playedTitles.Add(NormalizeTitle(track.Title));
        }

        // ── Detección de género y queries inteligentes ──

        private static readonly Dictionary<string, string[]> _genreKeywords = new()
        {
            ["salsa"] = new[] { "salsa éxitos", "salsa romántica mix", "salsa clásica", "salsa brava mix", "lo mejor de la salsa" },
            ["bachata"] = new[] { "bachata éxitos", "bachata romántica mix", "bachata sensual", "lo mejor de la bachata" },
            ["reggaeton"] = new[] { "reggaeton éxitos 2024", "reggaeton mix", "perreo mix", "reggaeton viejo mix" },
            ["cumbia"] = new[] { "cumbia éxitos", "cumbia mix bailable", "cumbia clásica", "lo mejor de la cumbia" },
            ["merengue"] = new[] { "merengue éxitos", "merengue mix bailable", "merengue clásico" },
            ["vallenato"] = new[] { "vallenato éxitos", "vallenato romántico", "vallenato clásico mix" },
            ["rock"] = new[] { "rock en español éxitos", "rock clásico mix", "rock latino mix", "classic rock greatest hits" },
            ["pop"] = new[] { "pop éxitos 2024", "pop latino mix", "pop en español mix", "pop hits mix" },
            ["rap"] = new[] { "rap éxitos", "hip hop mix", "rap en español mix", "trap latino mix" },
            ["trap"] = new[] { "trap latino mix", "trap éxitos 2024", "trap mix" },
            ["balada"] = new[] { "baladas románticas mix", "baladas en español", "baladas de amor mix" },
            ["ranchera"] = new[] { "rancheras éxitos", "música mexicana mix", "mariachi éxitos", "regional mexicano mix" },
            ["corrido"] = new[] { "corridos tumbados mix", "corridos éxitos", "corridos mix 2024" },
            ["electronic"] = new[] { "electronic dance mix", "EDM mix 2024", "house music mix", "techno mix" },
            ["jazz"] = new[] { "jazz clásico", "smooth jazz mix", "jazz éxitos", "jazz instrumental" },
            ["blues"] = new[] { "blues éxitos", "blues clásico mix", "blues guitar mix" },
            ["reggae"] = new[] { "reggae éxitos", "reggae mix", "reggae en español mix" },
            ["clasica"] = new[] { "música clásica famosa", "classical music best", "piano clásico" },
            ["kpop"] = new[] { "kpop éxitos 2024", "kpop mix", "kpop hits playlist" },
            ["r&b"] = new[] { "r&b éxitos", "r&b mix", "soul music mix", "r&b classics" },
        };

        /// <summary>
        /// Detecta el género probable de un track por su título y artista.
        /// </summary>
        public static string? DetectGenre(string title, string artist)
        {
            var combined = $"{title} {artist}".ToLowerInvariant();
            foreach (var kv in _genreKeywords)
            {
                if (combined.Contains(kv.Key))
                    return kv.Key;
            }
            // Heurísticas adicionales por palabras clave comunes
            if (combined.Contains("reggaet") || combined.Contains("perreo")) return "reggaeton";
            if (combined.Contains("cumbi")) return "cumbia";
            if (combined.Contains("bachi") || combined.Contains("bachata")) return "bachata";
            if (combined.Contains("ranchera") || combined.Contains("mariachi") || combined.Contains("norteñ")) return "ranchera";
            if (combined.Contains("corrido") || combined.Contains("tumbado")) return "corrido";
            if (combined.Contains("romántic") || combined.Contains("amor") || combined.Contains("corazón")) return "balada";
            return null;
        }

        /// <summary>
        /// Construye queries de búsqueda variadas: primero por género, luego por artista.
        /// </summary>
        private static string[] BuildSmartQueries(string title, string artist)
        {
            var queries = new List<string>();
            var genre = DetectGenre(title, artist);

            if (genre != null && _genreKeywords.TryGetValue(genre, out var genreQueries))
            {
                // Tomar 2 queries aleatorias del género
                var shuffled = genreQueries.OrderBy(_ => _random.Next()).Take(2);
                queries.AddRange(shuffled);
            }

            // Agregar búsquedas por artista (pero variadas)
            if (!string.IsNullOrEmpty(artist))
            {
                queries.Add($"{artist} éxitos");
                queries.Add($"{artist} mix");
            }

            // Si no detectamos género, buscar genéricamente
            if (genre == null && !string.IsNullOrEmpty(title))
            {
                // Extraer posible género del título (ej: "Salsa Mix 2024" → buscar salsa)
                queries.Add($"música similar a {artist}");
            }

            return queries.ToArray();
        }


        /// <summary>
        /// Inicia modo radio por género: busca canciones, llena la cola y reproduce.
        /// </summary>
        public async Task PlayGenreAsync(string genre)
        {
            if (!_genreKeywords.TryGetValue(genre, out var genreQueries)) return;

            ActiveGenre = genre;
            _playedIds.Clear();
            _playedTitles.Clear();
            PlayQueue.Clear();
            _currentQueueIndex = -1;

            // Buscar con queries aleatorias del género
            var shuffled = genreQueries.OrderBy(_ => _random.Next()).ToArray();
            var tracks = new List<Track>();

            foreach (var query in shuffled)
            {
                if (tracks.Count >= 10) break;
                var results = await _apiService.SearchTracksAsync(query);
                foreach (var r in results)
                {
                    if (tracks.Count >= 10) break;
                    if (IsGoodTrack(r) && !tracks.Any(t => t.VideoId == r.VideoId))
                        tracks.Add(r);
                }
            }

            if (tracks.Count == 0) { ActiveGenre = null; return; }

            foreach (var t in tracks)
            {
                PlayQueue.Add(t);
                MarkAsPlayed(t);
            }
            _currentQueueIndex = 0;
            RebuildQueueItems();
            _isPlayingFromGenre = true;
            await PlayTrack(PlayQueue[0]);
            _isPlayingFromGenre = false;
        }


        /// <summary>
        /// Search-first con ratio 2:1: 2 canciones del género (otros artistas), 1 del mismo artista.
        /// Agrega a PlayQueue y reproduce la primera.
        /// </summary>
        private async Task FetchAndPlayRelatedAsync()
        {
            if (CurrentTrack == null) return;

            var newTracks = await SmartSearchTracksAsync(CurrentTrack.Title, CurrentTrack.Uploader);

            if (newTracks.Count > 0)
            {
                foreach (var t in newTracks)
                {
                    PlayQueue.Add(t);
                    MarkAsPlayed(t);
                }
                _currentQueueIndex = PlayQueue.Count - newTracks.Count;
                RebuildQueueItems();
                _isPlayingFromGenre = _activeGenre != null;
                await PlayTrack(PlayQueue[_currentQueueIndex]);
                _isPlayingFromGenre = false;
                return;
            }

            // Último recurso: limpiar historial y reintentar
            _playedIds.Clear();
            _playedTitles.Clear();
            if (CurrentTrack != null) MarkAsPlayed(CurrentTrack);
            newTracks = await SmartSearchTracksAsync(CurrentTrack.Title, CurrentTrack.Uploader);
            if (newTracks.Count > 0)
            {
                foreach (var t in newTracks)
                {
                    PlayQueue.Add(t);
                    MarkAsPlayed(t);
                }
                _currentQueueIndex = PlayQueue.Count - newTracks.Count;
                RebuildQueueItems();
                _isPlayingFromGenre = _activeGenre != null;
                await PlayTrack(PlayQueue[_currentQueueIndex]);
                _isPlayingFromGenre = false;
            }
        }

        /// <summary>
        /// Extiende la cola cuando llega al final — agrega más tracks con ratio 2:1.
        /// </summary>
        private async Task ExtendQueueWithRelatedAsync()
        {
            if (CurrentTrack == null) return;

            var newTracks = await SmartSearchTracksAsync(CurrentTrack.Title, CurrentTrack.Uploader);

            if (newTracks.Count > 0)
            {
                foreach (var t in newTracks)
                {
                    PlayQueue.Add(t);
                    MarkAsPlayed(t);
                }
                _currentQueueIndex++;
                RebuildQueueItems();
                _isPlayingFromGenre = _activeGenre != null;
                await PlayTrack(PlayQueue[_currentQueueIndex]);
                _isPlayingFromGenre = false;
            }
            else
            {
                // Limpiar historial y reintentar
                _playedIds.Clear();
                _playedTitles.Clear();
                if (CurrentTrack != null) MarkAsPlayed(CurrentTrack);
                var retry = await SmartSearchTracksAsync(CurrentTrack.Title, CurrentTrack.Uploader);
                if (retry.Count > 0)
                {
                    foreach (var t in retry)
                    {
                        PlayQueue.Add(t);
                        MarkAsPlayed(t);
                    }
                    _currentQueueIndex++;
                    RebuildQueueItems();
                    _isPlayingFromGenre = _activeGenre != null;
                    await PlayTrack(PlayQueue[_currentQueueIndex]);
                    _isPlayingFromGenre = false;
                }
            }
        }

        /// <summary>
        /// Búsqueda inteligente con ratio 2:1: 2 del género, 1 del artista.
        /// Devuelve hasta 9 tracks (6 género + 3 artista).
        /// </summary>
        private async Task<List<Track>> SmartSearchTracksAsync(string title, string artist)
        {
            var result = new List<Track>();
            var queueIds = new HashSet<string>(PlayQueue.Select(t => t.VideoId));

            var genreQueries = GetGenreQueries(title, artist);
            var artistQueries = GetArtistQueries(artist);

            int gi = 0, ai = 0;

            while (result.Count < 9 && (gi < genreQueries.Length || ai < artistQueries.Length))
            {
                // 2 del género (otros artistas)
                for (int k = 0; k < 2 && gi < genreQueries.Length; k++, gi++)
                {
                    var tracks = await _apiService.SearchTracksAsync(genreQueries[gi]);
                    foreach (var t in tracks)
                    {
                        if (result.Count >= 9) break;
                        if (IsGoodTrack(t, queueIds) && !result.Any(r => r.VideoId == t.VideoId))
                        {
                            result.Add(t);
                            queueIds.Add(t.VideoId);
                        }
                    }
                }

                // 1 del mismo artista
                if (ai < artistQueries.Length)
                {
                    var tracks = await _apiService.SearchTracksAsync(artistQueries[ai++]);
                    foreach (var t in tracks)
                    {
                        if (result.Count >= 9) break;
                        if (IsGoodTrack(t, queueIds) && !result.Any(r => r.VideoId == t.VideoId))
                        {
                            result.Add(t);
                            queueIds.Add(t.VideoId);
                            break; // Solo 1 del artista por query
                        }
                    }
                }
            }

            return result;
        }

        private string[] GetGenreQueries(string title, string artist)
        {
            if (_activeGenre != null && _genreKeywords.TryGetValue(_activeGenre, out var gq))
                return gq.OrderBy(_ => _random.Next()).ToArray();
            var genre = DetectGenre(title, artist);
            if (genre != null && _genreKeywords.TryGetValue(genre, out var gq2))
                return gq2.OrderBy(_ => _random.Next()).ToArray();
            return new[] { "música popular mix", "éxitos latinos 2024", "lo más escuchado" };
        }

        private static string[] GetArtistQueries(string artist)
        {
            if (string.IsNullOrEmpty(artist)) return Array.Empty<string>();
            return new[] { $"{artist} éxitos", $"{artist} mejores canciones", $"lo mejor de {artist}" };
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
                    var best = streamInfo.AudioStreams
                        .Where(s => s.Bitrate <= 128_000)
                        .OrderByDescending(s => s.Bitrate)
                        .FirstOrDefault()
                        ?? streamInfo.AudioStreams.OrderBy(s => s.Bitrate).First();
                    streamUrl = best.Url;
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
            RefreshDownloadState();
        }

    }
}
