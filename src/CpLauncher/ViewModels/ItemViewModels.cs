using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CpLauncher.Services;
using L2Mod.Core.Imaging;
using L2Mod.Core.Ini;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;

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

/// <summary>
/// A pack of the manifest. A pack without options is selected as a whole (<see cref="IsSelected"/>); a pack with
/// options has one checkbox per option and its own checkbox shows all / some (null) / none and sets all of them.
/// </summary>
public partial class PackViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _settingOptions;

    public PackViewModel(Pack pack, bool isSelected, Action selectionChanged)
        : this(pack, isSelected, Array.Empty<bool>(), selectionChanged)
    {
    }

    /// <param name="optionSelection">For a pack with options: whether each option is selected, in manifest order.</param>
    public PackViewModel(Pack pack, bool isSelected, IReadOnlyList<bool> optionSelection, Action selectionChanged)
    {
        Pack = pack;
        _selectionChanged = selectionChanged;
        for (int i = 0; i < pack.Options.Count; i++)
        {
            bool selected = i < optionSelection.Count && optionSelection[i];
            Options.Add(new PackOptionViewModel(pack, pack.Options[i], selected, OnOptionChanged));
        }

        _isSelected = isSelected;
        if (HasOptions)
        {
            _isSelected = Options.Any(option => option.IsSelected);
        }
    }

    public Pack Pack { get; }

    public ObservableCollection<PackOptionViewModel> Options { get; } = new ObservableCollection<PackOptionViewModel>();

    public bool HasOptions
    {
        get
        {
            return Options.Count > 0;
        }
    }

    /// <summary>The pack's checkbox: for a pack with options true when all are selected, null when some are.
    /// Setting it selects or clears every option.</summary>
    public bool? IsChecked
    {
        get
        {
            if (!HasOptions)
            {
                return IsSelected;
            }

            int selected = Options.Count(option => option.IsSelected);
            if (selected == 0)
            {
                return false;
            }

            if (selected == Options.Count)
            {
                return true;
            }

            return null;
        }

        set
        {
            bool select = value == true;
            if (!HasOptions)
            {
                IsSelected = select;
                return;
            }

            _settingOptions = true;
            try
            {
                foreach (PackOptionViewModel option in Options)
                {
                    option.IsSelected = select;
                }
            }
            finally
            {
                _settingOptions = false;
            }

            OnOptionChanged();
        }
    }

    /// <summary>The selection ids of this pack: its id when selected, or each selected option's selection id.</summary>
    public IEnumerable<string> SelectedIds
    {
        get
        {
            if (!HasOptions)
            {
                if (IsSelected)
                {
                    yield return Pack.Id;
                }

                yield break;
            }

            foreach (PackOptionViewModel option in Options)
            {
                if (option.IsSelected)
                {
                    yield return option.SelectionId;
                }
            }
        }
    }

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

    /// <summary>"22 ikony" for an icon pack, "wybrano 3 z 9" for a pack with options, "1 efekt" otherwise.</summary>
    public string SizeText
    {
        get
        {
            if (HasOptions)
            {
                return $"wybrano {Options.Count(option => option.IsSelected)} z {Options.Count}";
            }

            int icons = Pack.Operations.OfType<IIconOperation>().Count();
            if (icons > 0)
            {
                return $"{icons} {Polish.Plural(icons, "ikona", "ikony", "ikon")}";
            }

            int count = Pack.Operations.Count;
            return $"{count} {Polish.Plural(count, "efekt", "efekty", "efektów")}";
        }
    }

    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; } = new ObservableCollection<ThumbnailViewModel>();

    /// <summary>For a pack with options: any option selected (setting it selects or clears all).</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditable = true;

    [ObservableProperty]
    private string? _previewNote;

    /// <summary>"+16": the icons of the pack beyond the preview strip, shown as the strip's last slot; null when none.</summary>
    [ObservableProperty]
    private string? _overflowText;

    public void SetPreview(PackPreview preview)
    {
        Thumbnails.Clear();
        foreach (PreviewIcon icon in preview.Icons)
        {
            Thumbnails.Add(new ThumbnailViewModel(icon.Target, icon.Image));
        }

        foreach (PackOptionViewModel option in Options)
        {
            option.Thumbnails.Clear();
            if (preview.OptionIcons.TryGetValue(option.Option.Id, out IReadOnlyList<PreviewIcon>? icons))
            {
                foreach (PreviewIcon icon in icons)
                {
                    option.Thumbnails.Add(new ThumbnailViewModel(icon.Target, icon.Image));
                }
            }
        }

        int targets = Pack.Operations.OfType<IIconOperation>().Select(operation => operation.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int hidden = targets - preview.Icons.Count - preview.Failed;
        OverflowText = hidden > 0 ? $"+{hidden}" : null;
        PreviewNote = preview.Failed > 0 ? $"{preview.Failed} bez podglądu (brak ikony w Twoim Icon.utx lub obrazka)" : null;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (HasOptions)
        {
            bool any = Options.Any(option => option.IsSelected);
            if (value != any)
            {
                IsChecked = value;
            }

            return;
        }

        OnPropertyChanged(nameof(IsChecked));
        _selectionChanged();
    }

    partial void OnIsEditableChanged(bool value)
    {
        foreach (PackOptionViewModel option in Options)
        {
            option.IsEditable = value;
        }
    }

    private void OnOptionChanged()
    {
        if (_settingOptions)
        {
            return;
        }

        IsSelected = Options.Any(option => option.IsSelected);
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(SizeText));
        _selectionChanged();
    }
}

/// <summary>One choice of a pack with options, e.g. one skill's icon over the target.</summary>
public partial class PackOptionViewModel : ObservableObject
{
    private readonly Action _changed;

    public PackOptionViewModel(Pack pack, PackOption option, bool isSelected, Action changed)
    {
        Option = option;
        SelectionId = pack.GetSelectionId(option);
        _isSelected = isSelected;
        _changed = changed;
    }

    public PackOption Option { get; }

    /// <summary>"&lt;pack id&gt;/&lt;option id&gt;", as stored in the settings and passed to the installer.</summary>
    public string SelectionId { get; }

    public string Name
    {
        get
        {
            return Option.Name;
        }
    }

    public string? Description
    {
        get
        {
            return Option.Description;
        }
    }

    /// <summary>The icons of the skills the option is about (one, or the two of an effect swap).</summary>
    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; } = new ObservableCollection<ThumbnailViewModel>();

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditable = true;

    partial void OnIsSelectedChanged(bool value)
    {
        _changed();
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
