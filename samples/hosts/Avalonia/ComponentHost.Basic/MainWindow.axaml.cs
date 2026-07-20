using Avalonia.Controls;

namespace HtmlML.Samples.ComponentHost;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PrimaryHost.DiagnosticReported += (_, diagnostic) => StatusText.Text = $"{diagnostic.Code}: {diagnostic.Message}";
        Opened += (_, _) => MountComponent();
        Closed += (_, _) => PrimaryHost.Dispose();
    }

    private void MountComponent()
    {
        try
        {
            PrimaryHost.MountComponent();
            StatusText.Text = "Mounted dev.htmlml.componenthost-basic";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Mount failed: {exception.Message}";
        }
    }
}
