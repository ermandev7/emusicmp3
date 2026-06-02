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
        }
    }
}
