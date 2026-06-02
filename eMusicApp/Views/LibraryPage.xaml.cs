using Microsoft.Maui.Controls;
using eMusicApp.ViewModels;

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
            {
                vm.LoadLibraryCommand.Execute(null);
            }
        }
    }
}
