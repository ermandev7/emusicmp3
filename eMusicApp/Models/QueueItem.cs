namespace eMusicApp.Models
{
    public class QueueItem
    {
        public Track Track { get; init; } = new Track();
        public bool IsNowPlaying { get; init; }
    }
}
