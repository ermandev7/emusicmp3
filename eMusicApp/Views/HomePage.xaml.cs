using Microsoft.Maui.Controls;
using eMusicApp.ViewModels;

namespace eMusicApp.Views
{
    public partial class HomePage : ContentPage
    {
        public HomePage(HomeViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            if (BindingContext is HomeViewModel vm)
            {
                vm.LoadRecentTracksCommand.Execute(null);
            }
        }

        private async void OnGenreTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not VisualElement el) return;
            // Buscar el Border padre (el contenedor del género)
            var target = el is Border ? el : el.Parent as VisualElement ?? el;
            await target.ScaleTo(0.93, 80, Easing.CubicIn);
            await target.ScaleTo(1.0, 120, Easing.CubicOut);
        }

        private async void OnTrackTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not VisualElement el) return;
            await el.ScaleTo(0.96, 60, Easing.CubicIn);
            await el.ScaleTo(1.0, 100, Easing.CubicOut);
        }
    }
}
