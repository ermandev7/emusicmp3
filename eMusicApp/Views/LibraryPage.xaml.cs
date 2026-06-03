using Microsoft.Maui.Controls;
using eMusicApp.Models;
using eMusicApp.Services;
using eMusicApp.ViewModels;
using System.Linq;

namespace eMusicApp.Views
{
    public partial class LibraryPage : ContentPage
    {
        public LibraryPage(LibraryViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            if (BindingContext is LibraryViewModel vm)
                vm.LoadLibraryCommand.Execute(null);
        }

        private async void OnCreatePlaylistClicked(object sender, System.EventArgs e)
        {
            var name = await DisplayPromptAsync("Nueva playlist", "Nombre:", "Crear", "Cancelar",
                placeholder: "Mi playlist", maxLength: 50);
            if (string.IsNullOrWhiteSpace(name)) return;
            if (BindingContext is LibraryViewModel vm)
                await vm.CreatePlaylistCommand.ExecuteAsync(name);
        }

        private async void OnPlaylistTapped(object sender, TappedEventArgs e)
        {
            if (e.Parameter is Playlist playlist)
                await Shell.Current.GoToAsync($"PlaylistDetailPage?id={playlist.Id}&name={Uri.EscapeDataString(playlist.Name)}");
        }

        private async void OnDeletePlaylistClicked(object sender, System.EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is Playlist playlist)
            {
                bool confirm = await DisplayAlert("Eliminar", $"¿Eliminar «{playlist.Name}»?", "Eliminar", "Cancelar");
                if (!confirm) return;
                if (BindingContext is LibraryViewModel vm)
                    await vm.DeletePlaylistCommand.ExecuteAsync(playlist);
            }
        }

        private async void OnAddToPlaylistClicked(object sender, System.EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is Track track)
                await ShowAddToPlaylistAsync(track);
        }

        internal async System.Threading.Tasks.Task ShowAddToPlaylistAsync(Track track)
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
                if (target == null) return;
                if (BindingContext is LibraryViewModel libVm)
                {
                    libVm.Playlists.Insert(0, target);
                    libVm.NotifyPlaylistsChanged();
                }
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
