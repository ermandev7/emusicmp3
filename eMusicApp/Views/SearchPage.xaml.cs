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

        private void OnTrackTapped(object? sender, TappedEventArgs e)
        {
            SearchBarControl.Unfocus();
        }
    }
}
