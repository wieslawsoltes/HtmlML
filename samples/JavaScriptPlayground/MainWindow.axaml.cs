using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using JavaScript.Avalonia;
using Jint.Native;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace JavaScriptPlayground;

public partial class MainWindow : Window
{
    private readonly List<Preset> _presets;
    private JintAvaloniaHost? _host;
    private readonly TextDocument _xamlDocument = new();
    private readonly TextDocument _scriptDocument = new();
    private readonly RegistryOptions _registryOptions = new(ThemeName.LightPlus);

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif

        ConfigureEditors();

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
        _xamlDocument.Text = preset.Xaml;
        _scriptDocument.Text = preset.Script;
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
            var xaml = _xamlDocument.Text ?? string.Empty;
            var loaded = AvaloniaRuntimeXamlLoader.Load(xaml);
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

    private void ConfigureEditors()
    {
        XamlEditor.Document = _xamlDocument;
        ScriptEditor.Document = _scriptDocument;

        var xamlTextMate = XamlEditor.InstallTextMate(_registryOptions);
        var scriptTextMate = ScriptEditor.InstallTextMate(_registryOptions);
        ApplyGrammar(xamlTextMate, ".xaml");
        ApplyGrammar(scriptTextMate, ".js");
    }

    private void ApplyGrammar(dynamic installation, string extension)
    {
        var language = _registryOptions.GetLanguageByExtension(extension);
        if (language is null)
        {
            return;
        }

        var scope = _registryOptions.GetScopeByLanguageId(language.Id);
        if (scope is not null)
        {
            installation.SetGrammar(scope);
        }
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
            var script = _scriptDocument.Text;
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
        Padding="20"
        Background="#f7f9fc"
        BorderBrush="#d9e2f1"
        BorderThickness="1"
        CornerRadius="8">
  <StackPanel Spacing="10">
    <TextBlock Name="title"
               Text="JavaScript.Avalonia"
               Foreground="#1f2937"
               FontSize="22"
               FontWeight="SemiBold" />
    <TextBlock Text="Play with DOM-style APIs directly inside Avalonia."
               Foreground="#475569" />
    <Button Name="mainButton"
            Content="Click me"
            HorizontalAlignment="Left"
            Padding="16,8"
            Background="#2563eb"
            Foreground="White"
            CornerRadius="4" />
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
                "Text nodes",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16">
  <StackPanel Spacing="8">
    <TextBlock Text="document.createTextNode demo" FontWeight="SemiBold" />
    <StackPanel Name="textContainer" Spacing="4" />
    <Button Name="updateText" Content="Update text node" HorizontalAlignment="Left" />
  </StackPanel>
</Border>
""",
                """
const container = document.getElementById('textContainer');
const update = document.getElementById('updateText');

const greeting = document.createTextNode('Hello from createTextNode');
const literals = document.createTextNode('<StackPanel /> stays literal & safe');

container.appendChild(greeting);
container.appendChild(literals);

update.addEventListener('click', () => {
  greeting.data = `Updated at ${new Date().toLocaleTimeString()}`;
  const history = document.createTextNode(`History entry ${container.childElementCount + 1}`);
  container.appendChild(history);
});
"""),
            new Preset(
                "Child manipulation",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16">
  <StackPanel Spacing="8">
    <TextBlock Text="appendChild / insertBefore / removeChild / replaceChild" FontWeight="SemiBold" />
    <StackPanel Name="list" Spacing="4">
      <TextBlock Text="Alpha" />
      <TextBlock Text="Charlie" />
    </StackPanel>
    <StackPanel Orientation="Horizontal" Spacing="8">
      <Button Name="insert" Content="Insert Bravo" />
      <Button Name="replace" Content="Replace Charlie" />
      <Button Name="remove" Content="Remove Alpha" />
      <Button Name="append" Content="Append Delta" />
    </StackPanel>
  </StackPanel>
</Border>
""",
                """
const list = document.getElementById('list');
const buttons = {
  insert: document.getElementById('insert'),
  replace: document.getElementById('replace'),
  remove: document.getElementById('remove'),
  append: document.getElementById('append')
};

const nodes = {
  alpha: list.children[0],
  charlie: list.children[1],
  bravo: document.createElement('TextBlock'),
  delta: document.createElement('TextBlock')
};

nodes.bravo.textContent = 'Bravo';
nodes.delta.textContent = 'Delta';

buttons.insert.addEventListener('click', () => {
  if (!nodes.bravo.parentElement) {
    list.insertBefore(nodes.bravo, nodes.charlie);
  }
});

buttons.replace.addEventListener('click', () => {
  const wrapper = document.createElement('Border');
  wrapper.setAttribute('Padding', '4');
  wrapper.setAttribute('Background', '#f5f5f5');
  const label = document.createElement('TextBlock');
  label.textContent = 'Charlie (wrapped)';
  wrapper.appendChild(label);
  list.replaceChild(wrapper, nodes.charlie);
  nodes.charlie = wrapper;
});

buttons.remove.addEventListener('click', () => {
  if (nodes.alpha && nodes.alpha.parentElement) {
    list.removeChild(nodes.alpha);
  }
});

buttons.append.addEventListener('click', () => {
  if (!nodes.delta.parentElement) {
    list.appendChild(nodes.delta);
  }
});
"""),
            new Preset(
                "Avalonia properties",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16">
  <StackPanel Spacing="12">
    <Border Name="sampleBorder"
            Width="200"
            Height="120"
            HorizontalAlignment="Left"
            CornerRadius="8"
            Background="#2b2d42">
      <TextBlock Name="sampleText"
                 Text="Click the buttons to update properties"
                 TextWrapping="Wrap"
                 Foreground="White"
                 HorizontalAlignment="Center"
                 VerticalAlignment="Center"
                 Margin="12" />
    </Border>
    <StackPanel Orientation="Horizontal" Spacing="8">
      <Button Name="brushBtn" Content="Set Accent Colors" />
      <Button Name="thicknessBtn" Content="Set Padding" />
      <Button Name="radiusBtn" Content="Set CornerRadius" />
      <Button Name="resetBtn" Content="Reset" />
    </StackPanel>
  </StackPanel>
</Border>
""",
                """
const border = document.getElementById('sampleBorder');
const text = document.getElementById('sampleText');
const brushBtn = document.getElementById('brushBtn');
const thicknessBtn = document.getElementById('thicknessBtn');
const radiusBtn = document.getElementById('radiusBtn');
const resetBtn = document.getElementById('resetBtn');

brushBtn.addEventListener('click', () => {
  border.setAttribute('background', '#ff7b7b');
  border.setAttribute('border-brush', '#feb47b');
  text.style.setProperty('foreground', '#1b1b1d');
});

thicknessBtn.addEventListener('click', () => {
  border.setAttribute('padding', '24,12');
  border.setAttribute('border-thickness', '2');
  border.setAttribute('border-brush', '#1b1b1d');
});

radiusBtn.addEventListener('click', () => {
  border.setAttribute('corner-radius', '0,24,0,24');
});

resetBtn.addEventListener('click', () => {
  border.setAttribute('background', '#2b2d42');
  border.setAttribute('padding', '0');
  border.setAttribute('border-thickness', '0');
  border.setAttribute('border-brush', 'Transparent');
  border.setAttribute('corner-radius', '8');
  text.style.setProperty('foreground', 'White');
});
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
                "Events & head",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16">
  <StackPanel Spacing="12">
    <TextBlock Text="Capture, passive, and custom events" FontWeight="SemiBold" />
    <Border Name="outer" Background="#f2f4ff" Padding="12">
      <Border Name="inner" Background="#d7e2ff" Padding="12">
        <StackPanel Spacing="8">
          <Button Name="trigger" Content="Dispatch custom event" HorizontalAlignment="Left" />
          <TextBlock Name="info" FontWeight="Bold" TextWrapping="Wrap" />
          <TextBlock Name="log" TextWrapping="Wrap" />
        </StackPanel>
      </Border>
    </Border>
  </StackPanel>
</Border>
""",
                """
const outer = document.getElementById('outer');
const inner = document.getElementById('inner');
const trigger = document.getElementById('trigger');
const info = document.getElementById('info');
const log = document.getElementById('log');

function write(message) {
  log.textContent += message + '\n';
}

document.title = 'JavaScript.Avalonia events';
const headEntry = document.createElement('TextBlock');
headEntry.textContent = 'Head updated at ' + new Date().toLocaleTimeString();
document.head.appendChild(headEntry);

const body = document.body;
const firstChildName = body.firstChild ? body.firstChild.nodeName : 'none';
info.textContent = `documentElement: ${document.documentElement.nodeName}, body first child: ${firstChildName}`;

document.addEventListener('pointerdown', evt => {
  write(`document capture → phase=${evt.eventPhase}`);
}, { capture: true });

document.addEventListener('pointerdown', evt => {
  write(`document bubble → phase=${evt.eventPhase}`);
});

outer.addEventListener('pointerdown', evt => {
  write(`outer capture → currentTarget=${evt.currentTarget.nodeName}`);
}, { capture: true });

outer.addEventListener('pointerdown', evt => {
  write(`outer bubble → defaultPrevented=${evt.defaultPrevented}`);
});

inner.addEventListener('pointerdown', evt => {
  write('inner passive listener (preventDefault ignored)');
  evt.preventDefault();
}, { passive: true });

inner.addEventListener('pointerdown', evt => {
  write(`inner bubble → pointerType=${evt.pointerType}, button=${evt.button}`);
});

inner.addEventListener('custom', evt => {
  write(`custom event detail=${evt.detail}`);
  evt.preventDefault();
});

trigger.addEventListener('click', () => {
  log.textContent = '';
  const synthetic = { type: 'custom', bubbles: true, detail: Date.now() };
  const result = inner.dispatchEvent(synthetic);
          write(`dispatchEvent returned ${result}, defaultPrevented=${synthetic.defaultPrevented}`);
});
"""),
            new Preset(
                "Custom events",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="20" Background="#f8fafc" CornerRadius="8">
  <StackPanel Spacing="12">
    <TextBlock Text="CustomEvent constructors" FontWeight="SemiBold" FontSize="18" Foreground="#1f2937" />
    <Border Background="#eef2ff" Padding="12" CornerRadius="6">
      <StackPanel Name="eventArea" Spacing="8">
        <TextBlock Text="Event log" FontWeight="SemiBold" />
        <Border Background="White" Padding="8" CornerRadius="4" BorderBrush="#c7d2fe" BorderThickness="1">
          <TextBlock Name="eventLog" TextWrapping="Wrap" Foreground="#1e293b" />
        </Border>
        <StackPanel Orientation="Horizontal" Spacing="8">
          <Button Name="fireCustom" Content="CustomEvent" Padding="14,8" />
          <Button Name="fireNative" Content="Event" Padding="14,8" />
          <Button Name="clearLog" Content="Clear" Padding="14,8" />
        </StackPanel>
      </StackPanel>
    </Border>
    <Border Name="syntheticNode" Background="#fde68a" Padding="10" CornerRadius="6" BorderBrush="#f59e0b" BorderThickness="1">
      <TextBlock Text="Synthetic path node" Foreground="#92400e" />
    </Border>
  </StackPanel>
</Border>
""",
                """
const area = document.getElementById('eventArea');
const logBlock = document.getElementById('eventLog');
const helper = document.getElementById('syntheticNode');
const fireCustom = document.getElementById('fireCustom');
const fireNative = document.getElementById('fireNative');
const clearLog = document.getElementById('clearLog');

const appendLog = message => {
  const stamp = new Date().toLocaleTimeString();
  const existing = logBlock.textContent ? `${logBlock.textContent}\n` : '';
  logBlock.textContent = `${existing}[${stamp}] ${message}`;
};

area.addEventListener('notify', evt => {
  appendLog(`capture → target: ${evt.target?.nodeName}, current: ${evt.currentTarget?.nodeName}`);
}, { capture: true });

area.addEventListener('notify', evt => {
  appendLog(`bubble → detail: ${JSON.stringify(evt.detail)}, from synthetic: ${evt.eventPhase === 3}`);
});

helper.addEventListener('notify', evt => {
  appendLog(`synthetic node observed phase=${evt.eventPhase}`);
}, { capture: true });

fireCustom.addEventListener('click', () => {
  const evt = new CustomEvent('notify', { detail: { count: logBlock.textContent.split('\n').filter(Boolean).length + 1 }, bubbles: true, path: [helper] });
  area.dispatchEvent(evt);
});

fireNative.addEventListener('click', () => {
  const evt = new Event('notify', { bubbles: true });
  area.dispatchEvent(evt);
});

clearLog.addEventListener('click', () => {
  logBlock.textContent = '';
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
                "Canvas 2D",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16" Background="#f8fafc" BorderBrush="#d4dbe5" BorderThickness="1" CornerRadius="8">
  <StackPanel Spacing="12">
    <TextBlock Text="CanvasRenderingContext2D demo" FontWeight="SemiBold" Foreground="#0f172a" />
    <Border Name="paintSurface" Width="520" Height="280" Background="#ffffff" BorderBrush="#e2e8f0" BorderThickness="1" CornerRadius="4" />
    <StackPanel Orientation="Horizontal" Spacing="8">
      <Button Name="drawCanvas" Content="Draw scene" />
      <Button Name="animateCanvas" Content="Animate" />
      <Button Name="clearCanvas" Content="Clear" />
    </StackPanel>
    <TextBlock Name="canvasStatus" Foreground="#334155" />
  </StackPanel>
</Border>
""",
                """
const surface = document.getElementById('paintSurface');
const ctx = surface.getContext('2d');
const drawBtn = document.getElementById('drawCanvas');
const animateBtn = document.getElementById('animateCanvas');
const clearBtn = document.getElementById('clearCanvas');
const status = document.getElementById('canvasStatus');

let animationHandle = 0;
let angle = 0;

function drawScene() {
  const width = surface.offsetWidth;
  const height = surface.offsetHeight;
  ctx.clearRect(0, 0, width, height);

  ctx.fillStyle = '#ffffff';
  ctx.fillRect(0, 0, width, height);

  ctx.fillStyle = '#2563eb';
  ctx.fillRect(28, 28, 160, 100);

  ctx.strokeStyle = '#f97316';
  ctx.lineWidth = 6;
  ctx.beginPath();
  ctx.moveTo(220, 44);
  ctx.lineTo(340, 140);
  ctx.lineTo(220, 184);
  ctx.closePath();
  ctx.stroke();

  ctx.fillStyle = '#10b981';
  ctx.beginPath();
  ctx.arc(400, 120, 48, 0, Math.PI * 2, false);
  ctx.fill();

  ctx.fillStyle = '#0f172a';
  ctx.font = '20px Segoe UI';
  ctx.fillText('CanvasRenderingContext2D from Avalonia', 28, height - 36);

  status.textContent = 'Scene rendered using the Avalonia drawing context.';
}

function animate() {
  const width = surface.offsetWidth;
  const height = surface.offsetHeight;
  ctx.clearRect(0, 0, width, height);

  ctx.fillStyle = '#f1f5f9';
  ctx.fillRect(0, 0, width, height);

  const cx = width / 2;
  const cy = height / 2;
  const radius = Math.min(width, height) / 4;

  ctx.strokeStyle = '#475569';
  ctx.lineWidth = 4;
  ctx.beginPath();
  ctx.arc(cx, cy, radius, 0, Math.PI * 2, false);
  ctx.stroke();

  const orbitX = cx + Math.cos(angle) * radius;
  const orbitY = cy + Math.sin(angle) * radius;

  ctx.fillStyle = '#f97316';
  ctx.beginPath();
  ctx.arc(orbitX, orbitY, 16, 0, Math.PI * 2, false);
  ctx.fill();

  angle += 0.1;
  animationHandle = window.requestAnimationFrame(animate);
}

drawBtn.addEventListener('click', () => {
  if (animationHandle) {
    window.cancelAnimationFrame(animationHandle);
    animationHandle = 0;
  }
  drawScene();
});

animateBtn.addEventListener('click', () => {
  if (animationHandle) {
    return;
  }
  status.textContent = 'Animating with requestAnimationFrame...';
  angle = 0;
  animationHandle = window.requestAnimationFrame(animate);
});

clearBtn.addEventListener('click', () => {
  if (animationHandle) {
    window.cancelAnimationFrame(animationHandle);
    animationHandle = 0;
  }
  ctx.clearRect(0, 0, surface.offsetWidth, surface.offsetHeight);
  status.textContent = 'Canvas cleared.';
});

drawScene();
"""
            ),
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
            new Preset(
                "External library",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16">
  <StackPanel Spacing="12">
    <TextBlock Name="status" FontSize="18" FontWeight="SemiBold" Text="Loading external library..." />
    <TextBlock Text="This preset fetches day.js from a CDN using require() and shows the formatted time." TextWrapping="Wrap" />
    <Button Name="refresh" Content="Refresh timestamp" HorizontalAlignment="Left" />
  </StackPanel>
</Border>
""",
                """
const status = document.getElementById('status');
const refresh = document.getElementById('refresh');

function updateTime() {
  try {
    const dayjs = require('https://cdn.jsdelivr.net/npm/dayjs@1/dayjs.min.js');
    status.textContent = `Loaded dayjs@${dayjs.version} – ${dayjs().format('YYYY-MM-DD HH:mm:ss')}`;
  } catch (err) {
    status.textContent = `Failed to load dayjs: ${err}`;
  }
}

refresh.addEventListener('click', () => updateTime());
updateTime();
"""),

            new Preset(
                "External library (relative time)",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16">
  <StackPanel Spacing="12">
    <TextBlock Name="statusAdvanced" FontSize="18" FontWeight="SemiBold" Text="Loading timeline..." />
    <TextBlock Text="Requires dayjs and its relativeTime plugin, loaded from jsDelivr." TextWrapping="Wrap" />
    <Button Name="refreshAdvanced" Content="Refresh timeline" HorizontalAlignment="Left" />
    <StackPanel Name="eventList" Spacing="4" />
  </StackPanel>
</Border>
""",
                """
const status = document.getElementById('statusAdvanced');
const refresh = document.getElementById('refreshAdvanced');
const eventList = document.getElementById('eventList');

function updateTimeline() {
  try {
    const dayjs = require('https://cdn.jsdelivr.net/npm/dayjs@1/dayjs.min.js');
    const relativeTime = require('https://cdn.jsdelivr.net/npm/dayjs@1/plugin/relativeTime.js');
    dayjs.extend(relativeTime);

    const events = [
      { label: 'Kick-off', offset: -3 },
      { label: 'Design freeze', offset: -1 },
      { label: 'Beta release', offset: 4 },
      { label: 'Launch', offset: 12 }
    ];

    for (const child of eventList.children) {
      child.remove();
    }

    const now = dayjs();
    for (const evt of events) {
      const block = document.createElement('TextBlock');
      block.textContent = `${evt.label}: ${now.add(evt.offset, 'day').fromNow()}`;
      eventList.appendChild(block);
    }

    status.textContent = `Timeline refreshed at ${now.format('YYYY-MM-DD HH:mm:ss')}`;
  } catch (error) {
    status.textContent = `Failed to load timeline data: ${error}`;
  }
}

refresh.addEventListener('click', () => updateTimeline());
updateTimeline();
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
