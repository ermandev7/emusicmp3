using Microsoft.Maui.Controls;
using eMusicApp.ViewModels;
using System.Windows.Input;

namespace eMusicApp.Views
{
    public partial class FullPlayerPage : ContentPage
    {
        public FullPlayerPage(PlayerViewModel playerViewModel)
        {
            InitializeComponent();
            
            // Create a temporary binding context that holds both the PlayerViewModel and the MinimizeCommand
            BindingContext = new FullPlayerViewModelWrapper(playerViewModel, Navigation);
        }

        private void OnSliderDragCompleted(object sender, System.EventArgs e)
        {
            if (sender is Slider slider && BindingContext is FullPlayerViewModelWrapper wrapper)
            {
                wrapper.Player.SeekCommand.Execute(slider.Value);
            }
        }
    }

    public class FullPlayerViewModelWrapper
    {
        public PlayerViewModel Player { get; }
        public ICommand MinimizeCommand { get; }

        public FullPlayerViewModelWrapper(PlayerViewModel player, INavigation navigation)
        {
            Player = player;
            MinimizeCommand = new Command(async () => await navigation.PopModalAsync());
        }
    }
}
