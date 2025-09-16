using Avalonia.Controls;
using Xunit;

namespace JavaScript.Avalonia.Tests;

internal static class HostTestUtilities
{
    public static (JintAvaloniaHost Host, Window Window) CreateHost(Control? root = null)
    {
        var window = new Window
        {
            Content = root ?? new StackPanel()
        };

        return (new JintAvaloniaHost(window), window);
    }

    public static AvaloniaDomElement GetElement(object? value)
    {
        return Assert.IsType<AvaloniaDomElement>(value);
    }
}
