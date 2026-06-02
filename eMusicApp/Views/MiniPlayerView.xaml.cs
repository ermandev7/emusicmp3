using Microsoft.Maui.Controls;

namespace eMusicApp.Views
{
    public partial class MiniPlayerView : ContentView
    {
        public MiniPlayerView()
        {
            InitializeComponent();
        }

        private async void OnMiniPlayerTapped(object sender, System.EventArgs e)
        {
            eMusicApp.ViewModels.PlayerViewModel playerVm = null;
            
            if (BindingContext is eMusicApp.ViewModels.SearchViewModel searchVm)
                playerVm = searchVm.Player;
            else if (BindingContext is eMusicApp.ViewModels.HomeViewModel homeVm)
                playerVm = homeVm.Player;
            else if (BindingContext is eMusicApp.ViewModels.LibraryViewModel libVm)
                playerVm = libVm.Player;

            if (playerVm != null)
            {
                await Application.Current.MainPage.Navigation.PushModalAsync(new FullPlayerPage(playerVm));
            }
        }
    }
}
