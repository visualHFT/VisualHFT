using System.Collections.ObjectModel;
using Avalonia.Media;
using VisualHFT.Desktop.Screens;

namespace VisualHFT.Desktop.ViewModels;

public sealed class SettingsGroupRow : ObservableObject
{
    private readonly Action _edited;
    private bool _isSelected;
    private bool _isShown = true;

    public SettingsGroupRow(Action edited) => _edited = edited;

    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Eyebrow { get; init; } = "";
    public string Summary { get; init; } = "";
    public bool HasSummary { get; init; }
    public string StatusText { get; init; } = "";
    public bool HasStatus { get; init; }
    public IBrush StatusBrush { get; init; } = Brushes.Transparent;
    public ObservableCollection<SettingsSectionRow> Sections { get; } = new();
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsShown { get => _isShown; set => Set(ref _isShown, value); }
    public bool IsDirty => Sections.Any(section => section.Fields.Any(row => row.IsDirty));

    public void NotifyEdited()
    {
        Raise(nameof(IsDirty));
        _edited();
    }

    public static SettingsGroupRow Create(SettingsGroupModel model, Action edited)
    {
        var group = new SettingsGroupRow(edited)
        {
            Key = model.Key,
            Title = model.Title,
            Eyebrow = model.Kind.ToUpperInvariant(),
            Summary = model.Summary,
            HasSummary = model.Summary.Length > 0 && model.Title.IndexOf(model.Summary, StringComparison.OrdinalIgnoreCase) < 0,
            StatusText = model.StatusText,
            HasStatus = model.StatusText.Length > 0,
            StatusBrush = BrushFor(model.StatusTone)
        };
        foreach (var section in model.Sections)
        {
            var row = new SettingsSectionRow { Title = section.Title };
            foreach (var field in section.Fields)
                row.Fields.Add(new SettingsFieldRow(field, group));
            group.Sections.Add(row);
        }
        return group;
    }

    private static IBrush BrushFor(string tone) => tone switch
    {
        "ok" => Ok,
        "warn" => Warn,
        "bad" => Bad,
        _ => Idle
    };

    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#3DDC84"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F5C16C"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#9AA4B2"));
}

public sealed class SettingsSectionRow : ObservableObject
{
    private bool _isShown = true;

    public string Title { get; init; } = "";
    public ObservableCollection<SettingsFieldRow> Fields { get; } = new();
    public bool IsShown { get => _isShown; set => Set(ref _isShown, value); }
}

public sealed class SettingsFieldRow : ObservableObject
{
    private readonly SettingsGroupRow _group;
    private readonly string _original;
    private string _value;
    private bool _revealed;
    private bool _isShown = true;

    public SettingsFieldRow(SettingsFieldModel model, SettingsGroupRow group)
    {
        _group = group;
        _original = model.Value;
        _value = model.Value;
        Label = model.Label;
        Hint = model.Hint;
        Kind = model.Kind;
        Path = model.ListPath ?? model.Path;
        ListPath = model.ListPath;
        Choices = model.Choices;
    }

    public string Label { get; }
    public string Hint { get; }
    public SettingsFieldKind Kind { get; }
    public string Path { get; }
    public string? ListPath { get; }
    public IReadOnlyList<SettingsChoice> Choices { get; }
    public bool HasHint => Hint.Length > 0;
    public bool IsText => Kind == SettingsFieldKind.Text;
    public bool IsSecret => Kind == SettingsFieldKind.Secret;
    public bool IsMultiline => Kind == SettingsFieldKind.Multiline;
    public bool IsBool => Kind == SettingsFieldKind.Bool;
    public bool IsChoice => Kind == SettingsFieldKind.Choice;
    public bool ShowLabeled => !IsBool;
    public bool ShowLabeledHint => ShowLabeled && HasHint;
    public bool ShowBoolHint => IsBool && HasHint;
    public bool IsShown { get => _isShown; set => Set(ref _isShown, value); }
    public bool IsDirty => Canon(_value) != Canon(_original);
    public double DirtyOpacity => IsDirty ? 1 : 0;
    public string Watermark => Kind switch
    {
        SettingsFieldKind.Multiline => "One symbol per line",
        SettingsFieldKind.Text or SettingsFieldKind.Secret => "Not set",
        _ => ""
    };
    public char PasswordChar => _revealed ? '\0' : '•';
    public string RevealLabel => _revealed ? "Hide" : "Show";

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value ?? ""))
                return;
            Raise(nameof(IsDirty));
            Raise(nameof(DirtyOpacity));
            Raise(nameof(Flag));
            Raise(nameof(SelectedChoice));
            _group.NotifyEdited();
        }
    }

    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (!Set(ref _revealed, value))
                return;
            Raise(nameof(PasswordChar));
            Raise(nameof(RevealLabel));
        }
    }

    public bool Flag
    {
        get => bool.TryParse(_value, out var flag) && flag;
        set
        {
            var text = value ? "True" : "False";
            if (text != _value)
                Value = text;
        }
    }

    public SettingsChoice? SelectedChoice
    {
        get
        {
            foreach (var choice in Choices)
            {
                if (choice.Value == _value)
                    return choice;
            }
            return null;
        }
        set
        {
            if (value == null || value.Value == _value)
                return;
            Value = value.Value;
        }
    }

    private static string Canon(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
