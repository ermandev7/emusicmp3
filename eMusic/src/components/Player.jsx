import { useRef, useEffect, useState } from 'react';
import { useStore } from '../store/useStore';
import { Play, Pause, SkipBack, SkipForward, ChevronDown, Loader, FileText, PlusCircle, X } from 'lucide-react';
import { getColorSync } from 'colorthief';
import Lyrics from './Lyrics';

export default function Player() {
  const { currentTrack, isPlaying, setIsPlaying, playNext, playPrevious, themeColor, setThemeColor, isLoading } = useStore();
  const audioRef = useRef(null);
  const [progress, setProgress] = useState(0);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [isExpanded, setIsExpanded] = useState(false);
  const [showLyrics, setShowLyrics] = useState(false);
  const [showPlaylists, setShowPlaylists] = useState(false);
  const [touchStartY, setTouchStartY] = useState(null);
  const [swipeOffset, setSwipeOffset] = useState(0);
  const progressBarRef = useRef(null);
  const [isNative, setIsNative] = useState(!!window.isNativeApp);

  useEffect(() => {
    window.disableWebViewAudio = () => setIsNative(true);
    return () => delete window.disableWebViewAudio;
  }, []);

  useEffect(() => {
    if (audioRef.current) {
      if (isPlaying) {
        if (isNative && currentTrack?.url && !currentTrack.url.startsWith('blob:')) {
          if (useStore.getState().isCrossfadingTriggered) {
            useStore.getState().setCrossfadeTriggered(false);
          } else {
            const t = encodeURIComponent(currentTrack.title || '');
            const a = encodeURIComponent(currentTrack.uploader || currentTrack.artist || '');
            const img = encodeURIComponent(currentTrack.thumbnail || '');
            const u = encodeURIComponent(currentTrack.url);
            const trackId = encodeURIComponent(currentTrack.id || '');
            window.sendNativeCommand(`emusic://play?url=${u}&title=${t}&artist=${a}&thumb=${img}&id=${trackId}`);
          }
        } else {
          audioRef.current.play().catch(console.error);
        }
      } else {
        if (isNative) {
          window.sendNativeCommand(`emusic://pause`);
        } else {
          audioRef.current.pause();
        }
      }
    }
  }, [isPlaying, currentTrack, isNative]);

  // Expose a global function for C# to update progress
  useEffect(() => {
    window.updateNativeProgress = (posMs, durMs) => {
      const posSec = posMs / 1000;
      const durSec = durMs / 1000;
      setProgress((posSec / durSec) * 100 || 0);
      setCurrentTime(posSec);
      setDuration(durSec);

      const rem = durSec - posSec;
      const state = useStore.getState();
      
      // Preparar la siguiente canción casi inmediatamente (a los 2 segundos)
      if (posSec >= 2 && !state.isNextPrepared && rem > 10) {
          state.prepareNextTrack();
      }
      
      // El Crossfade ahora lo hace C# de forma independiente a los 25 segundos.
      // Ya no disparamos el crossfade desde JS para la app nativa.
      // Para la versión web (React puro), dejamos que termine sola o cruce a los 2s.
      if (!isNative && rem <= 2 && rem > 0 && state.isNextPrepared && !state.isCrossfadingTriggered) {
          state.triggerCrossfade();
      }
    };
    
    window.onNativeTrackEnded = () => {
      playNext();
    };
    
    window.onNativeCrossfadeCompleted = (title, artist, thumb) => {
      useStore.getState().completeNativeCrossfade(title, artist, thumb);
    };
    
    window.playNativeNext = () => {
      playNext();
    };

    window.playNativePrevious = () => {
      playPrevious();
    };

    window.playFromNativeSearch = (query) => {
      useStore.getState().searchAndPlay(query);
    };
    
    window.onNativePlaybackStateChanged = (isPlayingNative) => {
      useStore.getState().setIsPlaying(isPlayingNative);
    };

    window.onNativeDownloadComplete = () => {
      useStore.getState().setDownloadingId(null);
      window.sendNativeCommand('emusic://getdownloads');
    };

    window.onNativeDownloadDeleted = () => {
      window.sendNativeCommand('emusic://getdownloads');
    };

    window.onNativeDownloadsReceived = (jsonString) => {
      try {
        const data = JSON.parse(jsonString);
        // Map native DownloadedTrack properties to match our React store
        const mapped = data.map(d => ({
          id: d.Id,
          title: d.Title,
          uploader: d.Artist,
          thumbnail: d.ThumbUrl,
          url: `/watch?v=${d.Id}`,
          type: 'stream',
          isOffline: true
        }));
        useStore.setState({ downloads: mapped });
      } catch (e) {
        console.error("Error parsing native downloads", e);
      }
    };

    // Solicitar descargas iniciales al montar si es nativo
    if (isNative) {
      window.sendNativeCommand('emusic://getdownloads');
    }

    return () => {
      delete window.updateNativeProgress;
      delete window.onNativeTrackEnded;
      delete window.playNativeNext;
      delete window.playNativePrevious;
      delete window.playFromNativeSearch;
      delete window.onNativePlaybackStateChanged;
      delete window.onNativeDownloadComplete;
      delete window.onNativeDownloadDeleted;
      delete window.onNativeDownloadsReceived;
    };
  }, [playNext, playPrevious, isNative]);

  // Media Session API for Lock Screen Controls
  useEffect(() => {
    if ('mediaSession' in navigator && currentTrack) {
      navigator.mediaSession.metadata = new window.MediaMetadata({
        title: currentTrack.title,
        artist: currentTrack.uploader,
        album: 'eMusic',
        artwork: [
          { src: currentTrack.thumbnail, sizes: '512x512', type: 'image/jpeg' }
        ]
      });

      navigator.mediaSession.setActionHandler('play', () => setIsPlaying(true));
      navigator.mediaSession.setActionHandler('pause', () => setIsPlaying(false));
      navigator.mediaSession.setActionHandler('previoustrack', playPrevious);
      navigator.mediaSession.setActionHandler('nexttrack', playNext);
    }
  }, [currentTrack, setIsPlaying, playNext, playPrevious]);

  const handleTimeUpdate = () => {
    if (audioRef.current) {
      const p = (audioRef.current.currentTime / audioRef.current.duration) * 100;
      setProgress(p || 0);
      setCurrentTime(audioRef.current.currentTime);
      setDuration(audioRef.current.duration);
    }
  };

  const handleProgressClick = (e) => {
    if (progressBarRef.current) {
      const rect = progressBarRef.current.getBoundingClientRect();
      const clickX = e.clientX - rect.left;
      const width = rect.width;
      const percentage = Math.max(0, Math.min(1, clickX / width));
      
      if (shouldPlayInWebView) {
        if (audioRef.current) {
          audioRef.current.currentTime = percentage * audioRef.current.duration;
        }
      } else {
        const targetTimeMs = Math.floor(percentage * duration * 1000);
        if (isNative) {
          window.sendNativeCommand(`emusic://seek?position=${targetTimeMs}`);
        }
        setProgress(percentage * 100);
        setCurrentTime(percentage * duration);
      }
    }
  };

  const handleImageLoad = (e) => {
    try {
      const color = getColorSync(e.target);
      if (color) {
        const hex = color.hex();
        setThemeColor(hex);
        if (isNative) {
          window.sendNativeCommand(`emusic://color?hex=${hex.replace('#', '')}`);
        }
      }
    } catch (err) {
      console.error("No se pudo extraer el color de la carátula:", err);
    }
  };

  const formatTime = (time) => {
    if (!time || isNaN(time)) return "0:00";
    const mins = Math.floor(time / 60);
    const secs = Math.floor(time % 60);
    return `${mins}:${secs < 10 ? '0' : ''}${secs}`;
  };

  if (!currentTrack) return null;

  // Determine if the WebView audio tag should actually load the stream
  const shouldPlayInWebView = !isNative || (currentTrack.url && currentTrack.url.startsWith('blob:'));

  return (
    <>
      <audio 
        ref={audioRef} 
        src={shouldPlayInWebView ? currentTrack.url : ""} 
        onTimeUpdate={shouldPlayInWebView ? handleTimeUpdate : undefined}
        onEnded={shouldPlayInWebView ? playNext : undefined} 
        autoPlay={shouldPlayInWebView}
      />

      {/* Mini Player */}
      {!isExpanded && (
        <div 
          onClick={() => setIsExpanded(true)}
          style={{
            position: 'fixed', bottom: 'calc(var(--nav-height) + 10px)', left: '10px', right: '10px',
            height: '65px', borderRadius: '12px', zIndex: 50, display: 'flex', alignItems: 'center', padding: '0 15px',
            justifyContent: 'space-between', overflow: 'hidden',
            background: `linear-gradient(to right, rgba(30,30,30,0.95), ${themeColor}99)`,
            backdropFilter: 'blur(20px)',
            WebkitBackdropFilter: 'blur(20px)',
            border: '1px solid rgba(255, 255, 255, 0.1)',
            boxShadow: '0 8px 32px 0 rgba(0, 0, 0, 0.4)',
            cursor: 'pointer'
          }} className="animate-fade-in"
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: '12px', overflow: 'hidden', flex: 1 }}>
            <img 
              src={currentTrack.thumbnail} 
              crossOrigin="anonymous" 
              onLoad={handleImageLoad}
              alt="Cover" 
              className={isLoading ? "animate-pulse" : ""}
              style={{ width: '44px', height: '44px', borderRadius: '6px', objectFit: 'cover', boxShadow: '0 4px 8px rgba(0,0,0,0.3)', opacity: isLoading ? 0.5 : 1, transition: 'opacity 0.2s' }} 
            />
            <div style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden', paddingRight: '10px' }}>
              <span className="truncate-text" style={{ fontSize: '0.95rem', fontWeight: '600', color: 'white' }}>{isLoading ? 'Cargando...' : currentTrack.title}</span>
              <span className="truncate-text" style={{ fontSize: '0.8rem', color: '#e0e0e0' }}>{currentTrack.uploader}</span>
            </div>
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: '15px' }} onClick={(e) => e.stopPropagation()}>
            <button onClick={() => setIsPlaying(!isPlaying)} style={{ background: 'transparent', border: 'none', color: 'white', cursor: 'pointer', padding: 0, transition: 'transform 0.1s' }} className="active:scale-90">
              {isLoading ? <Loader size={28} color="white" className="spin-animation" /> : (isPlaying ? <Pause fill="white" size={28} /> : <Play fill="white" size={28} />)}
            </button>
            <button onClick={playNext} style={{ background: 'transparent', border: 'none', color: 'white', cursor: 'pointer', padding: 0, transition: 'transform 0.1s' }} className="active:scale-90">
              <SkipForward fill="white" size={22} />
            </button>
          </div>

          <div 
            style={{ position: 'absolute', bottom: 0, left: 0, right: 0, height: '3px', background: 'rgba(255,255,255,0.1)' }}
          >
            <div style={{ height: '100%', width: `${progress}%`, background: 'var(--accent)', transition: 'width 0.1s linear' }} />
          </div>
        </div>
      )}

      {/* Full Screen Player */}
      {isExpanded && (
        <div 
          onTouchStart={(e) => setTouchStartY(e.touches[0].clientY)}
          onTouchMove={(e) => {
            if (touchStartY === null) return;
            const currentY = e.touches[0].clientY;
            const diff = currentY - touchStartY;
            if (diff > 0) setSwipeOffset(diff);
          }}
          onTouchEnd={() => {
            if (swipeOffset > 100) setIsExpanded(false);
            setTouchStartY(null);
            setSwipeOffset(0);
          }}
          style={{
          position: 'fixed', top: 0, left: 0, right: 0, bottom: 0,
          background: `linear-gradient(to bottom, ${themeColor}F2, #121212)`,
          backdropFilter: 'blur(30px)',
          WebkitBackdropFilter: 'blur(30px)',
          zIndex: 100, display: 'flex', flexDirection: 'column',
          padding: '20px',
          touchAction: 'none',
          transform: `translateY(${swipeOffset}px)`,
          transition: swipeOffset > 0 ? 'none' : 'transform 0.3s cubic-bezier(0.2, 0.8, 0.2, 1)',
          animation: 'slideUp 0.3s cubic-bezier(0.2, 0.8, 0.2, 1)'
        }}>
          {/* Header */}
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: '30px' }}>
            <button onClick={() => setIsExpanded(false)} style={{ background: 'transparent', border: 'none', color: 'white', cursor: 'pointer', padding: '10px', transition: 'transform 0.1s' }} className="active:scale-90">
              <ChevronDown size={36} />
            </button>
            <span style={{ fontSize: '0.85rem', fontWeight: 'bold', color: 'white', letterSpacing: '1.5px', textShadow: '0 2px 4px rgba(0,0,0,0.5)' }}>REPRODUCIENDO AHORA</span>
            <button onClick={() => setShowLyrics(true)} style={{ background: 'transparent', border: 'none', color: 'white', cursor: 'pointer', padding: '10px', transition: 'transform 0.1s' }} className="active:scale-90">
              <FileText size={24} />
            </button>
          </div>

          {/* Album Art Big */}
          <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: '20px 0' }}>
            <img 
              src={currentTrack.thumbnail} 
              alt="Cover" 
              className={isLoading ? "animate-pulse" : ""}
              style={{ 
                width: '100%', maxWidth: '350px', aspectRatio: '1/1', 
                borderRadius: '16px', objectFit: 'cover', 
                boxShadow: '0 30px 60px rgba(0,0,0,0.8)',
                opacity: isLoading ? 0.5 : 1,
                transition: 'opacity 0.2s'
              }} 
            />
          </div>

          {/* Track Info */}
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '35px' }}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '8px', overflow: 'hidden', flex: 1 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
                <h2 className="truncate-text" style={{ fontSize: '1.8rem', fontWeight: 'bold', color: 'white', margin: 0, textShadow: '0 2px 8px rgba(0,0,0,0.4)' }}>
                  {isLoading ? 'Cargando...' : currentTrack.title}
                </h2>
                {useStore.getState().downloads.some(d => d.id === currentTrack.id) && (
                  <div style={{ background: 'var(--accent)', borderRadius: '50%', padding: '4px', display: 'flex', boxShadow: '0 2px 4px rgba(0,0,0,0.4)' }}>
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="black" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>
                  </div>
                )}
              </div>
              <p className="truncate-text" style={{ fontSize: '1.2rem', color: 'rgba(255,255,255,0.8)', margin: 0, textShadow: '0 1px 4px rgba(0,0,0,0.4)' }}>{currentTrack.uploader}</p>
            </div>
            
            <div style={{ display: 'flex', gap: '2px' }}>
              <button 
                onClick={() => setShowPlaylists(true)} 
                style={{ background: 'transparent', border: 'none', cursor: 'pointer', padding: '10px', transition: 'transform 0.1s' }}
                className="active:scale-90"
              >
                <PlusCircle color="white" size={28} style={{ filter: 'drop-shadow(0 2px 4px rgba(0,0,0,0.5))' }} />
              </button>

              <button 
                onClick={async () => {
                  const state = useStore.getState();
                  const isDownloaded = state.downloads.some(d => d.id === currentTrack.id);
                  if (isDownloaded) {
                    if (isNative) {
                      window.sendNativeCommand(`emusic://deletedownload?id=${currentTrack.id}`);
                    } else {
                      const { removeTrackBlob } = await import('../store/offline.js');
                      await removeTrackBlob(currentTrack.id);
                      state.removeDownloadInfo(currentTrack.id);
                    }
                  } else {
                    state.setDownloadingId(currentTrack.id);
                    if (isNative) {
                      const u = encodeURIComponent(currentTrack.url);
                      const t = encodeURIComponent(currentTrack.title || '');
                      const a = encodeURIComponent(currentTrack.uploader || '');
                      const img = encodeURIComponent(currentTrack.thumbnail || '');
                      window.sendNativeCommand(`emusic://download?id=${currentTrack.id}&url=${u}&title=${t}&artist=${a}&thumb=${img}`);
                    } else {
                      const { downloadTrackBlob } = await import('../store/offline.js');
                      const success = await downloadTrackBlob(currentTrack);
                      if (success) {
                        state.addDownloadInfo(currentTrack);
                      } else {
                        alert("Error al descargar la canción.");
                      }
                      state.setDownloadingId(null);
                    }
                  }
                }}
                style={{ background: 'transparent', border: 'none', cursor: 'pointer', padding: '10px', transition: 'transform 0.1s' }}
                className="active:scale-90"
              >
                {useStore.getState().downloadingId === currentTrack.id ? (
                  <Loader size={26} color="white" className="spin-animation" style={{ filter: 'drop-shadow(0 2px 4px rgba(0,0,0,0.5))' }} />
                ) : useStore.getState().downloads.some(d => d.id === currentTrack.id) ? (
                  <svg width="28" height="28" viewBox="0 0 24 24" fill="var(--accent)" stroke="var(--accent)" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ filter: 'drop-shadow(0 2px 4px rgba(0,0,0,0.5))' }}>
                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
                    <polyline points="7 10 12 15 17 10"></polyline>
                    <line x1="12" y1="15" x2="12" y2="3"></line>
                  </svg>
                ) : (
                  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="white" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ filter: 'drop-shadow(0 2px 4px rgba(0,0,0,0.5))' }}>
                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
                    <polyline points="7 10 12 15 17 10"></polyline>
                    <line x1="12" y1="15" x2="12" y2="3"></line>
                  </svg>
                )}
              </button>

              <button 
                onClick={() => useStore.getState().toggleFavorite(currentTrack)} 
                style={{ background: 'transparent', border: 'none', cursor: 'pointer', padding: '10px', transition: 'transform 0.1s' }}
                className="active:scale-90"
              >
                <svg 
                  width="28" height="28" viewBox="0 0 24 24" 
                  fill={useStore.getState().favorites.some(f => f.id === currentTrack.id) ? "var(--accent)" : "none"} 
                  stroke={useStore.getState().favorites.some(f => f.id === currentTrack.id) ? "var(--accent)" : "white"} 
                  strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"
                  style={{ filter: 'drop-shadow(0 2px 4px rgba(0,0,0,0.5))' }}
                >
                  <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"></path>
                </svg>
              </button>
            </div>
          </div>

          {/* Progress Scrubber */}
          <div style={{ marginBottom: '45px' }}>
            <div 
              ref={progressBarRef}
              onClick={handleProgressClick}
              style={{ height: '6px', background: 'rgba(255,255,255,0.2)', borderRadius: '3px', cursor: 'pointer', position: 'relative' }}
            >
              <div style={{ position: 'absolute', top: 0, left: 0, height: '100%', width: `${progress}%`, background: 'white', borderRadius: '3px', transition: 'width 0.1s linear' }} />
              <div style={{ position: 'absolute', top: '50%', left: `${progress}%`, transform: 'translate(-50%, -50%)', width: '14px', height: '14px', background: 'white', borderRadius: '50%', boxShadow: '0 2px 6px rgba(0,0,0,0.4)' }} />
            </div>
            <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: '12px', fontSize: '0.85rem', color: 'rgba(255,255,255,0.8)', fontWeight: '500', textShadow: '0 1px 2px rgba(0,0,0,0.5)' }}>
              <span>{formatTime(currentTime)}</span>
              <span>{formatTime(duration)}</span>
            </div>
          </div>

          {/* Main Controls */}
          <div style={{ display: 'flex', justifyContent: 'space-evenly', alignItems: 'center', marginBottom: '60px' }}>
            <button onClick={playPrevious} style={{ background: 'transparent', border: 'none', color: 'white', cursor: 'pointer', padding: '10px', transition: 'transform 0.1s' }} className="active:scale-90">
              <SkipBack fill="white" size={42} style={{ filter: 'drop-shadow(0 4px 6px rgba(0,0,0,0.4))' }} />
            </button>
            <button 
              onClick={() => setIsPlaying(!isPlaying)} 
              style={{ 
                background: 'white', border: 'none', color: 'black', 
                cursor: 'pointer', width: '75px', height: '75px', 
                borderRadius: '50%', display: 'flex', alignItems: 'center', justifyContent: 'center',
                boxShadow: '0 8px 24px rgba(0,0,0,0.4)',
                transition: 'transform 0.1s'
              }}
              className="active:scale-90"
            >
              {isLoading ? <Loader size={38} color="black" className="spin-animation" /> : (isPlaying ? <Pause fill="black" size={38} /> : <Play fill="black" size={38} style={{ marginLeft: '4px' }} />)}
            </button>
            <button onClick={playNext} style={{ background: 'transparent', border: 'none', color: 'white', cursor: 'pointer', padding: '10px', transition: 'transform 0.1s' }} className="active:scale-90">
              <SkipForward fill="white" size={42} style={{ filter: 'drop-shadow(0 4px 6px rgba(0,0,0,0.4))' }} />
            </button>
          </div>
        </div>
      )}

      {/* Lyrics Overlay */}
      {isExpanded && showLyrics && (
        <Lyrics track={currentTrack} onClose={() => setShowLyrics(false)} themeColor={themeColor} />
      )}

      {/* Playlists Overlay */}
      {isExpanded && showPlaylists && (
        <div style={{
          position: 'absolute', top: 0, left: 0, right: 0, bottom: 0,
          background: `linear-gradient(to bottom, ${themeColor}F2, #000000)`,
          backdropFilter: 'blur(40px)',
          WebkitBackdropFilter: 'blur(40px)',
          zIndex: 200, display: 'flex', flexDirection: 'column',
          padding: '30px 20px',
          animation: 'slideUp 0.3s cubic-bezier(0.2, 0.8, 0.2, 1)'
        }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '30px' }}>
            <h2 style={{ color: 'white', margin: 0, fontSize: '1.5rem', fontWeight: 'bold' }}>Agregar a Playlist</h2>
            <button onClick={() => setShowPlaylists(false)} style={{ background: 'rgba(255,255,255,0.2)', border: 'none', borderRadius: '50%', color: 'white', padding: '8px', cursor: 'pointer' }}>
              <X size={24} />
            </button>
          </div>
          
          <div style={{ flex: 1, overflowY: 'auto' }}>
            {useStore.getState().playlists.length === 0 ? (
              <p style={{ color: '#b3b3b3', textAlign: 'center', marginTop: '40px' }}>No tienes playlists. Créalas desde la Biblioteca.</p>
            ) : (
              useStore.getState().playlists.map(p => {
                const trackId = currentTrack.id || (currentTrack.url && currentTrack.url.match(/[?&]v=([^&]{11})/) ? currentTrack.url.match(/[?&]v=([^&]{11})/)[1] : null);
                const isAdded = p.tracks.some(t => t.id === trackId);
                return (
                  <div 
                    key={p.id}
                    onClick={() => {
                      if (isAdded) {
                        useStore.getState().removeFromPlaylist(p.id, trackId);
                      } else {
                        useStore.getState().addToPlaylist(p.id, currentTrack);
                      }
                    }}
                    style={{ 
                      padding: '15px', background: 'rgba(255,255,255,0.05)', borderRadius: '12px', 
                      marginBottom: '10px', display: 'flex', justifyContent: 'space-between', alignItems: 'center',
                      cursor: 'pointer'
                    }}
                  >
                    <span style={{ fontSize: '1.1rem', color: 'white', fontWeight: '500' }}>{p.name}</span>
                    <div style={{ width: '24px', height: '24px', borderRadius: '50%', border: `2px solid ${isAdded ? 'var(--accent)' : 'rgba(255,255,255,0.3)'}`, display: 'flex', alignItems: 'center', justifyContent: 'center', background: isAdded ? 'var(--accent)' : 'transparent' }}>
                      {isAdded && <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="black" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>}
                    </div>
                  </div>
                );
              })
            )}
          </div>
        </div>
      )}
      
      {/* Animation Style */}
      <style>{`
        @keyframes slideUp {
          from { transform: translateY(100%); opacity: 0.5; }
          to { transform: translateY(0); opacity: 1; }
        }
        @keyframes spin {
          from { transform: rotate(0deg); }
          to { transform: rotate(360deg); }
        }
        .spin-animation {
          animation: spin 1s linear infinite;
        }
        .active\\:scale-90:active {
          transform: scale(0.9);
        }
      `}</style>
    </>
  );
}
