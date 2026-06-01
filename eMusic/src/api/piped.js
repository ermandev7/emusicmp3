import axios from 'axios';
import { useStore } from '../store/useStore';

const API_BASE = 'https://api.emusicmp3.duckdns.org';

export const searchMusic = async (query) => {
  try {
    const response = await axios.get(`${API_BASE}/search`, {
      params: { q: query, filter: 'music_songs' }
    });
    return response.data.items;
  } catch (error) {
    console.error("Error searching:", error);
    return [];
  }
};

export const getStream = async (videoId) => {
  try {
    const response = await axios.get(`${API_BASE}/streams/${videoId}`);
    const audioStreams = response.data.audioStreams;
    
    let bestAudio = null;
    if (audioStreams && audioStreams.length > 0) {
      const quality = useStore.getState().audioQuality;
      if (quality === 'standard') {
        bestAudio = audioStreams.sort((a, b) => a.bitrate - b.bitrate).find(s => s.bitrate >= 64000) || audioStreams[0];
      } else {
        bestAudio = audioStreams.sort((a, b) => b.bitrate - a.bitrate)[0];
      }
    } else {
      // Fallback to a video stream that includes audio
      const videoStreams = response.data.videoStreams || [];
      bestAudio = videoStreams.find(v => !v.videoOnly) || videoStreams[0];
    }

    if (!bestAudio) return null;
    
    return {
      url: bestAudio.url,
      title: response.data.title,
      thumbnail: response.data.thumbnailUrl,
      uploader: response.data.uploader,
      id: videoId,
      relatedStreams: response.data.relatedStreams || []
    };
  } catch (error) {
    console.error("Error getting stream:", error);
    return null;
  }
};
