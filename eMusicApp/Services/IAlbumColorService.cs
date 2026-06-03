using Microsoft.Maui.Graphics;
using System.Threading.Tasks;

namespace eMusicApp.Services
{
    public interface IAlbumColorService
    {
        Task<Color?> GetDominantColorAsync(string imageUrl);
    }
}
