using Avalonia;
using Avalonia.Headless;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(JavaScript.Avalonia.Tests.TestApp))]

namespace JavaScript.Avalonia.Tests;

public class TestApp : Application
{
}
