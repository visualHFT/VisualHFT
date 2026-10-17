using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using VisualHFT.Desktop.Hosting;
using VisualHFT.Desktop.Screens;
using VisualHFT.PluginManager;
using HostPlugins = VisualHFT.PluginManager.PluginManager;
using VisualHFT.TriggerEngine;

namespace VisualHFT.Desktop.Views;

public sealed class TriggerRuleWindow : Window
{
    private readonly TriggerDraft _draft;
    private readonly List<StudyDescriptor> _studies;
    private readonly TextBox _name;
    private readonly StackPanel _conditions;
    private readonly CheckBox _alert;
    private readonly TextBox _alertCooldown;
    private readonly CheckBox _webhook;
    private readonly TextBox _url;
    private readonly TextBox _method;
    private readonly TextBox _body;
    private readonly TextBox _hookCooldown;
    private readonly ComboBox _hookUnit;
    private readonly StackPanel _headers;
    private readonly TextBlock _status;
    private readonly List<ConditionEditor> _editors = new();

    public TriggerRuleWindow(TriggerRule? existing = null)
    {
        _studies = HostPlugins.GetSelectableStudies().ToList();
        _draft = existing == null ? new TriggerDraft() : TriggerDraft.FromRule(existing);
        if (_draft.Conditions.Count == 0)
            _draft.Conditions.Add(new ConditionDraft());

        Title = existing == null ? "New trigger rule" : "Edit trigger rule";
        Width = 640;
        Height = 720;
        Background = new SolidColorBrush(Color.Parse("#0E1116"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name = new TextBox { Text = _draft.Name };
        _conditions = new StackPanel { Spacing = 8 };
        _alert = new CheckBox { Content = "Notify in the app", IsChecked = _draft.AlertInApp };
        _alertCooldown = new TextBox { Text = _draft.AlertCooldownSeconds.ToString(CultureInfo.InvariantCulture), Width = 80 };
        _webhook = new CheckBox { Content = "Webhook", IsChecked = _draft.UseWebhook };
        _url = new TextBox { Text = _draft.WebhookUrl, Watermark = "https://" };
        _method = new TextBox { Text = _draft.WebhookMethod, Width = 80 };
        _body = new TextBox { Text = _draft.BodyTemplate, AcceptsReturn = true, MinHeight = 72 };
        _hookCooldown = new TextBox { Text = _draft.WebhookCooldownSeconds.ToString(CultureInfo.InvariantCulture), Width = 80 };
        _hookUnit = new ComboBox { ItemsSource = Enum.GetNames<TimeWindowUnit>(), SelectedItem = _draft.WebhookCooldownUnit.ToString(), Width = 140 };
        _headers = new StackPanel { Spacing = 4 };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };

        foreach (var condition in _draft.Conditions)
            AddEditor(condition);
        foreach (var header in _draft.Headers)
            AddHeader(header.Name, header.Value);
        if (_draft.Headers.Count == 0)
            AddHeader("", "");

        var addCondition = new Button { Content = "Add condition", Classes = { "quiet" } };
        addCondition.Click += (_, _) => AddEditor(new ConditionDraft());
        var addHeader = new Button { Content = "Add header", Classes = { "quiet" } };
        addHeader.Click += (_, _) => AddHeader("", "");
        var save = new Button { Content = "Save rule" };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 8,
                Children =
                {
                    Label("Name", _name),
                    new TextBlock { Text = "Conditions (all must hold)", FontWeight = FontWeight.SemiBold },
                    _conditions,
                    addCondition,
                    _alert,
                    Label("Alert cooldown seconds", _alertCooldown),
                    _webhook,
                    Label("Webhook URL", _url),
                    Label("Method", _method),
                    Label("Body template", _body),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { Label("Webhook cooldown", _hookCooldown), _hookUnit }
                    },
                    new TextBlock { Text = "Headers", FontWeight = FontWeight.SemiBold },
                    _headers,
                    addHeader,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { save, cancel }
                    },
                    _status
                }
            }
        };
    }

    private void AddEditor(ConditionDraft condition)
    {
        var editor = new ConditionEditor(_studies, condition);
        _editors.Add(editor);
        _conditions.Children.Add(editor.Root);
    }

    private void AddHeader(string name, string value)
    {
        var nameBox = new TextBox { Text = name, Watermark = "Name", Width = 160 };
        var valueBox = new TextBox { Text = value, Watermark = "Value", MinWidth = 200 };
        _headers.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { nameBox, valueBox }
        });
    }

    private void Save()
    {
        _draft.Name = _name.Text ?? "";
        _draft.Conditions.Clear();
        foreach (var editor in _editors)
            _draft.Conditions.Add(editor.Read());
        _draft.AlertInApp = _alert.IsChecked == true;
        _draft.AlertCooldownSeconds = int.TryParse(_alertCooldown.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var alertCooldown) ? alertCooldown : 0;
        _draft.UseWebhook = _webhook.IsChecked == true;
        _draft.WebhookUrl = _url.Text ?? "";
        _draft.WebhookMethod = _method.Text ?? "POST";
        _draft.BodyTemplate = _body.Text ?? "";
        _draft.WebhookCooldownSeconds = int.TryParse(_hookCooldown.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hookCooldown) ? hookCooldown : 0;
        if (Enum.TryParse<TimeWindowUnit>(_hookUnit.SelectedItem as string, out var unit))
            _draft.WebhookCooldownUnit = unit;
        _draft.Headers.Clear();
        foreach (var child in _headers.Children.OfType<StackPanel>())
        {
            var boxes = child.Children.OfType<TextBox>().ToList();
            if (boxes.Count == 2 && !string.IsNullOrWhiteSpace(boxes[0].Text))
                _draft.Headers.Add(new HeaderDraft { Name = boxes[0].Text ?? "", Value = boxes[1].Text ?? "" });
        }

        var error = _draft.Validate(_studies.Select(study => study.Id).ToHashSet());
        if (error != null)
        {
            _status.Text = error;
            return;
        }

        TriggerEngineService.AddOrUpdateRule(_draft.ToRule());
        Close();
    }

    private static Control Label(string title, Control input)
    {
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, Foreground = Brushes.LightGray },
                input
            }
        };
    }

    private sealed class ConditionEditor
    {
        private readonly ComboBox _study;
        private readonly ComboBox _operator;
        private readonly TextBox _threshold;
        private readonly TextBox _window;
        private readonly ComboBox _unit;

        public ConditionEditor(IReadOnlyList<StudyDescriptor> studies, ConditionDraft condition)
        {
            _study = new ComboBox
            {
                ItemsSource = studies,
                MinWidth = 220,
                ItemTemplate = new FuncDataTemplate<StudyDescriptor>((study, _) =>
                    new TextBlock
                    {
                        Text = study == null
                            ? ""
                            : string.IsNullOrEmpty(study.GroupName) ? study.DisplayName : study.GroupName + " / " + study.DisplayName
                    }),
                SelectedItem = studies.FirstOrDefault(study => study.Id == condition.StudyId) ?? (studies.Count > 0 ? studies[0] : null)
            };
            _operator = new ComboBox
            {
                ItemsSource = Enum.GetNames<ConditionOperator>(),
                SelectedItem = condition.Operator.ToString(),
                Width = 140
            };
            _threshold = new TextBox { Text = condition.Threshold.ToString(CultureInfo.InvariantCulture), Width = 90 };
            _window = new TextBox { Text = condition.WindowDuration.ToString(CultureInfo.InvariantCulture), Width = 70 };
            _unit = new ComboBox
            {
                ItemsSource = Enum.GetNames<TimeWindowUnit>(),
                SelectedItem = condition.WindowUnit.ToString(),
                Width = 120
            };
            Root = new Border
            {
                BorderBrush = Brushes.DimGray,
                BorderThickness = new Avalonia.Thickness(1),
                Padding = new Avalonia.Thickness(8),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        _study,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children = { _operator, _threshold, new TextBlock { Text = "for", VerticalAlignment = VerticalAlignment.Center }, _window, _unit }
                        }
                    }
                }
            };
        }

        public Control Root { get; }

        public ConditionDraft Read()
        {
            Enum.TryParse<ConditionOperator>(_operator.SelectedItem as string, out var op);
            Enum.TryParse<TimeWindowUnit>(_unit.SelectedItem as string, out var unit);
            double.TryParse(_threshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold);
            int.TryParse(_window.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var window);
            return new ConditionDraft
            {
                StudyId = (_study.SelectedItem as StudyDescriptor)?.Id ?? "",
                Operator = op,
                Threshold = threshold,
                WindowDuration = window,
                WindowUnit = unit
            };
        }
    }
}
