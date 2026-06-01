import { useState } from 'react';
import { useStore } from '../store/useStore';
import { Heart, Plus, ListMusic, ChevronLeft, Trash2, Settings } from 'lucide-react';

export default function Library() {
  const { favorites, downloads, playlists, createPlaylist, deletePlaylist, removeFromPlaylist, audioQuality, setAudioQuality } = useStore();
  const [selectedPlaylist, setSelectedPlaylist] = useState(null);
  const [showCreateModal, setShowCreateModal] = useState(false);
  const [newPlaylistName, setNewPlaylistName] = useState("");

  const playTrack = (list, index) => {
    // Play the given list starting at the clicked index
    useStore.getState().setQueueAndPlay(list, index);
  };

  const handleCreatePlaylist = () => {
    if (newPlaylistName && newPlaylistName.trim()) {
      createPlaylist(newPlaylistName.trim());
      setNewPlaylistName("");
      setShowCreateModal(false);
    }
  };

  if (selectedPlaylist) {
    const p = playlists.find(p => p.id === selectedPlaylist);
    if (!p) {
      setSelectedPlaylist(null);
      return null;
    }
    return (
      <div style={{ padding: '20px', paddingBottom: '80px' }} className="animate-fade-in">
        <div style={{ display: 'flex', alignItems: 'center', gap: '15px', marginBottom: '30px' }}>
          <button onClick={() => setSelectedPlaylist(null)} style={{ background: 'transparent', border: 'none', color: 'white', cursor: 'pointer' }}>
            <ChevronLeft size={28} />
          </button>
          <h1 style={{ fontSize: '1.5rem', fontWeight: 'bold', margin: 0 }}>{p.name}</h1>
        </div>
        
        {p.tracks.length === 0 ? (
          <div style={{ textAlign: 'center', marginTop: '30px', padding: '40px 20px', background: 'rgba(255,255,255,0.03)', borderRadius: '16px' }}>
            <ListMusic size={64} color="var(--accent)" style={{ margin: '0 auto 20px', filter: 'drop-shadow(0 0 15px var(--accent))', opacity: 0.8 }} />
            <h3 style={{ fontSize: '1.2rem', color: 'white', marginBottom: '10px' }}>Lista vacía</h3>
            <p style={{ color: '#b3b3b3', fontSize: '0.95rem' }}>Explora canciones y agrégalas a esta lista.</p>
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '15px' }}>
            {p.tracks.map((item, idx) => (
              <div key={item.id} style={{ display: 'flex', alignItems: 'center', gap: '15px' }}>
                <div 
                  onClick={() => playTrack(p.tracks, idx)}
                  style={{ display: 'flex', alignItems: 'center', gap: '15px', cursor: 'pointer', flex: 1 }}
                >
                  <img src={item.thumbnail} alt="cover" style={{ width: '50px', height: '50px', objectFit: 'cover', borderRadius: '4px' }} />
                  <div style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden', flex: 1 }}>
                    <span className="truncate-text" style={{ fontSize: '1rem', fontWeight: '500' }}>{item.title}</span>
                    <span className="truncate-text" style={{ fontSize: '0.8rem', color: 'var(--text-subdued)' }}>{item.uploader}</span>
                  </div>
                </div>
                <button 
                  onClick={() => removeFromPlaylist(p.id, item.id)}
                  style={{ background: 'transparent', border: 'none', color: '#ff4444', padding: '10px', cursor: 'pointer' }}
                >
                  <Trash2 size={20} />
                </button>
              </div>
            ))}
          </div>
        )}
      </div>
    );
  }

  return (
    <div style={{ padding: '20px', paddingBottom: '80px' }}>
      <h1 style={{ marginBottom: '20px', fontSize: '1.5rem', fontWeight: 'bold' }}>Tu Biblioteca</h1>
      
      {favorites.length === 0 ? (
        <div style={{ textAlign: 'center', marginTop: '30px', padding: '40px 20px', background: 'rgba(255,255,255,0.03)', borderRadius: '16px', marginBottom: '40px' }}>
          <Heart size={64} color="var(--accent)" style={{ margin: '0 auto 20px', filter: 'drop-shadow(0 0 15px var(--accent))', opacity: 0.8 }} />
          <h3 style={{ fontSize: '1.2rem', color: 'white', marginBottom: '10px' }}>Tu corazón está vacío</h3>
          <p style={{ color: '#b3b3b3', fontSize: '0.95rem' }}>Ve a la lupa y busca a tus artistas favoritos para agregarlos aquí.</p>
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '15px', marginBottom: '40px' }}>
          {favorites.map((item, idx) => (
            <div 
              key={item.id} 
              onClick={() => playTrack(favorites, idx)}
              style={{ display: 'flex', alignItems: 'center', gap: '15px', cursor: 'pointer' }}
            >
              <img src={item.thumbnail} alt="cover" style={{ width: '50px', height: '50px', objectFit: 'cover', borderRadius: '4px' }} />
              <div style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden', flex: 1 }}>
                <span className="truncate-text" style={{ fontSize: '1rem', fontWeight: '500' }}>{item.title}</span>
                <span className="truncate-text" style={{ fontSize: '0.8rem', color: 'var(--text-subdued)' }}>{item.uploader}</span>
              </div>
              <Heart fill="var(--accent)" stroke="var(--accent)" size={20} />
            </div>
          ))}
        </div>
      )}

      <h1 style={{ marginBottom: '20px', fontSize: '1.5rem', fontWeight: 'bold' }}>Descargas (Offline)</h1>
      
      {downloads.length === 0 ? (
        <div style={{ textAlign: 'center', marginTop: '30px', padding: '40px 20px', background: 'rgba(255,255,255,0.03)', borderRadius: '16px' }}>
          <svg width="64" height="64" viewBox="0 0 24 24" fill="none" stroke="var(--accent)" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" style={{ margin: '0 auto 20px', filter: 'drop-shadow(0 0 15px var(--accent))', opacity: 0.8 }}>
            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
            <polyline points="7 10 12 15 17 10"></polyline>
            <line x1="12" y1="15" x2="12" y2="3"></line>
          </svg>
          <h3 style={{ fontSize: '1.2rem', color: 'white', marginBottom: '10px' }}>Nada por aquí</h3>
          <p style={{ color: '#b3b3b3', fontSize: '0.95rem' }}>Descarga canciones para escucharlas cuando no tengas internet. Usa el botón ⬇️ en el reproductor.</p>
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '15px' }}>
          {downloads.map((item, idx) => (
            <div 
              key={item.id} 
              onClick={() => playTrack(downloads, idx)}
              style={{ display: 'flex', alignItems: 'center', gap: '15px', cursor: 'pointer' }}
            >
              <img src={item.thumbnail} alt="cover" style={{ width: '50px', height: '50px', objectFit: 'cover', borderRadius: '4px' }} />
              <div style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden', flex: 1 }}>
                <span className="truncate-text" style={{ fontSize: '1rem', fontWeight: '500' }}>{item.title}</span>
                <span className="truncate-text" style={{ fontSize: '0.8rem', color: 'var(--text-subdued)' }}>{item.uploader}</span>
              </div>
              <svg width="20" height="20" viewBox="0 0 24 24" fill="var(--accent)" stroke="var(--accent)" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path><polyline points="7 10 12 15 17 10"></polyline><line x1="12" y1="15" x2="12" y2="3"></line></svg>
            </div>
          ))}
        </div>
      )}

      <h1 style={{ marginBottom: '20px', marginTop: '30px', fontSize: '1.5rem', fontWeight: 'bold' }}>Mis Playlists</h1>
      
      <div style={{ display: 'flex', flexDirection: 'column', gap: '15px', marginBottom: '40px' }}>
        <div 
          onClick={() => setShowCreateModal(true)}
          style={{ display: 'flex', alignItems: 'center', gap: '15px', cursor: 'pointer', padding: '15px', background: 'rgba(255,255,255,0.05)', borderRadius: '12px' }}
        >
          <div style={{ width: '50px', height: '50px', background: 'rgba(255,255,255,0.1)', borderRadius: '8px', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <Plus size={24} color="white" />
          </div>
          <span style={{ fontSize: '1.1rem', fontWeight: '500' }}>Crear nueva playlist</span>
        </div>

        {playlists.map(p => (
          <div key={p.id} style={{ display: 'flex', alignItems: 'center', gap: '15px' }}>
            <div 
              onClick={() => setSelectedPlaylist(p.id)}
              style={{ display: 'flex', alignItems: 'center', gap: '15px', cursor: 'pointer', padding: '10px', background: 'rgba(255,255,255,0.02)', borderRadius: '12px', flex: 1 }}
            >
              <div style={{ width: '50px', height: '50px', background: 'var(--accent)', borderRadius: '8px', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                <ListMusic size={24} color="black" />
              </div>
              <div style={{ display: 'flex', flexDirection: 'column' }}>
                <span style={{ fontSize: '1.1rem', fontWeight: '500' }}>{p.name}</span>
                <span style={{ fontSize: '0.85rem', color: 'var(--text-subdued)' }}>{p.tracks.length} canciones</span>
              </div>
            </div>
            <button 
              onClick={() => {
                if (window.confirm(`¿Seguro que quieres eliminar "${p.name}"?`)) deletePlaylist(p.id);
              }}
              style={{ background: 'transparent', border: 'none', color: '#ff4444', padding: '10px', cursor: 'pointer' }}
            >
              <Trash2 size={20} />
            </button>
          </div>
        ))}
      </div>

      <h1 style={{ marginBottom: '20px', marginTop: '30px', fontSize: '1.5rem', fontWeight: 'bold', display: 'flex', alignItems: 'center', gap: '10px' }}>
        <Settings size={24} /> Configuración
      </h1>
      <div style={{ padding: '20px', background: 'rgba(255,255,255,0.03)', borderRadius: '16px' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          <div>
            <h3 style={{ fontSize: '1.1rem', marginBottom: '5px' }}>Calidad de Audio</h3>
            <p style={{ fontSize: '0.85rem', color: '#b3b3b3' }}>Afecta el consumo de datos al escuchar online.</p>
          </div>
          <select 
            value={audioQuality} 
            onChange={(e) => setAudioQuality(e.target.value)}
            style={{ padding: '8px 12px', background: '#333', color: 'white', border: 'none', borderRadius: '8px', outline: 'none' }}
          >
            <option value="high">Alta</option>
            <option value="standard">Estándar</option>
          </select>
        </div>
      </div>

      {showCreateModal && (
        <div style={{
          position: 'fixed', top: 0, left: 0, right: 0, bottom: 0,
          background: 'rgba(0,0,0,0.8)', backdropFilter: 'blur(10px)', WebkitBackdropFilter: 'blur(10px)',
          zIndex: 300, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: '20px',
          animation: 'fade-in 0.2s ease-out'
        }}>
          <div style={{
            background: '#1a1a1a', borderRadius: '16px', padding: '25px', width: '100%', maxWidth: '340px',
            boxShadow: '0 10px 30px rgba(0,0,0,0.5)'
          }}>
            <h2 style={{ margin: '0 0 20px', fontSize: '1.3rem', fontWeight: 'bold' }}>Nueva Playlist</h2>
            <input 
              autoFocus
              type="text" 
              placeholder="Nombre de la playlist..." 
              value={newPlaylistName}
              onChange={(e) => setNewPlaylistName(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && handleCreatePlaylist()}
              style={{
                width: '100%', padding: '12px', borderRadius: '8px', border: '1px solid #333', 
                background: '#0d0d0d', color: 'white', fontSize: '1rem', marginBottom: '20px', outline: 'none'
              }}
            />
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '10px' }}>
              <button 
                onClick={() => { setShowCreateModal(false); setNewPlaylistName(""); }}
                style={{ background: 'transparent', border: 'none', color: '#b3b3b3', padding: '10px 15px', borderRadius: '8px', fontWeight: '500' }}
              >
                Cancelar
              </button>
              <button 
                onClick={handleCreatePlaylist}
                style={{ background: 'var(--accent)', border: 'none', color: 'black', padding: '10px 20px', borderRadius: '8px', fontWeight: 'bold' }}
              >
                Crear
              </button>
            </div>
          </div>
        </div>
      )}

    </div>
  );
}
