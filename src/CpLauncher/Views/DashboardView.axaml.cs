using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CpLauncher.ViewModels;

namespace CpLauncher.Views;

/// <summary>
/// The dashboard. The code here only runs the three effects (see <see cref="Effects"/>): the art comes in on
/// load, the dialog's gem flashes when the state of Icon.utx changes, and after "Zastosuj" installed the changes
/// the icon slots of the selected packs flash one after another.
/// </summary>
public partial class DashboardView : UserControl
{
    private static readonly TimeSpan SlotStep = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan SlotFlash = TimeSpan.FromMilliseconds(450);

    private DashboardViewModel? _viewModel;
    private string? _gemState;

    public DashboardView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ChangesInstalled -= OnChangesInstalled;
        }

        _viewModel = DataContext as DashboardViewModel;
        _gemState = null;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ChangesInstalled += OnChangesInstalled;
            _gemState = DescribeGem(_viewModel);
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Effects.Entrance(HeroArt, 16, 0, TimeSpan.FromMilliseconds(450), TimeSpan.Zero);
        Effects.Entrance(Guide, 0, 6, TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(100));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null || e.PropertyName is not (nameof(DashboardViewModel.IconState) or nameof(DashboardViewModel.HasPendingChanges)))
        {
            return;
        }

        string? state = DescribeGem(_viewModel);
        string? previous = _gemState;
        _gemState = state;
        // The first real state after "Sprawdzanie…" is not a change the member made, so it does not flash.
        if (previous is null || state is null || state == previous)
        {
            return;
        }

        Shape? gem = StatusDialog.GetVisualDescendants().OfType<Shape>().FirstOrDefault(shape => shape.Name == "PART_Gem");
        Ellipse? glow = StatusDialog.GetVisualDescendants().OfType<Ellipse>().FirstOrDefault(ellipse => ellipse.Name == "PART_Glow");
        if (gem is not null && glow is not null)
        {
            Effects.Flash(gem, glow);
        }
    }

    private void OnChangesInstalled(object? sender, EventArgs e)
    {
        TimeSpan delay = TimeSpan.Zero;
        foreach (Border pack in PacksList.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("pack")))
        {
            if (pack.DataContext is not PackViewModel { IsSelected: true })
            {
                continue;
            }

            foreach (Border flash in pack.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("flash") && border.IsEffectivelyVisible))
            {
                Effects.Pulse(flash, delay, SlotFlash);
                delay += SlotStep;
            }
        }
    }

    /// <summary>What the gem shows: null until the launcher has read Icon.utx.</summary>
    private static string? DescribeGem(DashboardViewModel viewModel)
    {
        if (viewModel.IconStatus is null)
        {
            return null;
        }

        return $"{viewModel.IconState.Severity}|{viewModel.HasPendingChanges}";
    }
}
