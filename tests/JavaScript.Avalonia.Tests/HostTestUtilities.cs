using Avalonia.Controls;
using Xunit;

namespace JavaScript.Avalonia.Tests;

internal static class HostTestUtilities
{
    public static (AvaloniaBrowserHost Host, Window Window) CreateHost(Control? root = null)
    {
        var window = new Window
        {
            Content = root ?? new StackPanel()
        };

        return (new AvaloniaBrowserHost(window), window);
    }

    public static AvaloniaDomElement GetElement(object? value)
    {
        return Assert.IsType<AvaloniaDomElement>(value);
    }
}
