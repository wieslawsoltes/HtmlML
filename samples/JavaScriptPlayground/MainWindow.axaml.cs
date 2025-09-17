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
                "Layout inspector",
                """
<StackPanel xmlns="https://github.com/avaloniaui"
            Margin="20"
            Spacing="12">
  <Border Name="card"
          Width="220"
          Padding="12"
          Background="#FFEEF2FF"
          BorderBrush="#FF537FE7"
          BorderThickness="2"
          CornerRadius="6">
    <StackPanel Spacing="6">
      <TextBlock Text="Layout &amp; Style Metrics" FontWeight="SemiBold" />
      <ScrollViewer Name="sampleScroll"
                    Height="90"
                    HorizontalScrollBarVisibility="Disabled">
        <StackPanel Name="scrollItems" Spacing="4">
          <TextBlock Text="Item 1" />
          <TextBlock Text="Item 2" />
          <TextBlock Text="Item 3" />
          <TextBlock Text="Item 4" />
        </StackPanel>
      </ScrollViewer>
    </StackPanel>
  </Border>
  <StackPanel Orientation="Horizontal" Spacing="8">
    <Button Name="addItem" Content="Add Item" />
    <Button Name="toggleAccent" Content="Toggle Accent" />
  </StackPanel>
  <TextBlock Name="output"
             Text="Metrics pending..."
             TextWrapping="Wrap"
             FontFamily="Consolas, Courier New, monospace" />
</StackPanel>
""",
                """
const card = document.getElementById('card');
const scroll = document.getElementById('sampleScroll');
const output = document.getElementById('output');
const list = document.getElementById('scrollItems');
const addItemButton = document.getElementById('addItem');
const toggleAccentButton = document.getElementById('toggleAccent');

for (let i = 5; i <= 12; i++) {
  const item = document.createElement('TextBlock');
  item.textContent = `Item ${i}`;
  list.appendChild(item);
}

scroll.scrollTop = 30;

const log = [];

function computeMetrics() {
  const style = window.getComputedStyle(card);
  return [
    `offset: (${Math.round(card.offsetLeft)}, ${Math.round(card.offsetTop)})`,
    `client: ${Math.round(card.clientWidth)} x ${Math.round(card.clientHeight)}`,
    `padding: ${style.getPropertyValue('padding')}`,
    `background: ${style.getPropertyValue('background-color')}`,
    `scroll viewport: ${Math.round(scroll.clientWidth)} x ${Math.round(scroll.clientHeight)}`,
    `scroll extent: ${Math.round(scroll.scrollWidth)} x ${Math.round(scroll.scrollHeight)}`,
    `scroll position: ${Math.round(scroll.scrollLeft)}, ${Math.round(scroll.scrollTop)}`
  ];
}

function render() {
  const metrics = computeMetrics();
  const history = log.slice(-5);
  output.textContent = metrics.concat(history).join('\n');
}

const observer = new MutationObserver(records => {
  records.forEach(r => {
    if (r.type === 'childList') {
      log.push(`childList ➜ +${r.addedNodes.length} / -${r.removedNodes.length}`);
    } else if (r.type === 'attributes') {
      log.push(`attribute ➜ ${r.attributeName} (old: ${r.oldValue ?? 'null'})`);
    }
  });
  render();
});

observer.observe(card, { attributes: true, attributeOldValue: true });
observer.observe(list, { childList: true });

addItemButton.addEventListener('click', () => {
  const item = document.createElement('TextBlock');
  item.textContent = `Item ${list.childNodes.length + 1}`;
  list.appendChild(item);
  render();
});

let accent = false;
toggleAccentButton.addEventListener('click', () => {
  accent = !accent;
  card.setAttribute('background', accent ? '#FFF4F0FF' : '#FFEEF2FF');
  card.setAttribute('border-brush', accent ? '#FF414BB2' : '#FF537FE7');
  render();
});

render();
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
                "Canvas 2D + bezier.js",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16" Background="#ffffff" BorderBrush="#d1d5db" BorderThickness="1" CornerRadius="8">
  <StackPanel Spacing="12">
    <TextBlock Text="Canvas 2D with bezier-js" FontWeight="SemiBold" Foreground="#1f2937" />
    <Border Name="librarySurface" Width="540" Height="300" Background="#f8fafc" BorderBrush="#e2e8f0" BorderThickness="1" CornerRadius="4" />
    <StackPanel Orientation="Horizontal" Spacing="8">
      <Button Name="libraryDraw" Content="Render curve" />
      <Button Name="libraryRandom" Content="Randomise control points" />
    </StackPanel>
    <TextBlock Name="libraryStatus" Foreground="#475569" />
  </StackPanel>
</Border>
""",
                """
const surface = document.getElementById('librarySurface');
const ctx = surface.getContext('2d');
const drawBtn = document.getElementById('libraryDraw');
const randomBtn = document.getElementById('libraryRandom');
const status = document.getElementById('libraryStatus');

let Bezier;
try {
  const bezierModule = require('https://cdn.jsdelivr.net/npm/bezier-js@6.1.3/dist/bezier.cjs');
  Bezier = bezierModule?.Bezier ?? bezierModule?.default ?? bezierModule;
  if (typeof Bezier !== 'function') {
    throw new Error('bezier-js module did not expose a constructor');
  }
} catch (error) {
  const message = `Failed to load bezier-js: ${error}`;
  if (status) {
    status.textContent = message;
  }
  console.error(message);
  throw error;
}

function createRandomCurve() {
  const w = surface.offsetWidth;
  const h = surface.offsetHeight;
  const margin = 32;
  return new Bezier(
    margin,
    h - margin,
    w * 0.25 + Math.random() * w * 0.2,
    margin + Math.random() * (h - 2 * margin),
    w * 0.6 + Math.random() * w * 0.2,
    margin + Math.random() * (h - 2 * margin),
    w - margin,
    margin
  );
}

let curve = createRandomCurve();

function renderCurve() {
  const w = surface.offsetWidth;
  const h = surface.offsetHeight;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = '#f8fafc';
  ctx.fillRect(0, 0, w, h);

  ctx.lineWidth = 3;
  ctx.strokeStyle = '#1f2937';
  ctx.beginPath();
  const primary = curve.getLUT(120);
  ctx.moveTo(primary[0].x, primary[0].y);
  for (let i = 1; i < primary.length; i++) {
    ctx.lineTo(primary[i].x, primary[i].y);
  }
  ctx.stroke();

  ctx.lineWidth = 2;
  ctx.strokeStyle = '#3b82f6';
  const offsets = curve.offset(18);
  offsets.forEach(offsetCurve => {
    const points = offsetCurve.getLUT(80);
    ctx.beginPath();
    ctx.moveTo(points[0].x, points[0].y);
    for (let i = 1; i < points.length; i++) {
      ctx.lineTo(points[i].x, points[i].y);
    }
    ctx.stroke();
  });

  ctx.lineWidth = 1;
  ctx.strokeStyle = '#94a3b8';
  ctx.beginPath();
  ctx.moveTo(curve.points[0].x, curve.points[0].y);
  for (let i = 1; i < curve.points.length; i++) {
    ctx.lineTo(curve.points[i].x, curve.points[i].y);
  }
  ctx.stroke();

  ctx.fillStyle = '#ef4444';
  curve.points.forEach(pt => {
    ctx.beginPath();
    ctx.arc(pt.x, pt.y, 4, 0, Math.PI * 2, false);
    ctx.fill();
  });
}

function renderAndReport(message) {
  renderCurve();
  if (status) {
    status.textContent = message ?? 'Curves rendered with bezier-js';
  }
}

const interactionRadius = 16;
let activePointIndex = -1;

const getPointerPosition = evt => ({
  x: evt?.x ?? 0,
  y: evt?.y ?? 0
});

const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

const distanceSquared = (a, b) => {
  const dx = a.x - b.x;
  const dy = a.y - b.y;
  return dx * dx + dy * dy;
};

function findClosestPoint(position, radius = interactionRadius) {
  let index = -1;
  let minDist = radius * radius;
  curve.points.forEach((pt, i) => {
    const dist = distanceSquared(position, pt);
    if (dist <= minDist) {
      minDist = dist;
      index = i;
    }
  });
  return index;
}

function updateActivePoint(position, message) {
  if (activePointIndex === -1) {
    return;
  }

  const pt = curve.points[activePointIndex];
  pt.x = clamp(position.x, 0, surface.offsetWidth);
  pt.y = clamp(position.y, 0, surface.offsetHeight);
  curve.update();
  renderAndReport(message);
}

function handlePointerDown(evt) {
  const position = getPointerPosition(evt);
  const index = findClosestPoint(position);
  if (index === -1) {
    return;
  }

  evt.preventDefault?.();
  activePointIndex = index;
  updateActivePoint(position, 'Dragging control point');
  surface.setPointerCapture?.(evt.pointerId);
}

function handlePointerMove(evt) {
  if (activePointIndex === -1) {
    return;
  }

  evt.preventDefault?.();
  updateActivePoint(getPointerPosition(evt), 'Dragging control point');
}

function handlePointerUp(evt) {
  if (activePointIndex === -1) {
    return;
  }

  activePointIndex = -1;
  surface.releasePointerCapture?.(evt?.pointerId);
  renderAndReport('Control point updated');
}

surface.addEventListener('pointerdown', handlePointerDown);
surface.addEventListener('pointermove', handlePointerMove);
surface.addEventListener('pointerup', handlePointerUp);
surface.addEventListener('pointerleave', handlePointerUp);

drawBtn.addEventListener('click', () => renderAndReport('Bezier curve redrawn'));

randomBtn.addEventListener('click', () => {
  activePointIndex = -1;
  curve = createRandomCurve();
  renderAndReport('Generated new curve — drag the red points to edit');
});

renderAndReport('Initial curve generated with bezier-js. Drag the red points to edit.');

"""
            ),
            new Preset(
                "Canvas 2D + rough.js",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16" Background="#ffffff" BorderBrush="#d1d5db" BorderThickness="1" CornerRadius="8">
  <StackPanel Spacing="12">
    <TextBlock Text="Canvas 2D with rough.js" FontWeight="SemiBold" Foreground="#111827" />
    <Border Name="roughSurface" Width="540" Height="300" Background="#f8fafc" BorderBrush="#e2e8f0" BorderThickness="1" CornerRadius="4" />
    <StackPanel Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
      <TextBlock Text="Roughness:" VerticalAlignment="Center" />
      <Slider Name="roughnessSlider" Minimum="0.5" Maximum="3.0" Value="1.0" Width="160" />
      <Button Name="roughRender" Content="Render scene" />
      <Button Name="roughPalette" Content="New palette" />
    </StackPanel>
    <TextBlock Name="roughStatus" Foreground="#475569" />
  </StackPanel>
</Border>
""",
                """
const surface = document.getElementById('roughSurface');
const ctx = surface.getContext('2d');
const slider = document.getElementById('roughnessSlider');
const renderBtn = document.getElementById('roughRender');
const paletteBtn = document.getElementById('roughPalette');
const status = document.getElementById('roughStatus');

let roughModule;
try {
  const mod = require('https://cdn.jsdelivr.net/npm/roughjs@4.6.6/bundled/rough.cjs.js');
  roughModule = mod?.canvas ? mod : (mod?.default?.canvas ? mod.default : mod);
  if (typeof roughModule?.canvas !== 'function') {
    throw new Error('rough.js canvas factory not found');
  }
} catch (error) {
  const message = `Failed to load rough.js: ${error}`;
  if (status) {
    status.textContent = message;
  }
  console.error(message);
  throw error;
}

const palettes = [
  ['#fde68a', '#f97316', '#1f2937'],
  ['#fbcfe8', '#ec4899', '#312e81'],
  ['#bfdbfe', '#1d4ed8', '#0f172a'],
  ['#dcfce7', '#16a34a', '#064e3b'],
  ['#fee2e2', '#ef4444', '#7f1d1d']
];
let paletteIndex = 0;

const getRoughness = () => {
  if (!slider) {
    return 1;
  }

  const raw = slider.Value ?? slider.value ?? slider.getAttribute?.('value') ?? slider.getAttribute?.('Value');
  const numeric = typeof raw === 'number' ? raw : parseFloat(raw ?? '1');
  return Number.isFinite(numeric) ? numeric : 1;
};

const pickPalette = () => {
  paletteIndex = Math.floor(Math.random() * palettes.length);
  return palettes[paletteIndex];
};

let currentPalette = pickPalette();

function renderScene(message) {
  const w = surface.offsetWidth;
  const h = surface.offsetHeight;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = '#f8fafc';
  ctx.fillRect(0, 0, w, h);

  const rough = roughModule.canvas(surface);
  const roughness = getRoughness();
  const [fill1, stroke1, accent] = currentPalette;

  rough.rectangle(36, 28, 180, 120, {
    roughness,
    fill: fill1,
    stroke: stroke1,
    fillStyle: 'hachure',
    strokeWidth: 2
  });

  rough.circle(320, 110, 120, {
    roughness,
    fill: accent,
    fillStyle: 'zigzag',
    stroke: stroke1,
    strokeWidth: 2
  });

  rough.linearPath([
    [60, 220],
    [160, 260],
    [260, 200],
    [360, 260],
    [460, 200]
  ], {
    roughness: roughness * 0.8,
    stroke: stroke1,
    bowing: 1.2,
    strokeWidth: 3
  });

  rough.rectangle(280, 180, 180, 90, {
    roughness: roughness * 1.1,
    fill: fill1,
    fillStyle: 'cross-hatch',
    stroke: accent,
    strokeWidth: 2
  });

  if (status) {
    const text = message ?? `Rendered with roughness ${roughness.toFixed(2)}`;
    status.textContent = text;
  }
}

const refreshFromSlider = () => {
  renderScene(`Rendered with roughness ${getRoughness().toFixed(2)}`);
};

renderBtn.addEventListener('click', () => renderScene('Manual render using rough.js'));

paletteBtn.addEventListener('click', () => {
  currentPalette = pickPalette();
  renderScene('Random palette applied');
});

slider.addEventListener('pointerup', refreshFromSlider);
slider.addEventListener('pointermove', evt => {
  if (evt.buttons) {
    refreshFromSlider();
  }
});

slider.addEventListener('keydown', refreshFromSlider);
slider.addEventListener('wheel', evt => {
  evt.preventDefault?.();
  refreshFromSlider();
}, { passive: false });

slider.addEventListener('valuechanged', refreshFromSlider);
slider.addEventListener('input', refreshFromSlider);

renderScene('Sketch scene rendered with rough.js — adjust the slider or change palette');
"""
            ),
            new Preset(
                "Canvas 2D + Chart.js",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16" Background="#f8fafc" BorderBrush="#d1d5db" BorderThickness="1" CornerRadius="8">
  <StackPanel Spacing="12">
    <TextBlock Text="Canvas 2D with Chart.js" FontWeight="SemiBold" Foreground="#1f2937" />
    <Border Name="chartSurface" Width="540" Height="300" Background="#ffffff" BorderBrush="#e2e8f0" BorderThickness="1" CornerRadius="4" />
    <StackPanel Orientation="Horizontal" Spacing="8">
      <Button Name="chartRandomize" Content="Randomise data" />
      <Button Name="chartToggle" Content="Toggle line/bar" />
    </StackPanel>
    <TextBlock Name="chartStatus" Foreground="#475569" TextWrapping="Wrap" />
  </StackPanel>
</Border>
""",
                """
const surface = document.getElementById('chartSurface');
const randomBtn = document.getElementById('chartRandomize');
const toggleBtn = document.getElementById('chartToggle');
const status = document.getElementById('chartStatus');

if (!surface) {
  throw new Error('chartSurface element not found');
}

const context = surface.getContext('2d');
if (!context) {
  throw new Error('CanvasRenderingContext2D unavailable for Chart.js demo');
}

let logicalWidth = surface.offsetWidth;
let logicalHeight = surface.offsetHeight;
const updateLogicalSize = (width, height) => {
  if (typeof width === 'number' && !Number.isNaN(width)) {
    logicalWidth = width;
  }
  if (typeof height === 'number' && !Number.isNaN(height)) {
    logicalHeight = height;
  }
};

const canvasElement = {
  nodeName: 'CANVAS',
  style: surface.style ?? {},
  ownerDocument: surface.ownerDocument ?? document,
  defaultView: typeof window !== 'undefined' ? window : undefined,
  getContext: () => context,
  addEventListener: () => {},
  removeEventListener: () => {},
  dispatchEvent: () => false,
  getBoundingClientRect: () => surface.getBoundingClientRect?.() ?? {
    width: surface.offsetWidth,
    height: surface.offsetHeight,
    top: 0,
    left: 0,
    right: surface.offsetWidth,
    bottom: surface.offsetHeight
  }
};

Object.defineProperties(canvasElement, {
  width: {
    configurable: true,
    enumerable: true,
    get: () => logicalWidth,
    set: value => updateLogicalSize(value, logicalHeight)
  },
  height: {
    configurable: true,
    enumerable: true,
    get: () => logicalHeight,
    set: value => updateLogicalSize(logicalWidth, value)
  },
  clientWidth: {
    configurable: true,
    enumerable: true,
    get: () => surface.offsetWidth
  },
  clientHeight: {
    configurable: true,
    enumerable: true,
    get: () => surface.offsetHeight
  },
  offsetWidth: {
    configurable: true,
    enumerable: true,
    get: () => surface.offsetWidth
  },
  offsetHeight: {
    configurable: true,
    enumerable: true,
    get: () => surface.offsetHeight
  }
});

if (canvasElement.ownerDocument && typeof canvasElement.ownerDocument.defaultView === 'undefined' && typeof window !== 'undefined') {
  canvasElement.ownerDocument.defaultView = window;
}

context.canvas = canvasElement;

let chartModule;
try {
  chartModule = require('https://cdn.jsdelivr.net/npm/chart.js@4.4.3/dist/chart.umd.js');
} catch (error) {
  const message = `Failed to load Chart.js: ${error}`;
  if (status) {
    status.textContent = message;
  }
  console.error(message);
  throw error;
}

const Chart = chartModule?.Chart ?? chartModule?.default ?? chartModule;
if (typeof Chart !== 'function' && typeof Chart?.register !== 'function') {
  const message = 'Chart.js module did not expose a Chart constructor';
  if (status) {
    status.textContent = message;
  }
  throw new Error(message);
}

if (typeof Chart.register === 'function' && chartModule?.registerables) {
  Chart.register(...chartModule.registerables);
}

const helpers = Chart?.helpers ?? chartModule?.helpers;
if (helpers?.canvas?.acquireContext && !helpers.canvas.__avaloniaPatched) {
  const originalAcquire = helpers.canvas.acquireContext.bind(helpers.canvas);
  helpers.canvas.acquireContext = (item, ...args) => {
    if (item === surface || item === canvasElement || item === context) {
      return context;
    }
    return originalAcquire(item, ...args);
  };
  helpers.canvas.__avaloniaPatched = true;
}

const labels = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
let dataPoints = labels.map(() => 40 + Math.round(Math.random() * 60));
let currentType = 'line';
let chart;

const buildGradient = () => {
  const gradientHeight = canvasElement.offsetHeight ?? canvasElement.height ?? logicalHeight;
  const gradient = context.createLinearGradient(0, 0, 0, gradientHeight);
  gradient.addColorStop(0, 'rgba(59, 130, 246, 0.35)');
  gradient.addColorStop(1, 'rgba(59, 130, 246, 0.08)');
  return gradient;
};

const createConfig = type => ({
  type,
  data: {
    labels,
    datasets: [
      {
        label: 'Active sessions',
        data: dataPoints,
        borderColor: '#2563eb',
        backgroundColor: type === 'bar' ? '#93c5fd' : buildGradient(),
        borderWidth: 2,
        fill: type !== 'bar',
        tension: 0.45,
        pointRadius: type === 'bar' ? 0 : 4,
        pointBackgroundColor: '#1d4ed8',
        hoverRadius: 6
      }
    ]
  },
  options: {
    responsive: false,
    animation: {
      duration: 600,
      easing: 'easeInOutCubic'
    },
    scales: {
      x: {
        ticks: { color: '#64748b' },
        grid: { color: 'rgba(148, 163, 184, 0.18)' }
      },
      y: {
        beginAtZero: true,
        suggestedMax: 120,
        ticks: { color: '#64748b' },
        grid: { color: 'rgba(148, 163, 184, 0.12)' }
      }
    },
    plugins: {
      legend: {
        labels: { color: '#0f172a' }
      },
      tooltip: {
        backgroundColor: '#0f172a',
        borderColor: '#1d4ed8',
        borderWidth: 1,
        padding: 12
      }
    }
  }
});

const report = message => {
  if (status) {
    status.textContent = message;
  }
};

const renderChart = type => {
  if (chart) {
    chart.destroy();
  }
  try {
    chart = new Chart(canvasElement, createConfig(type));
    report(`Chart.js ${(chartModule?.version ?? Chart?.version) ?? ''} rendering a ${type} chart`);
  } catch (error) {
    report(`Failed to create chart: ${error}`);
    console.error('Chart.js initialisation error', error);
    chart = null;
  }
};

const randomiseData = () => {
  dataPoints = labels.map(() => 25 + Math.round(Math.random() * 90));
  if (!chart) {
    return;
  }
  chart.data.datasets[0].data = dataPoints;
  if (currentType !== 'bar') {
    chart.data.datasets[0].backgroundColor = buildGradient();
  }
  chart.update();
  report('Dataset randomised with smooth animation');
};

randomBtn.addEventListener('click', () => {
  if (chart) {
    randomiseData();
  }
});

toggleBtn.addEventListener('click', () => {
  currentType = currentType === 'line' ? 'bar' : 'line';
  renderChart(currentType);
});

renderChart(currentType);
"""
            ),
            new Preset(
                "Canvas 2D + Fabric.js",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16" Background="#ffffff" BorderBrush="#d1d5db" BorderThickness="1" CornerRadius="8">
  <StackPanel Spacing="12">
    <TextBlock Text="Canvas 2D with Fabric.js" FontWeight="SemiBold" Foreground="#1f2937" />
    <Border Name="fabricSurface" Width="560" Height="320" Background="#f8fafc" BorderBrush="#e2e8f0" BorderThickness="1" CornerRadius="4" />
    <StackPanel Orientation="Horizontal" Spacing="8">
      <Button Name="fabricAddRect" Content="Add rectangle" />
      <Button Name="fabricAddCircle" Content="Add circle" />
      <Button Name="fabricToggleGrid" Content="Toggle grid" />
      <Button Name="fabricReset" Content="Reset scene" />
    </StackPanel>
    <TextBlock Name="fabricStatus" Foreground="#475569" TextWrapping="Wrap" />
  </StackPanel>
</Border>
""",
                """
const surface = document.getElementById('fabricSurface');
const addRect = document.getElementById('fabricAddRect');
const addCircle = document.getElementById('fabricAddCircle');
const gridBtn = document.getElementById('fabricToggleGrid');
const resetBtn = document.getElementById('fabricReset');
const status = document.getElementById('fabricStatus');

if (!surface) {
  throw new Error('fabricSurface element not found');
}

surface.width = surface.offsetWidth;
surface.height = surface.offsetHeight;

let fabricModule;
try {
  fabricModule = require('https://cdn.jsdelivr.net/npm/fabric@5.3.0/dist/fabric.min.js');
} catch (error) {
  const message = `Failed to load Fabric.js: ${error}`;
  if (status) {
    status.textContent = message;
  }
  console.error(message);
  throw error;
}

const fabricGlobal = typeof window !== 'undefined' ? window.fabric : undefined;
const fabric = fabricModule?.fabric ?? fabricModule?.default ?? fabricGlobal ?? fabricModule;
if (!fabric?.Canvas) {
  const message = 'Fabric.js module did not expose a Canvas constructor';
  if (status) {
    status.textContent = message;
  }
  throw new Error(message);
}

const canvas = new fabric.Canvas(surface, {
  selection: true,
  backgroundColor: '#f8fafc',
  preserveObjectStacking: true
});
canvas.setDimensions({ width: surface.offsetWidth, height: surface.offsetHeight });

const report = message => {
  if (status) {
    status.textContent = message;
  }
};

const randomBetween = (min, max) => Math.round(min + Math.random() * (max - min));
const randomColor = () => {
  const palette = ['#3b82f6', '#ec4899', '#f97316', '#22c55e', '#a855f7'];
  return palette[randomBetween(0, palette.length - 1)];
};

const gridLines = [];
let gridVisible = false;

const clearGrid = () => {
  while (gridLines.length) {
    const line = gridLines.pop();
    canvas.remove(line);
  }
};

const createGrid = () => {
  clearGrid();
  const step = 60;
  const width = canvas.getWidth();
  const height = canvas.getHeight();
  for (let x = step; x < width; x += step) {
    const line = new fabric.Line([x, 0, x, height], {
      stroke: '#cbd5f5',
      strokeWidth: 1,
      strokeDashArray: [6, 6],
      selectable: false,
      evented: false
    });
    line.excludeFromExport = true;
    gridLines.push(line);
    canvas.add(line);
    line.sendToBack();
  }
  for (let y = step; y < height; y += step) {
    const line = new fabric.Line([0, y, width, y], {
      stroke: '#cbd5f5',
      strokeWidth: 1,
      strokeDashArray: [6, 6],
      selectable: false,
      evented: false
    });
    line.excludeFromExport = true;
    gridLines.push(line);
    canvas.add(line);
    line.sendToBack();
  }
  gridVisible = true;
  report('Grid overlay enabled – drag shapes to explore snapping visuals.');
  canvas.renderAll();
};

const toggleGrid = () => {
  if (gridVisible) {
    clearGrid();
    gridVisible = false;
    canvas.renderAll();
    report('Grid overlay removed.');
    return;
  }
  createGrid();
};

const baseShapes = () => {
  clearGrid();
  gridVisible = false;
  canvas.clear();
  canvas.backgroundColor = '#f1f5f9';

  const gradientRect = new fabric.Rect({
    left: 80,
    top: 70,
    width: 200,
    height: 140,
    rx: 26,
    ry: 26,
    fill: new fabric.Gradient({
      type: 'linear',
      gradientUnits: 'percentage',
      coords: { x1: 0, y1: 0, x2: 0, y2: 1 },
      colorStops: [
        { offset: 0, color: '#93c5fd' },
        { offset: 1, color: '#1d4ed8' }
      ]
    }),
    stroke: '#1e3a8a',
    strokeWidth: 2
  });
  gradientRect.shadow = new fabric.Shadow({
    color: 'rgba(15, 23, 42, 0.18)',
    blur: 16,
    offsetX: 0,
    offsetY: 10
  });

  const circle = new fabric.Circle({
    left: 320,
    top: 90,
    radius: 70,
    fill: new fabric.Gradient({
      type: 'radial',
      coords: { r1: 0, r2: 1, x1: 0.5, y1: 0.5, x2: 0.5, y2: 0.5 },
      colorStops: [
        { offset: 0, color: '#fda4af' },
        { offset: 1, color: '#db2777' }
      ]
    }),
    stroke: '#be123c',
    strokeWidth: 2
  });

  const text = new fabric.Textbox('Fabric.js', {
    left: 200,
    top: 240,
    width: 200,
    fontSize: 28,
    fontFamily: 'Segoe UI',
    fontWeight: 'bold',
    fill: '#0f172a',
    textAlign: 'center'
  });

  canvas.add(gradientRect, circle, text);
  canvas.renderAll();
  report('Scene initialised with layered vector objects.');
};

const addRandomRect = () => {
  const rect = new fabric.Rect({
    left: randomBetween(40, canvas.getWidth() - 160),
    top: randomBetween(40, canvas.getHeight() - 120),
    width: randomBetween(80, 160),
    height: randomBetween(60, 120),
    rx: 18,
    ry: 18,
    fill: randomColor(),
    opacity: 0.85
  });
  rect.shadow = new fabric.Shadow({
    color: 'rgba(15, 23, 42, 0.15)',
    blur: 14,
    offsetX: 0,
    offsetY: 8
  });
  canvas.add(rect);
  canvas.setActiveObject(rect);
  report('Added a draggable rounded rectangle.');
};

const addRandomCircle = () => {
  const circle = new fabric.Circle({
    left: randomBetween(60, canvas.getWidth() - 140),
    top: randomBetween(60, canvas.getHeight() - 140),
    radius: randomBetween(36, 72),
    fill: randomColor(),
    stroke: '#1f2937',
    strokeWidth: 1.5,
    opacity: 0.9
  });
  circle.shadow = new fabric.Shadow({
    color: 'rgba(15, 23, 42, 0.12)',
    blur: 12,
    offsetX: 0,
    offsetY: 6
  });
  canvas.add(circle);
  canvas.setActiveObject(circle);
  report('Added an interactive circle.');
};

canvas.on('object:modified', evt => {
  const target = evt?.target;
  if (!target) {
    return;
  }
  const { left, top, angle } = target;
  report(`Updated ${target.type} → left=${Math.round(left ?? 0)}, top=${Math.round(top ?? 0)}, angle=${Math.round(angle ?? 0)}`);
});

canvas.on('selection:created', evt => {
  const target = evt?.selected?.[0];
  if (target) {
    report(`Selected ${target.type} – try scaling or rotating.`);
  }
});

canvas.on('selection:cleared', () => {
  report('Selection cleared.');
});

addRect.addEventListener('click', addRandomRect);
addCircle.addEventListener('click', addRandomCircle);
gridBtn.addEventListener('click', toggleGrid);
resetBtn.addEventListener('click', () => {
  baseShapes();
  report('Scene reset to default composition.');
});

baseShapes();
"""
            ),
            new Preset(
                "Canvas 2D + Paper.js",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16" Background="#0f172a" CornerRadius="8">
  <StackPanel Spacing="12">
    <TextBlock Text="Canvas 2D with Paper.js" FontWeight="SemiBold" Foreground="#f8fafc" />
    <Border Name="paperSurface" Width="560" Height="320" Background="#111827" BorderBrush="#1e293b" BorderThickness="1" CornerRadius="6" />
    <StackPanel Orientation="Horizontal" Spacing="8">
      <Button Name="paperMutate" Content="Mutate scene" />
      <Button Name="paperToggle" Content="Pause / resume" />
    </StackPanel>
    <TextBlock Name="paperStatus" Foreground="#e2e8f0" TextWrapping="Wrap" />
  </StackPanel>
</Border>
""",
                """
const surface = document.getElementById('paperSurface');
const mutateBtn = document.getElementById('paperMutate');
const toggleBtn = document.getElementById('paperToggle');
const status = document.getElementById('paperStatus');

if (!surface) {
  throw new Error('paperSurface element not found');
}

const paperContext = surface.getContext('2d');
if (!paperContext) {
  throw new Error('CanvasRenderingContext2D unavailable for Paper.js demo');
}

const paperCanvas = paperContext.canvas ?? surface;
paperCanvas.width = surface.offsetWidth;
paperCanvas.height = surface.offsetHeight;

let paperModule;
try {
  paperModule = require('https://cdn.jsdelivr.net/npm/paper@0.12.17/dist/paper-full.min.js');
} catch (error) {
  const message = `Failed to load Paper.js: ${error}`;
  if (status) {
    status.textContent = message;
  }
  console.error(message);
  throw error;
}

const paperGlobal = typeof window !== 'undefined' ? window.paper : undefined;
const paper = paperModule?.paper ?? paperModule?.default ?? paperGlobal ?? paperModule;
if (typeof paper?.setup !== 'function') {
  const message = 'Paper.js module did not expose a setup function';
  if (status) {
    status.textContent = message;
  }
  throw new Error(message);
}

paper.setup(paperCanvas);

const report = message => {
  if (status) {
    status.textContent = message;
  }
};

const width = surface.offsetWidth;
const height = surface.offsetHeight;
const center = new paper.Point(width / 2, height / 2);

const background = new paper.Path.Rectangle({
  point: [0, 0],
  size: [width, height],
  radius: 18,
  fillColor: '#111827'
});
background.sendToBack();

const halo = new paper.Path.Circle({
  center,
  radius: Math.min(width, height) * 0.45,
  strokeColor: '#60a5fa',
  dashArray: [12, 8],
  strokeWidth: 2,
  opacity: 0.85
});

const star = new paper.Path.Star({
  center,
  points: 7,
  radius1: Math.min(width, height) * 0.16,
  radius2: Math.min(width, height) * 0.34,
  fillColor: '#ec4899',
  strokeColor: '#be185d',
  strokeWidth: 3
});
star.shadowColor = new paper.Color(0, 0, 0, 0.28);
star.shadowBlur = 18;
star.shadowOffset = new paper.Point(0, 10);

const ribbon = new paper.Path.Rectangle({
  point: [60, height / 2 - 40],
  size: [width - 120, 80],
  radius: 28,
  fillColor: new paper.Color(0.22, 0.35, 0.55, 0.45)
});
ribbon.rotate(-8, center);
ribbon.blendMode = 'soft-light';

const wave = new paper.Path({
  strokeColor: '#38bdf8',
  strokeWidth: 2,
  opacity: 0.75,
  smooth: true
});
wave.sendToBack();
let waveBase = [];

const rebuildWave = () => {
  wave.removeSegments();
  const segments = 14;
  const baseline = height - 70;
  const amplitude = 22;
  for (let i = 0; i <= segments; i++) {
    const x = (width / segments) * i;
    const phase = (i / segments) * Math.PI * 2;
    const y = baseline + Math.sin(phase) * amplitude;
    wave.add(new paper.Point(x, y));
  }
  wave.smooth({ type: 'catmull-rom', factor: 0.5 });
  waveBase = wave.segments.map(segment => segment.point.y);
};

rebuildWave();

const connectors = new paper.Group();
const rebuildConnectors = () => {
  connectors.removeChildren();
  const spokes = 6;
  for (let i = 0; i < spokes; i++) {
    const angle = (360 / spokes) * i;
    const start = center.add(new paper.Point({ length: star.bounds.width * 0.2, angle }));
    const end = center.add(new paper.Point({ length: star.bounds.width * 0.8, angle }));
    const path = new paper.Path.Line(start, end);
    path.strokeColor = new paper.Color(0.55, 0.63, 0.75, 0.65);
    path.strokeWidth = 1.5;
    path.dashArray = [8, 6];
    connectors.addChild(path);
  }
  connectors.sendToBack();
};

rebuildConnectors();

const particles = new paper.Group();
const createParticles = count => {
  particles.removeChildren();
  for (let i = 0; i < count; i++) {
    const base = new paper.Point(
      40 + Math.random() * (width - 80),
      80 + Math.random() * (height - 140)
    );
    const radius = 5 + Math.random() * 9;
    const blob = new paper.Path.Circle({
      center: base,
      radius,
      fillColor: new paper.Color({
        hue: 210 + Math.random() * 40,
        saturation: 0.35,
        brightness: 0.95,
        alpha: 0.45
      })
    });
    blob.blendMode = 'screen';
    blob.data = {
      base,
      amplitude: 4 + Math.random() * 6,
      speed: 0.5 + Math.random() * 1.2
    };
    particles.addChild(blob);
  }
};

createParticles(26);

let animationEnabled = true;

paper.view.onFrame = event => {
  if (!animationEnabled) {
    return;
  }

  star.rotate(0.6);
  connectors.rotate(0.25, center);

  particles.children.forEach((particle, index) => {
    const base = particle.data.base;
    const amp = particle.data.amplitude;
    const speed = particle.data.speed;
    const phase = event.time * speed + index;
    particle.position.x = base.x + Math.sin(phase) * amp * 4;
    particle.position.y = base.y + Math.cos(phase * 1.4) * amp * 2;
  });

  wave.segments.forEach((segment, index) => {
    segment.point.y = waveBase[index] + Math.sin(event.time * 1.6 + index * 0.35) * 4;
  });
  wave.smooth({ type: 'catmull-rom', factor: 0.5 });
};

const mutateScene = () => {
  const hue = Math.round(Math.random() * 360);
  star.fillColor = new paper.Color({ hue, saturation: 0.65, brightness: 0.95 });
  star.strokeColor = new paper.Color({ hue: (hue + 320) % 360, saturation: 0.6, brightness: 0.6 });
  halo.strokeColor = new paper.Color({ hue: (hue + 180) % 360, saturation: 0.4, brightness: 0.85 });
  ribbon.fillColor = new paper.Color({ hue: (hue + 40) % 360, saturation: 0.3, brightness: 0.55, alpha: 0.5 });

  particles.children.forEach((particle, index) => {
    const base = new paper.Point(
      40 + Math.random() * (width - 80),
      80 + Math.random() * (height - 140)
    );
    particle.data.base = base;
    particle.position = base.clone();
    particle.data.amplitude = 4 + Math.random() * 7;
    particle.data.speed = 0.5 + Math.random() * 1.4;
    particle.fillColor = new paper.Color({
      hue: (hue + index * 9) % 360,
      saturation: 0.4,
      brightness: 0.95,
      alpha: 0.48
    });
  });

  rebuildWave();
  rebuildConnectors();
  paper.view.requestUpdate();
  report('Paper.js scene mutated with new palette and geometry.');
};

mutateBtn.addEventListener('click', mutateScene);

toggleBtn.addEventListener('click', () => {
  animationEnabled = !animationEnabled;
  if (animationEnabled) {
    paper.view.play();
    report('Animation resumed.');
  } else {
    paper.view.pause();
    report('Animation paused — mutate to explore new layouts.');
  }
});

report('Paper.js generative scene ready — mutate the palette or pause the motion.');
paper.view.play();
"""
            ),
            new Preset(
                "Modules: CommonJS",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16" Background="#f9fafc" BorderBrush="#d8e2ef" BorderThickness="1" CornerRadius="8">
  <StackPanel Spacing="10">
    <TextBlock Text="CommonJS module (ms)" FontWeight="SemiBold" FontSize="18" Foreground="#1f2937" />
    <TextBlock Text="Convert between human readable durations and milliseconds using the ms package." TextWrapping="Wrap" Foreground="#475569" />
    <StackPanel Orientation="Horizontal" Spacing="8">
      <TextBox Name="msInput" Width="160" Text="1.5h" />
      <Button Name="msParse" Content="Parse ⟶ ms" />
      <Button Name="msFormat" Content="Format ⟶ long" />
    </StackPanel>
    <TextBlock Name="msOutput" Foreground="#0f172a" FontFamily="Consolas" />
  </StackPanel>
</Border>
""",
                """
const input = document.getElementById('msInput');
const parseBtn = document.getElementById('msParse');
const formatBtn = document.getElementById('msFormat');
const output = document.getElementById('msOutput');

let ms;
try {
  ms = require('https://cdn.jsdelivr.net/npm/ms@2.1.3/index.js');
  if (typeof ms !== 'function') {
    throw new Error('ms module did not export a function');
  }
} catch (error) {
  const message = `Failed to load ms module: ${error}`;
  output.textContent = message;
  console.error(message);
  throw error;
}

const report = message => {
  output.textContent = message ?? '';
};

parseBtn.addEventListener('click', () => {
  const value = input.value?.trim();
  if (!value) {
    report('Enter a duration like 2500, 2m, 1.5h');
    return;
  }

  try {
    const result = ms(value);
    if (typeof result !== 'number' || Number.isNaN(result)) {
      report(`Could not parse "${value}"`);
      return;
    }
    report(`${value} = ${result.toLocaleString()} ms`);
  } catch (error) {
    report(`Parse error: ${error}`);
  }
});

formatBtn.addEventListener('click', () => {
  const value = Number.parseFloat(input.value ?? '');
  if (!Number.isFinite(value)) {
    report('Provide a numeric millisecond value to format.');
    return;
  }

  try {
    report(`${value.toLocaleString()} ms ≈ ${ms(value, { long: true })}`);
  } catch (error) {
    report(`Format error: ${error}`);
  }
});

report('CommonJS require() ready – try parsing "45m" or formatting 900000.');
"""
            ),
            new Preset(
                "Modules: AMD",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16" Background="#ffffff" BorderBrush="#d9e3f1" BorderThickness="1" CornerRadius="8">
  <StackPanel Spacing="12">
    <TextBlock Text="AMD module (underscore)" FontWeight="SemiBold" FontSize="18" Foreground="#111827" />
    <TextBlock Text="Load underscore via its AMD factory and explore functional helpers." TextWrapping="Wrap" Foreground="#475569" />
    <ItemsControl Name="amdTodo">
      <ItemsControl.Items>
        <TextBlock Text="Refactor module loader" />
        <TextBlock Text="Review pull requests" />
        <TextBlock Text="Write release notes" />
        <TextBlock Text="Triage issues" />
        <TextBlock Text="Ship new sample" />
      </ItemsControl.Items>
    </ItemsControl>
    <StackPanel Orientation="Horizontal" Spacing="8">
      <Button Name="amdShuffle" Content="Shuffle" />
      <Button Name="amdSample" Content="Pick task" />
    </StackPanel>
    <TextBlock Name="amdStatus" Foreground="#1e293b" FontFamily="Consolas" TextWrapping="Wrap" />
  </StackPanel>
</Border>
""",
                """
const list = document.getElementById('amdTodo');
const shuffleBtn = document.getElementById('amdShuffle');
const sampleBtn = document.getElementById('amdSample');
const status = document.getElementById('amdStatus');

let underscore;
try {
  underscore = require('https://cdn.jsdelivr.net/npm/underscore@1.13.6/underscore-min.js');
  if (!underscore || typeof underscore.sample !== 'function') {
    throw new Error('underscore export missing expected helpers');
  }
} catch (error) {
  const message = `Failed to load underscore via AMD: ${error}`;
  status.textContent = message;
  console.error(message);
  throw error;
}

const readItems = () => Array.from(list.children ?? []).map(child => child.textContent).filter(Boolean);

shuffleBtn.addEventListener('click', () => {
  const items = readItems();
  const shuffled = underscore.shuffle(items);
  list.children?.forEach(child => child.remove());
  shuffled.forEach(text => {
    const node = document.createElement('TextBlock');
    node.textContent = text;
    list.appendChild(node);
  });
  status.textContent = 'Shuffled using underscore.shuffle()';
});

sampleBtn.addEventListener('click', () => {
  const items = readItems();
  if (items.length === 0) {
    status.textContent = 'List is empty';
    return;
  }
  const pick = underscore.sample(items);
  status.textContent = `Focus next: ${pick}`;
});

status.textContent = 'underscore AMD module ready – shuffle the backlog or pick a task.';
"""
            ),
            new Preset(
                "Modules: Global fallback",
                """
<Border xmlns="https://github.com/avaloniaui" Padding="16" Background="#0f172a" CornerRadius="8">
  <StackPanel Spacing="12">
    <TextBlock Text="Global factory (anime.js)" FontWeight="SemiBold" Foreground="#f8fafc" FontSize="18" />
    <TextBlock Text="anime.js exposes a browser global – the loader captures it so we can orchestrate tweening." TextWrapping="Wrap" Foreground="#cbd5f5" />
    <StackPanel Orientation="Horizontal" Spacing="10" HorizontalAlignment="Center">
      <Border Name="animeBar1" Width="60" Height="12" CornerRadius="6" Background="#38bdf8" />
      <Border Name="animeBar2" Width="60" Height="12" CornerRadius="6" Background="#818cf8" />
      <Border Name="animeBar3" Width="60" Height="12" CornerRadius="6" Background="#f472b6" />
    </StackPanel>
    <StackPanel Orientation="Horizontal" Spacing="8">
      <Button Name="animePlay" Content="Play animation" />
      <Button Name="animeReset" Content="Reset" />
    </StackPanel>
    <TextBlock Name="animeStatus" Foreground="#e2e8f0" />
  </StackPanel>
</Border>
""",
                """
const bar1 = document.getElementById('animeBar1');
const bar2 = document.getElementById('animeBar2');
const bar3 = document.getElementById('animeBar3');
const playBtn = document.getElementById('animePlay');
const resetBtn = document.getElementById('animeReset');
const status = document.getElementById('animeStatus');

let animeFactory;
try {
  const module = require('https://cdn.jsdelivr.net/npm/animejs@3.2.1/lib/anime.min.js');
  animeFactory = module?.anime ?? module ?? window.anime;
  if (typeof animeFactory !== 'function') {
    throw new Error('anime.js global was not detected');
  }
} catch (error) {
  const message = `Failed to load anime.js: ${error}`;
  status.textContent = message;
  console.error(message);
  throw error;
}

const bars = [bar1, bar2, bar3].filter(Boolean);

const resetBars = () => {
  bars.forEach((bar, index) => {
    bar.style.setProperty('transform', 'scaleX(1)');
    bar.style.setProperty('opacity', '1');
    bar.style.setProperty('box-shadow', 'none');
    bar.setAttribute('width', '60');
    bar.setAttribute('Background', index === 0 ? '#38bdf8' : index === 1 ? '#818cf8' : '#f472b6');
  });
  status.textContent = 'Ready – animate the bars!';
};

resetBars();

let running = null;

const playAnimation = () => {
  if (running) {
    running.pause?.();
  }

  running = animeFactory({
    targets: bars,
    scaleX: [1, 2.4],
    opacity: [1, 0.45],
    borderRadius: ['6px', '12px'],
    direction: 'alternate',
    easing: 'easeInOutSine',
    duration: 1200,
    delay: animeFactory.stagger(120),
    loop: 2,
    complete: () => {
      status.textContent = 'Animation finished – global fallback succeeded.';
    }
  });

  status.textContent = 'Animating with anime.js global factory...';
};

playBtn.addEventListener('click', playAnimation);

resetBtn.addEventListener('click', () => {
  if (running) {
    running.pause?.();
    running = null;
  }
  resetBars();
  status.textContent = 'Animation reset.';
});
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
