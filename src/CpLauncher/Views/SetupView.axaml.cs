using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CpLauncher.Views;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Effects.Entrance(Mechanic, 0, 6, TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(100));
    }
}
