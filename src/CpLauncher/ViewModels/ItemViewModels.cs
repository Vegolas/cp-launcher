using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CpLauncher.Services;
using L2Mod.Core.Imaging;
using L2Mod.Core.Ini;
using L2Mod.Core.Manifests;

namespace CpLauncher.ViewModels;

/// <summary>A line of text with a severity (banners, Icon.utx state, result of the last action).</summary>
public sealed class NoticeViewModel
{
    public NoticeViewModel(StatusSeverity severity, string text)
    {
        Severity = severity;
        Text = text;
    }

    public StatusSeverity Severity { get; }

    public string Text { get; }

    public bool IsSuccess
    {
        get
        {
            return Severity == StatusSeverity.Success;
        }
    }

    public bool IsWarning
    {
        get
        {
            return Severity == StatusSeverity.Warning;
        }
    }

    public bool IsError
    {
        get
        {
            return Severity == StatusSeverity.Error;
        }
    }
}

/// <summary>A collapsible dashboard section; whether it is expanded is remembered in settings.json.</summary>
public partial class SectionViewModel : ObservableObject
{
    private readonly Action<SectionViewModel> _expandedChanged;

    public SectionViewModel(string id, bool isExpanded, Action<SectionViewModel> expandedChanged)
    {
        Id = id;
        _isExpanded = isExpanded;
        _expandedChanged = expandedChanged;
    }

    public string Id { get; }

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>What the header says about the content while the section is collapsed.</summary>
    [ObservableProperty]
    private string? _summary;

    partial void OnIsExpandedChanged(bool value)
    {
        _expandedChanged(this);
    }
}

public sealed class ThumbnailViewModel
{
    public ThumbnailViewModel(string target, RgbaImage image)
    {
        Target = target;
        Image = image;
    }

    public string Target { get; }

    public RgbaImage Image { get; }
}

public partial class PackViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public PackViewModel(Pack pack, bool isSelected, Action selectionChanged)
    {
        Pack = pack;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public Pack Pack { get; }

    public string Id
    {
        get
        {
            return Pack.Id;
        }
    }

    public string Name
    {
        get
        {
            return Pack.Name;
        }
    }

    /// <summary>The leader's description of the pack (plain text), or null.</summary>
    public string? Description
    {
        get
        {
            return Pack.Description;
        }
    }

    public string SizeText
    {
        get
        {
            int count = Pack.Operations.Count;
            return $"{count} {Polish.Plural(count, "ikona", "ikony", "ikon")}";
        }
    }

    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; } = new ObservableCollection<ThumbnailViewModel>();

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditable = true;

    [ObservableProperty]
    private string? _previewNote;

    public void SetPreview(PackPreview preview)
    {
        Thumbnails.Clear();
        foreach (PreviewIcon icon in preview.Icons)
        {
            Thumbnails.Add(new ThumbnailViewModel(icon.Target, icon.Image));
        }

        int targets = Pack.Operations.Select(operation => operation.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int hidden = targets - preview.Icons.Count - preview.Failed;
        var notes = new List<string>();
        if (hidden > 0)
        {
            notes.Add($"+ {hidden} {Polish.Plural(hidden, "inna", "inne", "innych")}");
        }

        if (preview.Failed > 0)
        {
            notes.Add($"{preview.Failed} bez podglądu (brak ikony w Twoim Icon.utx lub obrazka)");
        }

        PreviewNote = notes.Count == 0 ? null : string.Join(" · ", notes);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        _selectionChanged();
    }
}

public partial class PresetViewModel : ObservableObject
{
    public PresetViewModel(OptionPreset preset)
    {
        Preset = preset;
    }

    public OptionPreset Preset { get; }

    public string Name
    {
        get
        {
            return Preset.Name;
        }
    }

    public string Summary
    {
        get
        {
            IEnumerable<string> values = Preset.Values.Select(Describe);
            return string.Join(", ", values);
        }
    }

    [ObservableProperty]
    private bool _isApplied;

    private static string Describe(IniValue value)
    {
        return $"{value.Section}.{value.Key} = {value.Value}";
    }
}

public sealed class AnnouncementViewModel
{
    public AnnouncementViewModel(Announcement announcement)
    {
        DateText = Polish.FormatDate(announcement.Date);
        Title = announcement.Title;
        Text = announcement.Text;
    }

    public string DateText { get; }

    public string Title { get; }

    /// <summary>Plain text from the signed manifest; shown in a TextBlock, never interpreted as markup.</summary>
    public string Text { get; }
}
