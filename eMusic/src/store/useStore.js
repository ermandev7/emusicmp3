import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { getStream } from '../api/piped';

export const useStore = create(
  persist(
    (set, get) => ({
      currentTrack: null,
      isPlaying: false,
      queue: [],
      currentIndex: -1,
      themeColor: '#121212',
      searchQuery: '',
      searchResults: [],
      isLoading: false,
      activeTab: 'home',
      isNextPrepared: false,
      isCrossfadingTriggered: false,
      nextStreamInfo: null,
      favorites: [],
      recentTracks: [],
      downloads: [], // Stores metadata of downloaded tracks
      downloadingId: null, // Tracks which track is currently downloading
      playlists: [], // Custom user playlists { id, name, tracks: [] }
      audioQuality: 'high', // 'high' or 'standard'

      addToRecents: (track) => set((state) => {
        const filtered = state.recentTracks.filter(t => t.id !== track.id);
        return { recentTracks: [track, ...filtered].slice(0, 15) };
      }),

      toggleFavorite: (track) => set((state) => {
        const trackId = track.id || (track.url && track.url.match(/[?&]v=([^&]{11})/) ? track.url.match(/[?&]v=([^&]{11})/)[1] : null);
        if (!trackId) return state;

        const isFavorite = state.favorites.some(f => f.id === trackId);
        if (isFavorite) {
          return { favorites: state.favorites.filter(f => f.id !== trackId) };
        } else {
          return { 
            favorites: [...state.favorites, {
              id: trackId,
              title: track.title,
              thumbnail: track.thumbnail,
              uploader: track.uploader || track.uploaderName || 'Desconocido',
              url: `/watch?v=${trackId}`,
              type: 'stream'
            }] 
          };
        }
      }),

      setDownloadingId: (id) => set({ downloadingId: id }),
      
      addDownloadInfo: (track) => set((state) => {
        const trackId = track.id || (track.url && track.url.match(/[?&]v=([^&]{11})/) ? track.url.match(/[?&]v=([^&]{11})/)[1] : null);
        if (!trackId) return state;
        if (state.downloads.some(d => d.id === trackId)) return state;
        
        return {
          downloads: [...state.downloads, {
            id: trackId,
            title: track.title,
            thumbnail: track.thumbnail,
            uploader: track.uploader || track.uploaderName || 'Desconocido',
            url: `/watch?v=${trackId}`,
            type: 'stream'
          }]
        };
      }),

      removeDownloadInfo: (id) => set((state) => ({
        downloads: state.downloads.filter(d => d.id !== id)
      })),

      createPlaylist: (name) => set((state) => ({
        playlists: [...state.playlists, { id: Date.now().toString(), name, tracks: [] }]
      })),

      deletePlaylist: (id) => set((state) => ({
        playlists: state.playlists.filter(p => p.id !== id)
      })),

      addToPlaylist: (playlistId, track) => set((state) => {
        const trackId = track.id || (track.url && track.url.match(/[?&]v=([^&]{11})/) ? track.url.match(/[?&]v=([^&]{11})/)[1] : null);
        if (!trackId) return state;
        return {
          playlists: state.playlists.map(p => {
            if (p.id === playlistId) {
              if (p.tracks.some(t => t.id === trackId)) return p;
              return { ...p, tracks: [...p.tracks, {
                id: trackId, title: track.title, thumbnail: track.thumbnail, 
                uploader: track.uploader || track.uploaderName || 'Desconocido',
                url: `/watch?v=${trackId}`, type: 'stream'
              }]};
            }
            return p;
          })
        };
      }),

      removeFromPlaylist: (playlistId, trackId) => set((state) => ({
        playlists: state.playlists.map(p => 
          p.id === playlistId ? { ...p, tracks: p.tracks.filter(t => t.id !== trackId) } : p
        )
      })),

      setAudioQuality: (quality) => set({ audioQuality: quality }),
      setThemeColor: (color) => set({ themeColor: color }),
      setCurrentTrack: (track) => set({ currentTrack: track, isPlaying: true }),
      setIsPlaying: (isPlaying) => set({ isPlaying }),
      setSearchQuery: (query) => set({ searchQuery: query }),
      setSearchResults: (results) => set({ searchResults: results }),
      setIsLoading: (isLoading) => set({ isLoading }),
      setActiveTab: (tab) => set({ activeTab: tab }),
      setCrossfadeTriggered: (val) => set({ isCrossfadingTriggered: val }),

      prepareNextTrack: async () => {
        const { currentIndex, queue, isNextPrepared } = get();
        if (isNextPrepared) return;

        let targetTrack = null;
        if (currentIndex < queue.length - 1) {
          targetTrack = queue[currentIndex + 1];
        } else {
          return; // Can't crossfade if there's no next track
        }

        if (targetTrack) {
          let videoId = targetTrack.id;
          if (!videoId && targetTrack.url) {
            const match = targetTrack.url.match(/[?&]v=([^&]{11})/);
            if (match) videoId = match[1];
          }

          if (videoId) {
             set({ isNextPrepared: true });
             const isDownloaded = get().downloads.some(d => d.id === videoId);
             let finalUrl = targetTrack.url;
             let streamInfo = targetTrack;
             
             if (!isDownloaded) {
                try {
                  streamInfo = await getStream(videoId);
                  if (streamInfo) finalUrl = streamInfo.url;
                } catch { return; }
             }
             
             if (window.isNativeApp) {
                set({ nextStreamInfo: streamInfo });
                const u = encodeURIComponent(finalUrl);
                const t = encodeURIComponent(streamInfo.title || '');
                const a = encodeURIComponent(streamInfo.uploader || '');
                const img = encodeURIComponent(streamInfo.thumbnail || '');
                window.sendNativeCommand(`emusic://preparenext?url=${u}&title=${t}&artist=${a}&thumb=${img}&id=${videoId}`);
             }
          }
        }
      },

      triggerCrossfade: () => {
        const { nextStreamInfo, currentIndex, isCrossfadingTriggered } = get();
        if (isCrossfadingTriggered || !nextStreamInfo) return;
        
        set({ isCrossfadingTriggered: true, isNextPrepared: false });
        
        if (window.isNativeApp) {
           window.sendNativeCommand('emusic://startcrossfade');
        }
        
        set({ 
           currentTrack: nextStreamInfo, 
           currentIndex: currentIndex + 1,
           nextStreamInfo: null
        });
      },

      completeNativeCrossfade: (title, artist, thumb) => {
        const { currentIndex, nextStreamInfo } = get();
        
        // Si C# hizo un fetch nativo, nextStreamInfo será null
        // Así que usamos la info que C# nos mandó
        let newTrack = nextStreamInfo;
        if (!newTrack && title && artist) {
            newTrack = {
                title: title,
                uploader: artist,
                thumbnail: thumb,
                url: "native_background_playback",
                id: "native_" + Date.now()
            };
        }

        if (!newTrack) {
          get().playNext();
          return;
        }

        set({ 
           isCrossfadingTriggered: true, 
           isNextPrepared: false,
           currentTrack: newTrack, 
           currentIndex: currentIndex + 1,
           nextStreamInfo: null
        });
      },

      setQueueAndPlay: async (queue, startIndex) => {
        const targetVideo = queue[startIndex];
        // Ensure tracks have either an ID or a parsable 11-char YouTube URL
        const validQueue = queue.filter(t => t.id || (t.url && t.url.match(/[?&]v=([^&]{11})/)));
        let newIndex = validQueue.indexOf(targetVideo);
        if (newIndex === -1) newIndex = 0;

        if (validQueue.length === 0) {
          set({ isLoading: false });
          return;
        }

        set({ queue: validQueue, currentIndex: newIndex, isLoading: true });
        await get().playQueueIndex(newIndex);
      },

      searchAndPlay: async (query) => {
        set({ isLoading: true });
        try {
          const res = await fetch(`https://api.emusicmp3.duckdns.org/search?q=${encodeURIComponent(query)}&filter=music_songs`);
          const data = await res.json();
          const items = data.items || [];
          if (items.length > 0) {
            get().setQueueAndPlay(items, 0);
          } else {
            set({ isLoading: false });
          }
        } catch (e) {
          console.error(e);
          set({ isLoading: false });
        }
      },

      playQueueIndex: async (index) => {
        const { queue } = get();
        if (index >= 0 && index < queue.length) {
          set({ currentIndex: index, isLoading: true });
          const video = queue[index];
          
          let videoId = video.id;
          if (!videoId && video.url) {
            const match = video.url.match(/[?&]v=([^&]{11})/);
            if (match) videoId = match[1];
          }

          if (videoId) {
            set({ isNextPrepared: false, isCrossfadingTriggered: false, nextStreamInfo: null });
            try {
              // CHECK OFFLINE STORAGE FIRST
              const isDownloaded = get().downloads.some(d => d.id === videoId);
              if (isDownloaded) {
                const { getTrackBlobUrl } = await import('./offline.js');
                const offlineUrl = await getTrackBlobUrl(videoId);
                if (offlineUrl) {
                  const trackData = get().downloads.find(d => d.id === videoId) || video;
                  const streamInfo = { ...trackData, url: offlineUrl, isOffline: true };
                  set({ currentTrack: streamInfo, isPlaying: true, isLoading: false });
                  get().addToRecents(streamInfo);
                  return; // Don't call Piped API
                }
              }

              // IF NOT OFFLINE, FETCH FROM PIPED
              const streamInfo = await getStream(videoId);
              if (streamInfo) {
                set({ currentTrack: streamInfo, isPlaying: true, isLoading: false });
                get().addToRecents(streamInfo);
              } else {
                set({ isLoading: false });
                get().playNext();
              }
            } catch {
              set({ isLoading: false });
              get().playNext();
            }
          } else {
            set({ isLoading: false });
            get().playNext();
          }
        }
      },

      playNext: () => {
        const { currentIndex, queue, currentTrack } = get();
        if (currentIndex < queue.length - 1) {
          get().playQueueIndex(currentIndex + 1);
        } else {
          // If queue ended, Auto-Play related tracks!
          if (currentTrack && currentTrack.relatedStreams && currentTrack.relatedStreams.length > 0) {
            const newTracks = currentTrack.relatedStreams.filter(t => t.type === 'stream');
            if (newTracks.length > 0) {
              set({ queue: [...queue, ...newTracks] });
              get().playQueueIndex(currentIndex + 1);
              return;
            }
          }
          
          // If no relatedStreams or it was empty, do a fallback search based on artist
          if (currentTrack) {
             let query = (currentTrack.uploader || currentTrack.title).replace(/ - Topic/ig, '').replace(/VEVO/ig, '').trim();
             fetch(`https://api.emusicmp3.duckdns.org/search?q=${encodeURIComponent(query)}&filter=music_songs`)
               .then(res => res.json())
               .then(data => {
                 const items = data.items || [];
                 // Filter out the current track so we don't just repeat it
                 const newTracks = items.filter(t => t.url !== currentTrack.url);
                 if (newTracks.length > 0) {
                   set({ queue: [...queue, ...newTracks] });
                   get().playQueueIndex(currentIndex + 1);
                 } else {
                   set({ isPlaying: false });
                 }
               })
               .catch(e => {
                 console.error(e);
                 set({ isPlaying: false });
               });
             return;
          }
          
          set({ isPlaying: false });
        }
      },

      playPrevious: () => {
        const { currentIndex } = get();
        if (currentIndex > 0) {
          get().playQueueIndex(currentIndex - 1);
        }
      }
    }),
    {
      name: 'emusic-storage',
      partialize: (state) => ({ 
        favorites: state.favorites, 
        recentTracks: state.recentTracks,
        downloads: state.downloads,
        searchResults: state.searchResults,
        playlists: state.playlists,
        audioQuality: state.audioQuality
      }),
    }
  )
);

useStore.subscribe((state, prevState) => {
    if (state.queue !== prevState.queue || state.currentIndex !== prevState.currentIndex) {
        if (window.isNativeApp && window.sendNativeCommand) {
            const nextTracks = state.queue.slice(state.currentIndex + 1, state.currentIndex + 21);
            const ids = nextTracks.map(t => t.id).filter(id => id).join(',');
            window.sendNativeCommand(`emusic://setqueue?ids=${ids}`);
        }
    }
});
