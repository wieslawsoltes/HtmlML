using HtmlML.Core;
using JavaScript.Avalonia;

var hostType = typeof(AvaloniaBrowserHost);
if (!string.Equals(hostType.Assembly.GetName().Name, "HtmlML.Backend.Avalonia", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Backend package smoke: implementation assembly was '{hostType.Assembly.GetName().Name}'.");
    return 1;
}
if (hostType.GetProperty(nameof(AvaloniaBrowserHost.Backend))?.PropertyType != typeof(IHtmlMlBackendHost))
{
    Console.Error.WriteLine("Backend package smoke: AvaloniaBrowserHost does not expose IHtmlMlBackendHost.");
    return 1;
}

Console.WriteLine(
    $"Backend package smoke: pass; host={hostType.Assembly.GetName().Name}, " +
    $"contract={typeof(IHtmlMlBackendHost).Assembly.GetName().Name}");
return 0;
