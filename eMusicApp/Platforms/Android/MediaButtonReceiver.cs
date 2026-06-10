using Android.Content;

namespace eMusicApp.Platforms.Android;

/// <summary>
/// BroadcastReceiver para los botones de la notificación de reproducción.
/// Los broadcasts se entregan instantáneamente (no se throttlean como
/// PendingIntent.GetForegroundService en pantalla encendida).
/// </summary>
[BroadcastReceiver(Exported = false)]
[IntentFilter(new[] {
    AndroidMedia3Service.ACTION_PREV,
    AndroidMedia3Service.ACTION_PLAY_PAUSE,
    AndroidMedia3Service.ACTION_NEXT
})]
public class MediaButtonReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action == null) return;

        var service = AndroidMedia3Service.Instance;
        if (service == null) return;

        switch (intent.Action)
        {
            case AndroidMedia3Service.ACTION_PREV:
                service.HandlePrev();
                break;
            case AndroidMedia3Service.ACTION_PLAY_PAUSE:
                service.HandlePlayPause();
                break;
            case AndroidMedia3Service.ACTION_NEXT:
                service.HandleNext();
                break;
        }
    }
}
