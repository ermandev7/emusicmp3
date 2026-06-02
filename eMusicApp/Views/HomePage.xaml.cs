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
    }
}
