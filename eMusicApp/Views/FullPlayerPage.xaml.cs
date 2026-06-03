using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using eMusicApp.ViewModels;
using System.ComponentModel;
using System.Windows.Input;

namespace eMusicApp.Views
{
    public partial class FullPlayerPage : ContentPage
    {
        private readonly PlayerViewModel _player;

        public FullPlayerPage(PlayerViewModel playerViewModel)
        {
            InitializeComponent();

            _player = playerViewModel;
            BindingContext = new FullPlayerViewModelWrapper(playerViewModel, Navigation);

            // Dynamic background from album art
            playerViewModel.PropertyChanged += OnPlayerPropertyChanged;
            ApplyDynamicBackground(playerViewModel.DominantColor);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _player.PropertyChanged -= OnPlayerPropertyChanged;
        }

        private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayerViewModel.DominantColor))
                ApplyDynamicBackground(_player.DominantColor);
        }

        private void ApplyDynamicBackground(Color color)
        {
            var dark = Color.FromRgba(color.Red * 0.35f, color.Green * 0.35f, color.Blue * 0.35f, 1f);
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(dark, 0.0f),
                    new GradientStop(Color.FromArgb("#121212"), 0.65f)
                },
                new Point(0, 0), new Point(0, 1));
        }

        private void OnSliderDragStarted(object sender, System.EventArgs e)
        {
            if (BindingContext is FullPlayerViewModelWrapper wrapper)
                wrapper.Player.IsDraggingSlider = true;
        }

        private void OnSliderDragCompleted(object sender, System.EventArgs e)
        {
            if (sender is Slider slider && BindingContext is FullPlayerViewModelWrapper wrapper)
            {
                // Convertir fracción (0-1) a posición en ms
                double positionMs = slider.Value * wrapper.Player.SliderMaximum;
                wrapper.Player.SeekCommand.Execute(positionMs);
                wrapper.Player.IsDraggingSlider = false;
            }
        }

        private async void OnQueueClicked(object sender, System.EventArgs e)
        {
            var queuePage = IPlatformApplication.Current.Services.GetRequiredService<QueuePage>();
            await Navigation.PushModalAsync(queuePage);
        }

        private async void OnSleepTimerClicked(object sender, System.EventArgs e)
        {
            var options = new[] { "5 minutos", "15 minutos", "30 minutos", "45 minutos", "60 minutos", "Desactivar" };
            var result = await DisplayActionSheet("Temporizador de sueño", "Cancelar", null, options);

            int minutes = result switch
            {
                "5 minutos"  => 5,
                "15 minutos" => 15,
                "30 minutos" => 30,
                "45 minutos" => 45,
                "60 minutos" => 60,
                "Desactivar" => 0,
                _ => -1
            };

            if (minutes >= 0)
                await _player.SetSleepTimerAsync(minutes);
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
