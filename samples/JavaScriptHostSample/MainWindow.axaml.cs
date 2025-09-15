using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using JavaScript.Avalonia;

namespace JavaScriptHostSample;

public partial class MainWindow : Window
{
    private readonly JintAvaloniaHost _jsHost;

    public MainWindow()
    {
        InitializeComponent();
        _jsHost = new JintAvaloniaHost(this);
        var runButton = this.FindControl<Button>("RunButton");
        if (runButton is not null)
        {
            runButton.Click += OnRunButtonClick;
        }
    }

    private void OnRunButtonClick(object? sender, RoutedEventArgs e)
    {
        RunSampleScript();
    }

    private void RunSampleScript()
    {
        _jsHost.ExecuteScriptText("""
const output = document.getElementById('OutputText');
if (output) {
  output.textContent = 'Running script...';
}
window.setTimeout(() => {
  if (output) {
    output.textContent = 'setTimeout executed after 200ms';
  }
}, 200);
window.requestAnimationFrame(() => {
  const existing = document.getElementById('DynamicMessage');
  if (existing && typeof existing.remove === 'function') {
    existing.remove();
  }
  const message = document.createElement('TextBlock');
  if (message && typeof message.setAttribute === 'function') {
    message.setAttribute('id', 'DynamicMessage');
    message.textContent = 'requestAnimationFrame triggered.';
    if (document.body && typeof document.body.appendChild === 'function') {
      document.body.appendChild(message);
    }
  }
  if (output) {
    output.textContent = 'requestAnimationFrame triggered.';
  }
});
""");
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
