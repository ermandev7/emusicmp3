import { useStore } from '../store/useStore';
import { Play } from 'lucide-react';

export default function Home() {
  const { favorites, recentTracks } = useStore();

  const playFavorite = (index) => {
    useStore.getState().setQueueAndPlay(favorites, index);
  };

  const playRecent = (index) => {
    useStore.getState().setQueueAndPlay(recentTracks, index);
  };

  return (
    <div style={{ padding: '20px', paddingBottom: '100px' }}>
      
      {/* Saludo dinámico */}
      <h1 style={{ marginBottom: '30px', fontSize: '2rem', fontWeight: 'bold', letterSpacing: '-0.5px' }}>
        {new Date().getHours() < 12 ? 'Buenos días' : new Date().getHours() < 19 ? 'Buenas tardes' : 'Buenas noches'}
      </h1>

      {/* Tus Favoritos (Acceso Rápido) */}
      <div style={{ marginBottom: '40px' }}>
        <h2 style={{ fontSize: '1.2rem', fontWeight: 'bold', marginBottom: '15px' }}>Tus Favoritos ❤️</h2>
        {favorites.length === 0 ? (
          <p style={{ color: '#b3b3b3', fontSize: '0.9rem' }}>Aún no has guardado ninguna canción.</p>
        ) : (
          <div style={{ 
            display: 'flex', gap: '15px', overflowX: 'auto', 
            paddingBottom: '10px', msOverflowStyle: 'none', scrollbarWidth: 'none' 
          }}>
            {favorites.slice(0, 10).map((track, idx) => (
              <div 
                key={track.id + idx} 
                onClick={() => playFavorite(idx)}
                style={{
                  minWidth: '120px', width: '120px', cursor: 'pointer',
                  display: 'flex', flexDirection: 'column', gap: '8px'
                }}
              >
                <div style={{ position: 'relative', width: '120px', height: '120px', borderRadius: '8px', overflow: 'hidden', boxShadow: '0 4px 10px rgba(0,0,0,0.3)' }}>
                  <img src={track.thumbnail} alt={track.title} style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
                  <div style={{ position: 'absolute', bottom: '5px', right: '5px', background: 'var(--accent)', borderRadius: '50%', padding: '6px', display: 'flex', alignItems: 'center', justifyContent: 'center', boxShadow: '0 2px 5px rgba(0,0,0,0.4)' }}>
                    <Play fill="black" size={14} color="black" style={{ marginLeft: '2px' }} />
                  </div>
                </div>
                <span className="truncate-text" style={{ fontSize: '0.85rem', fontWeight: '600' }}>{track.title}</span>
                <span className="truncate-text" style={{ fontSize: '0.75rem', color: 'var(--text-subdued)' }}>{track.uploader}</span>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Escuchado Recientemente */}
      <div>
        <h2 style={{ fontSize: '1.2rem', fontWeight: 'bold', marginBottom: '15px' }}>Escuchado Recientemente 🎧</h2>
        {recentTracks.length === 0 ? (
          <p style={{ color: '#b3b3b3', fontSize: '0.9rem' }}>Busca y reproduce una canción para ver tu historial aquí.</p>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
            {recentTracks.slice(0, 10).map((track, idx) => (
              <div 
                key={track.id + idx}
                onClick={() => playRecent(idx)}
                style={{
                  display: 'flex', alignItems: 'center', gap: '15px', padding: '10px',
                  background: 'rgba(255,255,255,0.03)', borderRadius: '8px', cursor: 'pointer',
                  transition: 'background 0.2s'
                }}
                className="active:scale-95"
              >
                <img src={track.thumbnail} alt="cover" style={{ width: '55px', height: '55px', objectFit: 'cover', borderRadius: '4px', boxShadow: '0 2px 4px rgba(0,0,0,0.2)' }} />
                <div style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden', flex: 1 }}>
                  <span className="truncate-text" style={{ fontSize: '1rem', fontWeight: '500' }}>{track.title}</span>
                  <span className="truncate-text" style={{ fontSize: '0.8rem', color: 'var(--text-subdued)' }}>{track.uploader}</span>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      <style>{`
        div::-webkit-scrollbar {
          display: none;
        }
        .active\\:scale-95:active {
          transform: scale(0.98);
        }
      `}</style>
    </div>
  );
}
