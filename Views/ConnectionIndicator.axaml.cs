using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class ConnectionIndicator : UserControl
{
    private ConnectionRegistry? _registry;
    private string? _activeConnectionId;

    /// <summary>Fired when the user selects a connection from the flyout.</summary>
    public event Action<ManagedConnection>? ConnectionSelected;

    /// <summary>Fired when the user clicks "+ New Connection..." in the flyout.</summary>
    public event Action? NewConnectionRequested;

    public ConnectionIndicator()
    {
        InitializeComponent();
        IndicatorButton.Click += (_, _) => ShowFlyout();
    }

    public void Initialize(ConnectionRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Set the displayed connection (dot color + label text).
    /// Pass null to show "Not connected" state.
    /// </summary>
    public void SetActiveConnection(ManagedConnection? managed)
    {
        if (managed != null)
        {
            _activeConnectionId = managed.Id;
            ConnectionLabel.Text = managed.Config.Name ?? managed.Config.Server;
            try
            {
                ConnectionDot.Fill = new SolidColorBrush(Color.Parse(managed.Color ?? "#88a1bb"));
            }
            catch
            {
                ConnectionDot.Fill = new SolidColorBrush(Color.Parse("#88a1bb"));
            }
        }
        else
        {
            _activeConnectionId = null;
            ConnectionLabel.Text = "Not connected";
            if (Application.Current?.Resources.TryGetResource("TextSecondary", null, out var res) == true && res is IBrush b)
                ConnectionDot.Fill = b;
        }
    }

    /// <summary>
    /// Set display from a SavedConnection (for tabs that don't use ManagedConnection directly).
    /// </summary>
    public void SetActiveConnection(SavedConnection? config)
    {
        if (config != null)
        {
            _activeConnectionId = config.Id;
            ConnectionLabel.Text = config.Name ?? config.Server;
            try
            {
                ConnectionDot.Fill = new SolidColorBrush(Color.Parse(config.Color ?? "#88a1bb"));
            }
            catch
            {
                ConnectionDot.Fill = new SolidColorBrush(Color.Parse("#88a1bb"));
            }
        }
        else
        {
            SetActiveConnection((ManagedConnection?)null);
        }
    }

    private void ShowFlyout()
    {
        if (_registry == null) return;

        var menu = new MenuFlyout();

        foreach (var managed in _registry.Connections)
        {
            var item = new MenuItem();

            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };

            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            try
            {
                dot.Fill = new SolidColorBrush(Color.Parse(managed.Color ?? "#88a1bb"));
            }
            catch
            {
                dot.Fill = new SolidColorBrush(Color.Parse("#88a1bb"));
            }
            panel.Children.Add(dot);

            var label = new TextBlock
            {
                Text = managed.Config.Name ?? managed.Config.Server,
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            panel.Children.Add(label);

            // Show connected status (but not for the currently active connection — it may be
            // connected via a different path like Compare tab's own connection flow)
            if (!managed.IsConnected && managed.Id != _activeConnectionId)
            {
                var status = new TextBlock
                {
                    Text = "(disconnected)",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse("#999999")),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                panel.Children.Add(status);
            }

            item.Header = panel;

            // Bold the active connection
            if (managed.Id == _activeConnectionId)
                label.FontWeight = FontWeight.SemiBold;

            var captured = managed;
            item.Click += (_, _) => ConnectionSelected?.Invoke(captured);

            menu.Items.Add(item);
        }

        // Separator + New Connection
        if (_registry.Connections.Count > 0)
            menu.Items.Add(new Separator());

        var newItem = new MenuItem
        {
            Header = "+ New Connection..."
        };
        newItem.Click += (_, _) => NewConnectionRequested?.Invoke();
        menu.Items.Add(newItem);

        menu.ShowAt(IndicatorButton);
    }
}
