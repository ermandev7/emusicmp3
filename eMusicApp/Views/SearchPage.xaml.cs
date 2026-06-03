using Microsoft.Maui.Controls;
using eMusicApp.Models;
using eMusicApp.Services;
using eMusicApp.ViewModels;
using System.Linq;

namespace eMusicApp.Views
{
    public partial class SearchPage : ContentPage
    {
        public SearchPage(SearchViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;

            // Ocultar teclado cuando se ejecuta la búsqueda
            viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(viewModel.IsBusy) && !viewModel.IsBusy)
                    SearchBarControl.Unfocus();
            };
        }

        // Ocultar teclado al hacer tap en un resultado
        private void OnTrackTapped(object? sender, TappedEventArgs e)
        {
            SearchBarControl.Unfocus();
        }

        private async void OnAddToPlaylistClicked(object sender, System.EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is Track track)
                await ShowAddToPlaylistAsync(track);
        }

        private async System.Threading.Tasks.Task ShowAddToPlaylistAsync(Track track)
        {
            var apiService = IPlatformApplication.Current.Services.GetRequiredService<ApiService>();
            var playlists = await apiService.GetPlaylistsAsync();

            var options = playlists.Select(p => p.Name).Append("+ Nueva playlist").ToArray();
            var result = await DisplayActionSheet("Agregar a playlist", "Cancelar", null, options);
            if (result == null || result == "Cancelar") return;

            Playlist? target;
            if (result == "+ Nueva playlist")
            {
                var name = await DisplayPromptAsync("Nueva playlist", "Nombre:", "Crear", "Cancelar",
                    placeholder: "Mi playlist", maxLength: 50);
                if (string.IsNullOrWhiteSpace(name)) return;
                target = await apiService.CreatePlaylistAsync(name);
            }
            else
            {
                target = playlists.FirstOrDefault(p => p.Name == result);
            }

            if (target == null) return;
            await apiService.AddTrackToPlaylistAsync(target.Id, track);
            await DisplayAlert("Listo", $"Añadida a \"{target.Name}\"", "OK");
        }
    }
}
