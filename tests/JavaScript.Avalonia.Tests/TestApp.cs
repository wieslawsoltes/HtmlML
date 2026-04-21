using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(JavaScript.Avalonia.Tests.TestApp))]

namespace JavaScript.Avalonia.Tests;

public class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            });
}
