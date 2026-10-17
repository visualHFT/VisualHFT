using System.Collections;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VisualHFT.Desktop.Screens;

namespace VisualHFT.Desktop.Views;

public sealed class NotificationBell : Window
{
    private readonly Action _markRead;
    private readonly Action _clear;
    private readonly StackPanel _feed;
    private readonly TextBlock _empty;
    private readonly TextBlock _badge;
    private IReadOnlyList<NotificationNote> _notes = Array.Empty<NotificationNote>();

    public NotificationBell(Action markRead, Action clear, IEnumerable notes)
    {
        ArgumentNullException.ThrowIfNull(markRead);
        ArgumentNullException.ThrowIfNull(clear);
        _markRead = markRead;
        _clear = clear;

        Title = "Notifications";
        Width = 380;
        Height = 420;
        Background = new SolidColorBrush(Color.Parse("#0E1116"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _badge = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeight.SemiBold };
        _empty = new TextBlock
        {
            Text = "No alerts",
            Foreground = new SolidColorBrush(Color.Parse("#9AA4B2")),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 12, 0, 0)
        };
        _feed = new StackPanel { Spacing = 8 };

        var mark = new Button { Content = "Mark read", Classes = { "quiet" } };
        var clearAll = new Button { Content = "Clear", Classes = { "quiet" } };
        mark.Click += (_, _) => MarkRead();
        clearAll.Click += (_, _) => Clear();

        var header = new DockPanel
        {
            Margin = new Avalonia.Thickness(16, 16, 16, 8),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    [DockPanel.DockProperty] = Dock.Right,
                    Children = { mark, clearAll }
                },
                _badge
            }
        };

        Content = new DockPanel
        {
            Children =
            {
                header,
                new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Margin = new Avalonia.Thickness(16, 0, 16, 16),
                        Children = { _empty, _feed }
                    }
                }
            }
        };
        DockPanel.SetDock(header, Dock.Top);
        Bind(notes);
    }

    public IEnumerable Notes => _notes;
    public int UnreadCount => NotificationInbox.UnreadCount(_notes);
    public IReadOnlyList<NotificationGroup> Groups => NotificationInbox.Group(_notes);

    public void Bind(IEnumerable? notes)
    {
        _notes = Capture(notes);
        Rebuild();
    }

    public void TogglePopup()
    {
        if (IsVisible)
            Hide();
        else
            Show();
    }

    private void MarkRead()
    {
        _markRead();
        var read = new NotificationNote[_notes.Count];
        for (var i = 0; i < _notes.Count; i++)
            read[i] = _notes[i] with { IsRead = true };
        _notes = read;
        Rebuild();
    }

    private void Clear()
    {
        _clear();
        _notes = Array.Empty<NotificationNote>();
        Rebuild();
    }

    private void Rebuild()
    {
        var unread = UnreadCount;
        _badge.Text = unread == 0 ? "Notifications" : "Notifications  " + unread;
        var groups = Groups;
        _empty.IsVisible = groups.Count == 0;
        _feed.Children.Clear();
        foreach (var group in groups)
            _feed.Children.Add(Row(group));
    }

    private static Control Row(NotificationGroup group)
    {
        var title = group.Title + (group.Count > 1 ? "  (" + group.Count + ")" : "");
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            Foreground = group.HasUnread ? Brushes.White : new SolidColorBrush(Color.Parse("#9AA4B2"))
        });
        foreach (var note in group.Items)
        {
            panel.Children.Add(new TextBlock
            {
                Text = note.Message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Tone(note.Type)
            });
            panel.Children.Add(new TextBlock
            {
                Text = note.Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 10,
                FontStyle = FontStyle.Italic,
                Foreground = new SolidColorBrush(Color.Parse("#7D8794")),
                Margin = new Avalonia.Thickness(0, 0, 0, 6)
            });
        }
        return panel;
    }

    private static IBrush Tone(string? type)
    {
        if (string.Equals(type, "ERROR", StringComparison.OrdinalIgnoreCase))
            return Brushes.IndianRed;
        if (string.Equals(type, "WARNING", StringComparison.OrdinalIgnoreCase))
            return Brushes.Goldenrod;
        if (string.Equals(type, "TRIGGER_ACTION", StringComparison.OrdinalIgnoreCase))
            return Brushes.DeepSkyBlue;
        return Brushes.LightGray;
    }

    private static IReadOnlyList<NotificationNote> Capture(IEnumerable? notes)
    {
        if (notes == null)
            return Array.Empty<NotificationNote>();
        var list = new List<NotificationNote>();
        foreach (var item in notes)
        {
            switch (item)
            {
                case NotificationNote note:
                    list.Add(note);
                    break;
                case NotificationGroup group:
                    foreach (var nested in group.Items)
                        list.Add(nested);
                    break;
            }
        }
        return list;
    }
}
