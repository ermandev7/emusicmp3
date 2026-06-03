using Microsoft.Maui.Controls;
using eMusicApp.ViewModels;

namespace eMusicApp.Views
{
    public partial class PlaylistDetailPage : ContentPage
    {
        public PlaylistDetailPage(PlaylistDetailViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
        }

        private async void OnBackClicked(object sender, System.EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}
