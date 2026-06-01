import localforage from 'localforage';
import { getStream } from '../api/piped';
import axios from 'axios';

localforage.config({
  name: 'eMusic',
  storeName: 'audio_cache',
  description: 'Guarda la música descargada para escuchar offline'
});

export const downloadTrackBlob = async (track, onProgress) => {
  try {
    const streamInfo = await getStream(track.id);
    if (!streamInfo || !streamInfo.url) throw new Error("No stream URL");

    // Fetch the actual audio file as a Blob
    const response = await axios.get(streamInfo.url, {
      responseType: 'blob',
      onDownloadProgress: (progressEvent) => {
        if (progressEvent.total) {
          const percentCompleted = Math.round((progressEvent.loaded * 100) / progressEvent.total);
          if (onProgress) onProgress(percentCompleted);
        }
      }
    });

    const audioBlob = response.data;
    
    // Save to IndexedDB
    await localforage.setItem(`track_${track.id}`, audioBlob);
    
    return true;
  } catch (error) {
    console.error("Error downloading track:", error);
    return false;
  }
};

let currentObjectUrl = null;

export const getTrackBlobUrl = async (id) => {
  try {
    const blob = await localforage.getItem(`track_${id}`);
    if (blob) {
      if (currentObjectUrl) {
        URL.revokeObjectURL(currentObjectUrl);
      }
      currentObjectUrl = URL.createObjectURL(blob);
      return currentObjectUrl;
    }
    return null;
  } catch (error) {
    console.error("Error reading track from IndexedDB:", error);
    return null;
  }
};

export const removeTrackBlob = async (id) => {
  try {
    await localforage.removeItem(`track_${id}`);
  } catch (error) {
    console.error("Error removing track:", error);
  }
};
