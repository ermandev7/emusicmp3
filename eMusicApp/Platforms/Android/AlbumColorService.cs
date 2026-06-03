using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Maui.Graphics;
using eMusicApp.Services;

namespace eMusicApp.Platforms.Android
{
    public class AlbumColorService : IAlbumColorService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        public async Task<Color?> GetDominantColorAsync(string imageUrl)
        {
            try
            {
                var bytes = await _http.GetByteArrayAsync(imageUrl);
                var bmp = global::Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                if (bmp == null) return null;

                // Scale to 12×12 for fast average-color calculation
                var scaled = global::Android.Graphics.Bitmap.CreateScaledBitmap(bmp, 12, 12, true);
                bmp.Recycle();

                long r = 0, g = 0, b = 0;
                int total = 0;
                for (int x = 0; x < scaled.Width; x++)
                {
                    for (int y = 0; y < scaled.Height; y++)
                    {
                        int pixel = scaled.GetPixel(x, y);
                        r += (pixel >> 16) & 0xFF;
                        g += (pixel >> 8)  & 0xFF;
                        b +=  pixel        & 0xFF;
                        total++;
                    }
                }
                scaled.Recycle();

                if (total == 0) return null;

                return Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total));
            }
            catch
            {
                return null;
            }
        }
    }
}
