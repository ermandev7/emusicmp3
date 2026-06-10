using Microsoft.Maui.Controls;
using eMusicApp.ViewModels;

namespace eMusicApp.Views
{
    public partial class SearchPage : ContentPage
    {
        public SearchPage(SearchViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;

            viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(viewModel.IsBusy) && !viewModel.IsBusy)
                    SearchBarControl.Unfocus();
            };
        }

        private async void OnTrackTapped(object? sender, TappedEventArgs e)
        {
            SearchBarControl.Unfocus();
            if (sender is not VisualElement el) return;
            await el.ScaleTo(0.96, 60, Easing.CubicIn);
            await el.ScaleTo(1.0, 100, Easing.CubicOut);
        }
    }
}
