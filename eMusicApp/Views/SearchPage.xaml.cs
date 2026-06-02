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

            // Ocultar teclado cuando se ejecuta la búsqueda
            viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(viewModel.IsBusy) && !viewModel.IsBusy)
                {
                    SearchBarControl.Unfocus();
                }
            };
        }

        // Ocultar teclado al hacer tap en un resultado
        private void OnTrackTapped(object? sender, TappedEventArgs e)
        {
            SearchBarControl.Unfocus();
        }
    }
}
