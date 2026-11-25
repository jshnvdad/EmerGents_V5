using System.Media;
using System.Windows;
using System.Windows.Threading;
using EmergencyDesk.ViewModels;

namespace EmergencyDesk.Views;

/// <summary>
/// Always-on-top emergency popup: flashing border + repeating sound until the operator acts.
/// Driven entirely by MainViewModel (CurrentAlert / commands); the VM opens and closes it.
/// </summary>
public partial class AlertWindow : Window
{
    private readonly DispatcherTimer _sound = new() { Interval = TimeSpan.FromSeconds(2) };

    public AlertWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        _sound.Tick += (_, _) => Beep(vm);
        Loaded += (_, _) => { Beep(vm); _sound.Start(); Activate(); };
        Closed += (_, _) => _sound.Stop();
    }

    private static void Beep(MainViewModel vm)
    {
        if (!vm.SoundEnabled) return;
        // Escalated alerts use the harsher system sound.
        (vm.CurrentAlert?.IsEscalated == true ? SystemSounds.Hand : SystemSounds.Exclamation).Play();
    }
}
