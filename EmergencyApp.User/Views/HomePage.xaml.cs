using EmergencyApp.User.ViewModels;

namespace EmergencyApp.User.Views;

public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _vm;

    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    private void OnPressed(object? sender, EventArgs e) => _vm.BeginHold();
    private void OnReleased(object? sender, EventArgs e) => _vm.EndHold();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnAppearingAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.OnDisappearing();
    }
}
