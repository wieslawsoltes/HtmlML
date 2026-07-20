using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HtmlML.Backends.Avalonia;
using HtmlML.Core;

namespace AvaloniaBackendSample;

public sealed partial class MainWindow : Window
{
    private AvaloniaBackendHost? _backend;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs args)
    {
        _backend = new AvaloniaBackendHost(this);
        _backend.Mount();
        _backend.EnsureCapabilities(
            HtmlMlBackendCapabilities.DomProjection
            | HtmlMlBackendCapabilities.CssLayout
            | HtmlMlBackendCapabilities.Canvas2D
            | HtmlMlBackendCapabilities.Svg
            | HtmlMlBackendCapabilities.PointerInput
            | HtmlMlBackendCapabilities.KeyboardInput
            | HtmlMlBackendCapabilities.Focus
            | HtmlMlBackendCapabilities.Accessibility);

        var root = _backend.CreateNode(new HtmlMlBackendNodeDescriptor(
            new HtmlMlNodeId(1),
            HtmlMlBackendNodeKind.Container,
            "HtmlML backend sample"));
        var heading = _backend.CreateNode(new HtmlMlBackendNodeDescriptor(
            new HtmlMlNodeId(2),
            HtmlMlBackendNodeKind.Text,
            "HtmlML.Backend.Avalonia"));
        var detail = _backend.CreateNode(new HtmlMlBackendNodeDescriptor(
            new HtmlMlNodeId(3),
            HtmlMlBackendNodeKind.Text,
            "Persistent Avalonia visuals projected through IHtmlMlBackendHost"));

        _backend.Attach(_backend.Root, root, 0);
        _backend.Attach(root, heading, 0);
        _backend.Attach(root, detail, 1);
        _backend.Arrange(heading, new HtmlMlRect(48, 56, 600, 48));
        _backend.Arrange(detail, new HtmlMlRect(48, 116, 620, 36));

        var headingControl = heading.Handle.GetRequired<TextBlock>();
        headingControl.FontSize = 30;
        headingControl.FontWeight = FontWeight.SemiBold;
        headingControl.Foreground = Brushes.White;
        var detailControl = detail.Handle.GetRequired<TextBlock>();
        detailControl.FontSize = 16;
        detailControl.Foreground = new SolidColorBrush(Color.Parse("#AEBCC8"));
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _backend?.Dispose();
        _backend = null;
    }
}
