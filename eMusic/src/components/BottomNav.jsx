import { useStore } from '../store/useStore';
import { Home, Search, Library } from 'lucide-react';

export default function BottomNav() {
  const { activeTab, setActiveTab } = useStore();

  return (
    <nav className="mobile-nav glass">
      <div 
        className={`nav-item ${activeTab === 'home' ? 'active' : ''}`}
        onClick={() => setActiveTab('home')}
      >
        <Home size={24} />
        <span>Inicio</span>
      </div>
      <div 
        className={`nav-item ${activeTab === 'search' ? 'active' : ''}`}
        onClick={() => setActiveTab('search')}
      >
        <Search size={24} />
        <span>Buscar</span>
      </div>
      <div 
        className={`nav-item ${activeTab === 'library' ? 'active' : ''}`}
        onClick={() => setActiveTab('library')}
      >
        <Library size={24} />
        <span>Tu biblioteca</span>
      </div>
    </nav>
  );
}
