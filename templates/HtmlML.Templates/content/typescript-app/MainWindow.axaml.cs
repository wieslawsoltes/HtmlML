using System.Text.Json;
using Avalonia.Controls;
using HtmlML.Sdk;
using HtmlML.Sdk.Avalonia;

namespace HtmlMLTypeScriptApp;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => MountComponents();
    }

    private void MountComponents()
    {
        Configure(PrimaryHost);
        PrimaryHost.MountComponent();
    }

    private static void Configure(HtmlMlComponentHost host)
    {
        foreach (var capability in new[] { HtmlMlComponentCapabilities.Commands, HtmlMlComponentCapabilities.Settings, HtmlMlComponentCapabilities.Notifications })
        {
            if (!File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Component", "htmlml-component.json")).Contains($"\\\"{capability}\\\"", StringComparison.Ordinal)) continue;
            host.RegisterHostCapability(new HtmlMlDelegateCapabilityHandler(capability, (_, arguments, _) =>
                ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { accepted = true, arguments }))));
        }
    }
}
