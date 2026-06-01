import { useState, useEffect } from 'react';
import { X, Loader } from 'lucide-react';

export default function Lyrics({ track, onClose, themeColor }) {
  const [lyrics, setLyrics] = useState(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState(null);

  useEffect(() => {
    if (!track) return;
    
    const fetchLyrics = async () => {
      setIsLoading(true);
      setError(null);
      
      try {
        const title = track.title.replace(/ *\([^)]*\) */g, "").replace(/ *\[[^\]]*]/g, ""); // Remove (Official Video), [Audio], etc.
        const artist = track.uploader.replace(/ - Topic/g, "");

        const res = await fetch(`https://lrclib.net/api/get?track_name=${encodeURIComponent(title)}&artist_name=${encodeURIComponent(artist)}`);
        
        if (!res.ok) {
          throw new Error("No se encontraron letras");
        }
        
        const data = await res.json();
        if (data && (data.syncedLyrics || data.plainLyrics)) {
          setLyrics(data.syncedLyrics || data.plainLyrics);
        } else {
          throw new Error("No se encontraron letras");
        }
      } catch {
        setError("Lo sentimos, no encontramos la letra para esta canción.");
      } finally {
        setIsLoading(false);
      }
    };

    fetchLyrics();
  }, [track]);

  return (
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
        <h2 style={{ color: 'white', margin: 0, fontSize: '1.5rem', fontWeight: 'bold' }}>Letra</h2>
        <button onClick={onClose} style={{ background: 'rgba(255,255,255,0.2)', border: 'none', borderRadius: '50%', color: 'white', padding: '8px', cursor: 'pointer' }} className="active:scale-90">
          <X size={24} />
        </button>
      </div>

      <div style={{ flex: 1, overflowY: 'auto', paddingBottom: '50px' }}>
        {isLoading ? (
          <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '100%' }}>
            <Loader size={40} color="white" className="spin-animation" />
          </div>
        ) : error ? (
          <div style={{ color: 'rgba(255,255,255,0.6)', textAlign: 'center', marginTop: '50px', fontSize: '1.2rem' }}>
            {error}
          </div>
        ) : (
          <div style={{ 
            color: 'white', 
            fontSize: '1.4rem', 
            lineHeight: '2', 
            fontWeight: 'bold', 
            whiteSpace: 'pre-wrap',
            textShadow: '0 2px 4px rgba(0,0,0,0.5)'
          }}>
            {lyrics}
          </div>
        )}
      </div>
    </div>
  );
}
