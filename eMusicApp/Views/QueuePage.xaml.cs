using Microsoft.Maui.Controls;
using eMusicApp.ViewModels;

namespace eMusicApp.Views
{
    public partial class QueuePage : ContentPage
    {
        private readonly PlayerViewModel _player;

        public QueuePage(PlayerViewModel playerViewModel)
        {
            InitializeComponent();
            _player = playerViewModel;
            BindingContext = playerViewModel;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            // Scroll to currently playing track
            var currentItem = _player.QueueItems.FirstOrDefault(q => q.IsNowPlaying);
            if (currentItem != null)
                QueueList.ScrollTo(currentItem, ScrollToPosition.Center, animated: false);
        }

        private async void OnBackClicked(object sender, System.EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}
