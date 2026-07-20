using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HtmlML.Sdk;
using HtmlML.Sdk.Avalonia;

namespace HtmlML.Samples.Hybrid;

public sealed partial class MainWindow : Window
{
    private int _nativeCommandCount;

    public MainWindow()
    {
        InitializeComponent();
        Configure(PrimaryHost);
        Configure(SecondaryHost);
        Opened += (_, _) => MountComponents();
        Closed += (_, _) =>
        {
            PrimaryHost.Dispose();
            SecondaryHost.Dispose();
        };
    }

    private void MountComponents()
    {
        try
        {
            PrimaryHost.MountComponent();
            SecondaryHost.MountComponent();
            StatusText.Text = "Mounted two isolated instances of dev.htmlml.hybrid-reactislands";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Mount failed: {exception.Message}";
        }
    }

    private void OnNativeCommand(object? sender, RoutedEventArgs e)
        => CommandText.Text = $"Native command #{++_nativeCommandCount}";

    private static void Configure(HtmlMlComponentHost host)
    {
        host.RegisterHostCapability(CreateHandler(HtmlMlComponentCapabilities.Commands));
        host.RegisterHostCapability(CreateHandler(HtmlMlComponentCapabilities.Settings));
    }

    private static HtmlMlDelegateCapabilityHandler CreateHandler(string capability)
        => new(capability, (method, arguments, _) =>
            ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { accepted = true, capability, method, arguments })));
}
