const axios = require('axios');

async function getStream(videoId) {
  try {
    const response = await axios.get(`https://api.emusicmp3.duckdns.org/streams/${videoId}`);
    const audioStreams = response.data.audioStreams;
    
    let bestAudio = null;
    if (audioStreams && audioStreams.length > 0) {
      bestAudio = audioStreams.sort((a, b) => b.bitrate - a.bitrate)[0];
    } else {
      const videoStreams = response.data.videoStreams || [];
      bestAudio = videoStreams.find(v => !v.videoOnly) || videoStreams[0];
    }

    if (!bestAudio) return null;
    return bestAudio.url;
  } catch (error) {
    return null;
  }
}

getStream('Iq_DGuE80Cc').then(url => {
  console.log("FINAL URL:", url ? url.substring(0, 100) + '...' : 'NULL');
});
