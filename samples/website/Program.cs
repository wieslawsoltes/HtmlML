using Avalonia;
using System;

namespace wwwroot;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<main>()
            .UsePlatformDetect()
            .LogToTrace();
}
