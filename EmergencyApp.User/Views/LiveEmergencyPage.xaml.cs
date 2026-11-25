using EmergencyApp.User.ViewModels;

namespace EmergencyApp.User.Views;

public partial class LiveEmergencyPage : ContentPage
{
    private readonly LiveEmergencyViewModel _vm;

    public LiveEmergencyPage(LiveEmergencyViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.AttachAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Detach();
    }
}
