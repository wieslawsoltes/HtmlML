using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using JavaScript.Avalonia;
using Jint.Native;
using Avalonia.Interactivity;

namespace JavaScriptPlayground;

public partial class MainWindow : Window
{
    private readonly List<Preset> _presets;
    private JintAvaloniaHost? _host;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif

        _presets = CreatePresets();
        PresetCombo.ItemsSource = _presets;
        PresetCombo.SelectionChanged += PresetComboOnSelectionChanged;
        PresetCombo.SelectedIndex = _presets.Count > 0 ? 0 : -1;
        AutoRunCheckBox.IsChecked = true;

        if (_presets.Count > 0)
        {
            ApplyPreset(_presets[0]);
        }
        else
        {
            ResetHost();
        }
    }

    private void PresetComboOnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PresetCombo.SelectedItem is Preset preset)
        {
            ApplyPreset(preset);
        }
    }

    private void ApplyPreset(Preset preset)
    {
        XamlEditor.Text = preset.Xaml;
        ScriptEditor.Text = preset.Script;
        LoadXaml(AutoRunCheckBox.IsChecked == true);
    }

    private void OnApplyPresetClick(object? sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is Preset preset)
        {
            ApplyPreset(preset);
        }
    }

    private void OnReloadXamlClick(object? sender, RoutedEventArgs e)
    {
        LoadXaml(AutoRunCheckBox.IsChecked == true);
    }

    private void OnRunScriptClick(object? sender, RoutedEventArgs e)
    {
        RunScript(resetHost: true);
    }

    private void LoadXaml(bool runScript)
    {
        try
        {
            var loaded = AvaloniaRuntimeXamlLoader.Load(XamlEditor.Text);
            if (loaded is not Control control)
            {
                throw new InvalidOperationException("Root element must derive from Control.");
            }

            PreviewHost.Child = control;
            SetStatus("XAML loaded", false);
            ResetHost();
            if (runScript)
            {
                RunScript(resetHost: false);
            }
        }
        catch (Exception ex)
        {
            PreviewHost.Child = null;
            SetStatus($"XAML error: {ex.Message}", true);
        }
    }

    private void ResetHost()
    {
        _host = new JintAvaloniaHost(this, host => new PlaygroundDomDocument(host, this));
    }

    private void RunScript(bool resetHost)
    {
        if (resetHost || _host is null)
        {
            ResetHost();
        }

        if (_host is null)
        {
            SetStatus("Host not ready", true);
            return;
        }

        try
        {
            var script = ScriptEditor.Text;
            if (!string.IsNullOrWhiteSpace(script))
            {
                _host.ExecuteScriptText(script);
            }
            SetStatus("Script executed", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Script error: {ex.Message}", true);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? Brushes.DarkRed : Brushes.DarkGreen;
    }

    private static List<Preset> CreatePresets()
    {
        return new List<Preset>
        {
            new Preset(
                "Welcome Panel",
                """
<Border xmlns="https://github.com/avaloniaui"
        Padding="16"
        Background="#20232a">
  <StackPanel Spacing="8">
    <TextBlock Name="title" Text="JavaScript.Avalonia" Foreground="White" FontSize="22" FontWeight="SemiBold" />
    <TextBlock Text="Play with DOM-style APIs directly inside Avalonia." Foreground="#dddddd" />
    <Button Name="mainButton" Content="Click me" HorizontalAlignment="Left" />
  </StackPanel>
</Border>
""",
                """
const header = document.getElementById('title');
const button = document.getElementById('mainButton');

button.addEventListener('click', () => {
  const stamp = new Date().toLocaleTimeString();
  header.textContent = `Button clicked at ${stamp}`;
});

header.textContent = 'JavaScript.Avalonia – Playground';
"""),
            new Preset(
                "ClassList & Dataset",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16">
  <Border Name="panel" Padding="16" Background="#FFEFD5">
    <Border.Styles>
      <Style Selector="Border.highlight">
        <Setter Property="Background" Value="#FFF2B6" />
        <Setter Property="BorderBrush" Value="#D88400" />
        <Setter Property="BorderThickness" Value="2" />
      </Style>
    </Border.Styles>
    <StackPanel Spacing="8">
      <TextBlock Name="status" Text="Highlight is off" />
      <Button Name="toggle" Content="Toggle highlight" HorizontalAlignment="Left" />
    </StackPanel>
  </Border>
</Border>
""",
                """
const panel = document.getElementById('panel');
panel.dataset.info = 'sample-panel';

const toggle = document.getElementById('toggle');
const status = document.getElementById('status');

toggle.addEventListener('click', () => {
  const active = panel.classList.toggle('highlight');
  status.textContent = active ? 'Highlight is on' : 'Highlight is off';
  panel.setAttribute('title', `dataset: ${panel.dataset.info}`);
});
"""),
            new Preset(
                "Pointer tracking",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="12">
  <StackPanel Spacing="12">
    <Border Name="surface"
            Height="200"
            Background="#1f6feb"
            CornerRadius="6"
            ToolTip.Tip="Move the pointer here">
      <TextBlock Name="coords"
                 HorizontalAlignment="Center"
                 VerticalAlignment="Center"
                 FontSize="18"
                 Foreground="White"
                 Text="Move pointer" />
    </Border>
    <TextBlock Name="log" Text="" TextWrapping="Wrap" />
  </StackPanel>
</Border>
""",
                """
const surface = document.getElementById('surface');
const coords = document.getElementById('coords');
const log = document.getElementById('log');

surface.addEventListener('pointermove', info => {
  coords.textContent = `x: ${info.x.toFixed(0)}, y: ${info.y.toFixed(0)}`;
});

surface.addEventListener('pointerdown', info => {
  log.textContent = `Pointer button: ${info.button}`;
  info.stopPropagation();
});
"""),
            new Preset(
                "Timers & animation",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16" Background="#101820">
  <StackPanel Spacing="12">
    <ProgressBar Name="progress" Minimum="0" Maximum="100" Height="8" />
    <TextBlock Name="timerLabel" Foreground="#f0f0f0" />
  </StackPanel>
</Border>
""",
                """
const bar = document.getElementById('progress');
const label = document.getElementById('timerLabel');
let value = 0;

function tick() {
  value = (value + 1) % 101;
  bar.setAttribute('value', value);
  label.textContent = `requestAnimationFrame progress: ${value}%`;
  window.requestAnimationFrame(tick);
}

tick();

let counter = 0;
const handle = window.setInterval(() => {
  counter++;
  console.info(`Interval fired ${counter} times`);
  if (counter >= 5) {
    window.clearInterval(handle);
    label.textContent += ' | interval stopped';
  }
}, 500);
"""),
            new Preset(
                "Ready state & query",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16">
  <StackPanel Spacing="6">
    <TextBlock Name="state" FontWeight="Bold" />
    <ItemsControl Name="list">
      <ItemsControl.Items>
        <TextBlock Text="One" />
        <TextBlock Text="Two" />
        <TextBlock Text="Three" />
      </ItemsControl.Items>
    </ItemsControl>
  </StackPanel>
</Border>
""",
                """
const stateLabel = document.getElementById('state');
stateLabel.textContent = `readyState: ${document.readyState}`;

document.addEventListener('DOMContentLoaded', () => {
  stateLabel.textContent = 'DOMContentLoaded fired';
});

const items = document.querySelectorAll('TextBlock');
console.table(items.map(i => i.textContent));
"""),
        };
    }

    private sealed class Preset
    {
        public Preset(string name, string xaml, string script)
        {
            Name = name;
            Xaml = xaml;
            Script = script;
        }

        public string Name { get; }
        public string Xaml { get; }
        public string Script { get; }
    }

    private sealed class PlaygroundDomDocument : AvaloniaDomDocument
    {
        private readonly MainWindow _window;

        public PlaygroundDomDocument(JintAvaloniaHost host, MainWindow window)
            : base(host)
        {
            _window = window;
        }

        protected override Control? GetDocumentRoot()
            => _window.PreviewHost.Child as Control;
    }
}
