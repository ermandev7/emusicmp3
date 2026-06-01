import { useEffect, useState } from 'react';
import Home from './components/Home';
import Search from './components/Search';
import Library from './components/Library';
import BottomNav from './components/BottomNav';
import Player from './components/Player';
import { useStore } from './store/useStore';
import { Download } from 'lucide-react';

function App() {
  const { activeTab, setActiveTab, themeColor } = useStore();
  const [deferredPrompt, setDeferredPrompt] = useState(null);

  const [touchStart, setTouchStart] = useState(null);
  const [touchEnd, setTouchEnd] = useState(null);
  const minSwipeDistance = 50;

  const onTouchStart = (e) => {
    setTouchEnd(null);
    setTouchStart({ x: e.targetTouches[0].clientX, y: e.targetTouches[0].clientY });
  };

  const onTouchMove = (e) => {
    setTouchEnd({ x: e.targetTouches[0].clientX, y: e.targetTouches[0].clientY });
  };

  const onTouchEnd = (e) => {
    if (!touchStart || !touchEnd) return;
    
    const distanceX = touchStart.x - touchEnd.x;
    const distanceY = touchStart.y - touchEnd.y;
    
    if (Math.abs(distanceY) > Math.abs(distanceX)) return;

    let node = e.target;
    let isScrollable = false;
    while (node && node !== document.documentElement) {
      if (node.scrollWidth > node.clientWidth) {
        const overflowX = window.getComputedStyle(node).overflowX;
        if (overflowX === 'auto' || overflowX === 'scroll') {
          isScrollable = true;
          break;
        }
      }
      node = node.parentNode;
    }

    if (isScrollable) return;

    const isLeftSwipe = distanceX > minSwipeDistance;
    const isRightSwipe = distanceX < -minSwipeDistance;
    
    const tabs = ['home', 'search', 'library'];
    const currentIndex = tabs.indexOf(activeTab);

    if (isLeftSwipe && currentIndex < tabs.length - 1) {
      setActiveTab(tabs[currentIndex + 1]);
    } else if (isRightSwipe && currentIndex > 0) {
      setActiveTab(tabs[currentIndex - 1]);
    }
  };

  useEffect(() => {
    const handlePopState = () => {
      const urlParams = new URLSearchParams(window.location.search);
      const tab = urlParams.get('tab') || 'home';
      setActiveTab(tab);
    };
    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  }, [setActiveTab]);

  useEffect(() => {
    const urlParams = new URLSearchParams(window.location.search);
    const currentUrlTab = urlParams.get('tab') || 'home';
    if (activeTab !== currentUrlTab) {
      window.history.pushState({}, '', `?tab=${activeTab}`);
    }
  }, [activeTab]);

  useEffect(() => {
    // Listen for the PWA install prompt
    const handleBeforeInstallPrompt = (e) => {
      e.preventDefault();
      setDeferredPrompt(e);
    };

    window.addEventListener('beforeinstallprompt', handleBeforeInstallPrompt);

    return () => {
      window.removeEventListener('beforeinstallprompt', handleBeforeInstallPrompt);
    };
  }, []);

  const handleInstallClick = async () => {
    if (deferredPrompt) {
      deferredPrompt.prompt();
      const { outcome } = await deferredPrompt.userChoice;
      if (outcome === 'accepted') {
        setDeferredPrompt(null);
      }
    }
  };

  const renderTab = () => {
    switch (activeTab) {
      case 'home': return <Home />;
      case 'search': return <Search />;
      case 'library': return <Library />;
      default: return <Home />;
    }
  };

  return (
    <div className="app-container" style={{
      background: `linear-gradient(to bottom, ${themeColor} 0%, #121212 100%)`
    }}>
      {deferredPrompt && (
        <div style={{
          background: 'var(--accent)', color: 'black', padding: '10px', 
          display: 'flex', justifyContent: 'center', alignItems: 'center', gap: '10px',
          fontWeight: 'bold', cursor: 'pointer', zIndex: 1000, position: 'relative'
        }} onClick={handleInstallClick}>
          <Download size={18} color="black" />
          <span>Instalar eMusic en el dispositivo</span>
        </div>
      )}

      <div 
        className="main-content animate-fade-in" 
        key={activeTab}
        onTouchStart={onTouchStart}
        onTouchMove={onTouchMove}
        onTouchEnd={onTouchEnd}
      >
        {renderTab()}
      </div>
      
      <Player />
      <BottomNav />
    </div>
  );
}

export default App;
