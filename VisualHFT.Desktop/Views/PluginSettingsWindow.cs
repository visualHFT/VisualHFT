using System.Collections;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VisualHFT.Commons.Studies;
using VisualHFT.Desktop.Hosting;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using HostPlugins = VisualHFT.PluginManager.PluginManager;
using VisualHFT.UserSettings;

namespace VisualHFT.Desktop.Views;

public sealed class PluginSettingsWindow : Window
{
    private readonly IPlugin _plugin;
    private readonly List<IEditor> _editors = new();
    private readonly TextBlock _status;

    public PluginSettingsWindow(IPlugin plugin, string? followCaption = null)
    {
        _plugin = plugin;
        Title = plugin.Name + " settings";
        Width = 560;
        Height = 680;
        Background = new SolidColorBrush(Color.Parse("#0E1116"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var form = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(16) };
        var settings = plugin.Settings;
        var boundToWatchlist = plugin is IStudy or IMultiStudy;
        if (boundToWatchlist)
        {
            form.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(followCaption)
                    ? "This study follows the ticker selected in the watchlist. It does not pick its own symbol."
                    : "Following watchlist  " + followCaption,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85
            });
        }
        if (settings == null)
        {
            form.Children.Add(new TextBlock { Text = "This plugin has no settings object." });
        }
        else
        {
            var error = settings.GetConfigurationError();
            if (!string.IsNullOrEmpty(error) && !boundToWatchlist)
                form.Children.Add(new TextBlock { Text = error, Foreground = Brushes.Goldenrod, TextWrapping = TextWrapping.Wrap });

            foreach (var property in settings.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                    continue;
                if (boundToWatchlist && IsWatchlistBound(property.Name))
                    continue;
                var editor = CreateEditor(property, settings);
                if (editor == null)
                    continue;
                _editors.Add(editor);
                form.Children.Add(editor.Build());
            }
        }

        _status = new TextBlock { Foreground = Brushes.MediumSeaGreen, TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "Save and restart plugin", HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += async (_, _) => await Save();
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(16, 0, 16, 16),
            Children = { save, close, _status }
        };

        Content = new DockPanel
        {
            Children =
            {
                buttons,
                new ScrollViewer { Content = form }
            }
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
    }

    private async Task Save()
    {
        try
        {
            foreach (var editor in _editors)
                editor.Write();

            if (_plugin.Settings != null)
            {
                var key = _plugin is IStudy or IMultiStudy ? SettingKey.TILE_STUDY : SettingKey.PLUGIN;
                SettingsManager.Instance.SetSetting(key, _plugin.GetPluginUniqueID(), _plugin.Settings);
            }

            _status.Text = "Restarting plugin…";
            await HostPlugins.RestartPluginAsync(_plugin);
            _status.Text = "Saved. Plugin restarted.";
        }
        catch (Exception ex)
        {
            _status.Foreground = Brushes.IndianRed;
            _status.Text = ex.Message;
        }
    }

    private static IEditor? CreateEditor(PropertyInfo property, ISetting settings)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(string))
            return new TextEditor(property, settings, mask: IsSecret(property.Name));
        if (type == typeof(bool))
            return new BoolEditor(property, settings);
        if (type.IsEnum)
            return new EnumEditor(property, settings, type);
        if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            return new TextEditor(property, settings, mask: false);
        if (typeof(IEnumerable<string>).IsAssignableFrom(type) && type != typeof(string))
            return new StringListEditor(property, settings);
        if (type == typeof(Provider))
            return new ProviderEditor(property, settings);
        return null;
    }

    private static bool IsWatchlistBound(string name) =>
        name.Equals("Symbol", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Provider", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ProviderID", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ProviderName", StringComparison.OrdinalIgnoreCase);

    private static bool IsSecret(string name) =>
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("pass", StringComparison.OrdinalIgnoreCase);

    private interface IEditor
    {
        Control Build();
        void Write();
    }

    private sealed class TextEditor : IEditor
    {
        private readonly PropertyInfo _property;
        private readonly ISetting _settings;
        private readonly bool _mask;
        private TextBox _box = null!;

        public TextEditor(PropertyInfo property, ISetting settings, bool mask)
        {
            _property = property;
            _settings = settings;
            _mask = mask;
        }

        public Control Build()
        {
            _box = new TextBox
            {
                Text = Convert.ToString(_property.GetValue(_settings), CultureInfo.InvariantCulture) ?? "",
                PasswordChar = _mask ? '•' : default
            };
            return Labeled(_property.Name, _box);
        }

        public void Write()
        {
            var target = Nullable.GetUnderlyingType(_property.PropertyType) ?? _property.PropertyType;
            if (target == typeof(string))
            {
                _property.SetValue(_settings, _box.Text ?? "");
                return;
            }
            if (string.IsNullOrWhiteSpace(_box.Text))
                return;
            var converted = Convert.ChangeType(_box.Text, target, CultureInfo.InvariantCulture);
            _property.SetValue(_settings, converted);
        }
    }

    private sealed class BoolEditor : IEditor
    {
        private readonly PropertyInfo _property;
        private readonly ISetting _settings;
        private CheckBox _box = null!;

        public BoolEditor(PropertyInfo property, ISetting settings)
        {
            _property = property;
            _settings = settings;
        }

        public Control Build()
        {
            _box = new CheckBox { Content = _property.Name, IsChecked = _property.GetValue(_settings) as bool? };
            return _box;
        }

        public void Write() => _property.SetValue(_settings, _box.IsChecked == true);
    }

    private sealed class EnumEditor : IEditor
    {
        private readonly PropertyInfo _property;
        private readonly ISetting _settings;
        private readonly Type _enumType;
        private ComboBox _box = null!;

        public EnumEditor(PropertyInfo property, ISetting settings, Type enumType)
        {
            _property = property;
            _settings = settings;
            _enumType = enumType;
        }

        public Control Build()
        {
            _box = new ComboBox { ItemsSource = Enum.GetNames(_enumType), SelectedItem = _property.GetValue(_settings)?.ToString() };
            return Labeled(_property.Name, _box);
        }

        public void Write()
        {
            if (_box.SelectedItem is string name)
                _property.SetValue(_settings, Enum.Parse(_enumType, name));
        }
    }

    private sealed class StringListEditor : IEditor
    {
        private readonly PropertyInfo _property;
        private readonly ISetting _settings;
        private TextBox _box = null!;

        public StringListEditor(PropertyInfo property, ISetting settings)
        {
            _property = property;
            _settings = settings;
        }

        public Control Build()
        {
            var current = _property.GetValue(_settings) as IEnumerable<string> ?? Array.Empty<string>();
            _box = new TextBox { Text = string.Join(", ", current), AcceptsReturn = true, MinHeight = 64 };
            return Labeled(_property.Name + " (comma separated)", _box);
        }

        public void Write()
        {
            var items = (_box.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (_property.PropertyType.IsAssignableFrom(typeof(List<string>)))
                _property.SetValue(_settings, items);
            else if (typeof(IList).IsAssignableFrom(_property.PropertyType))
            {
                if (_property.GetValue(_settings) is IList list)
                {
                    list.Clear();
                    foreach (var item in items)
                        list.Add(item);
                }
            }
        }
    }

    private sealed class ProviderEditor : IEditor
    {
        private readonly PropertyInfo _property;
        private readonly ISetting _settings;
        private TextBox _id = null!;
        private TextBox _name = null!;

        public ProviderEditor(PropertyInfo property, ISetting settings)
        {
            _property = property;
            _settings = settings;
        }

        public Control Build()
        {
            var provider = _property.GetValue(_settings) as Provider;
            _id = new TextBox { Text = provider?.ProviderID.ToString(CultureInfo.InvariantCulture) ?? "0", Width = 80 };
            _name = new TextBox { Text = provider?.ProviderName ?? "", MinWidth = 180 };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { _id, _name }
            };
            return Labeled(_property.Name, row);
        }

        public void Write()
        {
            var provider = _property.GetValue(_settings) as Provider ?? new Provider();
            if (int.TryParse(_id.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                provider.ProviderID = id;
            provider.ProviderName = _name.Text ?? "";
            _property.SetValue(_settings, provider);
        }
    }

    private static Control Labeled(string label, Control input)
    {
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, Foreground = Brushes.LightGray },
                input
            }
        };
    }
}
