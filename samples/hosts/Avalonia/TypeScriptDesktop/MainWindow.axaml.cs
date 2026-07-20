using System.Text.Json;
using Avalonia.Controls;
using HtmlML.Sdk;
using HtmlML.Sdk.Avalonia;

namespace HtmlML.Samples.TypeScriptDesktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PrimaryHost.RegisterHostCapability(CreateHandler(HtmlMlComponentCapabilities.Settings));
        PrimaryHost.RegisterHostCapability(CreateHandler(HtmlMlComponentCapabilities.Notifications));
        Opened += (_, _) => MountComponent();
        Closed += (_, _) => PrimaryHost.Dispose();
    }

    private void MountComponent()
    {
        try
        {
            PrimaryHost.MountComponent();
            StatusText.Text = "Mounted dev.htmlml.typescriptdesktop";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Mount failed: {exception.Message}";
        }
    }

    private static HtmlMlDelegateCapabilityHandler CreateHandler(string capability)
        => new(capability, (method, arguments, _) =>
            ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { accepted = true, capability, method, arguments })));
}
