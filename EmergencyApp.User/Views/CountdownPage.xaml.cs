using EmergencyApp.User.ViewModels;

namespace EmergencyApp.User.Views;

public partial class CountdownPage : ContentPage
{
    private readonly CountdownViewModel _vm;

    public CountdownPage(CountdownViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.StartAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Stop();   // leaving the screen (e.g. back button) cancels the countdown
    }
}
