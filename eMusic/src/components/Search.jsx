import { useStore } from '../store/useStore';
import { searchMusic } from '../api/piped';
import { Search as SearchIcon } from 'lucide-react';

export default function Search() {
  const { searchQuery, setSearchQuery, searchResults, setSearchResults, isLoading, setIsLoading } = useStore();

  const handleSearch = async (e) => {
    e.preventDefault();
    if (!searchQuery.trim()) return;
    
    setIsLoading(true);
    const results = await searchMusic(searchQuery);
    setSearchResults(results);
    // No borramos la búsqueda (setSearchQuery('')) para que el usuario pueda ver lo que buscó
    setIsLoading(false);
  };

  const playTrack = (video) => {
    const streamResults = searchResults.filter(item => item.type === 'stream');
    const index = streamResults.findIndex(item => item.url === video.url);
    if (index !== -1) {
      useStore.getState().setQueueAndPlay(streamResults, index);
    }
  };

  return (
    <div style={{ padding: '20px', paddingBottom: '80px' }}>
      <h1 style={{ marginBottom: '20px', fontSize: '1.5rem', fontWeight: 'bold' }}>Buscar</h1>
      
      <form onSubmit={handleSearch} style={{ position: 'relative', marginBottom: '20px' }}>
        <div style={{ position: 'absolute', left: '15px', top: '50%', transform: 'translateY(-50%)', color: 'black' }}>
          <SearchIcon size={20} />
        </div>
        <input 
          type="search" 
          placeholder="¿Qué quieres escuchar?" 
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          style={{
            width: '100%', padding: '15px 15px 15px 45px', borderRadius: '30px',
            border: 'none', fontSize: '1rem', backgroundColor: 'white', color: 'black',
            fontWeight: '500', outline: 'none'
          }}
        />
      </form>

      <div style={{ display: 'flex', flexDirection: 'column', gap: '15px' }}>
        {isLoading ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '15px' }}>
            {[...Array(6)].map((_, i) => (
              <div key={i} style={{ display: 'flex', gap: '15px', alignItems: 'center', padding: '10px', background: 'rgba(255,255,255,0.03)', borderRadius: '8px' }} className="animate-pulse">
                <div style={{ width: '50px', height: '50px', borderRadius: '4px', background: 'rgba(255,255,255,0.1)' }} />
                <div style={{ display: 'flex', flexDirection: 'column', gap: '8px', flex: 1 }}>
                  <div style={{ width: '70%', height: '14px', background: 'rgba(255,255,255,0.1)', borderRadius: '4px' }} />
                  <div style={{ width: '40%', height: '10px', background: 'rgba(255,255,255,0.05)', borderRadius: '4px' }} />
                </div>
              </div>
            ))}
          </div>
        ) : (
          searchResults.map((item, idx) => {
            if (item.type !== 'stream') return null;
            
            return (
              <div 
                key={idx} 
                onClick={() => playTrack(item)}
                style={{ display: 'flex', alignItems: 'center', gap: '15px', cursor: 'pointer' }}
              >
                <img src={item.thumbnail} alt="cover" style={{ width: '50px', height: '50px', objectFit: 'cover', borderRadius: '4px' }} />
                <div style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
                  <span className="truncate-text" style={{ fontSize: '1rem', fontWeight: '500' }}>{item.title}</span>
                  <span className="truncate-text" style={{ fontSize: '0.8rem', color: 'var(--text-subdued)' }}>{item.uploaderName}</span>
                </div>
              </div>
            );
          })
        )}
      </div>
    </div>
  );
}
