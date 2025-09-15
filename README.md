# HtmlML

HtmlML brings HTML-inspired markup and scripting capabilities to [Avalonia](https://avaloniaui.net/). The repository is split into two reusable libraries that can be consumed together or independently:

- **HtmlML** – a markup layer that renders HTML-like tags inside Avalonia applications, complete with styling and canvas support.
- **JavaScript.Avalonia** – a standalone JavaScript bridge powered by [Jint](https://github.com/sebastienros/jint) which exposes DOM-style APIs and event handling for any Avalonia `TopLevel`.

Together they enable you to describe user interfaces with familiar HTML semantics while orchestrating dynamic behaviour from JavaScript—no browser required.

## Highlights

- ⚡ **Avalonia-first**: Render HTML-inspired controls natively, respecting Avalonia layout, styling, and theming.
- 🧠 **Embedded JavaScript engine**: Run ES-compatible scripts via Jint, with `window`, `document`, timers, animation frames, and console access.
- 🧩 **DOM abstraction**: Query, create, and mutate Avalonia controls through a DOM-like API (`getElementById`, `querySelector`, `appendChild`, `setAttribute`, etc.).
- 🕹️ **Event bridge**: Wire Avalonia routed events (`click`, `pointerdown`, `keydown`, `input`, …) to JavaScript callbacks with strongly-typed payloads.
- 🖼️ **Canvas integration**: HtmlML ships with a `<canvas>` element that mirrors the familiar 2D drawing API.
- 🧱 **Extensible architecture**: Override document/element factories or compose custom hosts to tailor the experience for your application.

## Repository Layout

| Path | Description |
| --- | --- |
| `src/HtmlML` | HtmlML markup library and HTML element implementations. |
| `src/JavaScript.Avalonia` | Generic JavaScript host with DOM/event bridge for Avalonia. |
| `samples/website` | HtmlML showcase demonstrating markup, styling, and canvas scripting. |
| `samples/JavaScriptHostSample` | Plain Avalonia desktop app using `JavaScript.Avalonia` without HtmlML. |

## Getting Started

### Prerequisites

- .NET SDK 8.0 or later (see `global.json` for the tested version).
- A platform supported by Avalonia (Windows, macOS, Linux).

### Building the repository

```bash
# Restore and build everything (libraries + samples)
dotnet build HtmlML.sln
```

### Running the samples

```bash
# HtmlML website sample
dotnet run --project samples/website/website.csproj

# Standalone JavaScript host sample
dotnet run --project samples/JavaScriptHostSample/JavaScriptHostSample.csproj
```

### Consuming the libraries

NuGet packages are not yet published. To reference the projects locally:

```xml
<ItemGroup>
  <ProjectReference Include="..\src\HtmlML\HtmlML.csproj" />
  <ProjectReference Include="..\src\JavaScript.Avalonia\JavaScript.Avalonia.csproj" />
</ItemGroup>
```

If you only need the JavaScript integration, reference `JavaScript.Avalonia` on its own.

## Using HtmlML

HtmlML exposes HTML-like tags (heading levels, paragraphs, lists, sections, navigation, canvas, etc.) that map to Avalonia controls. Example:

```xml
<html xmlns="https://github.com/avaloniaui"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.index"
      title="HtmlML Demo">
  <head>
    <link rel="stylesheet" href="avares://website/Assets/demo.css" type="text/css" />
    <script type="text/javascript">
      <![CDATA[
      document.addEventListener('DOMContentLoaded', () => {
        const canvas = document.getElementById('draw');
        const ctx = canvas.getContext('2d');
        canvas.addEventListener('pointermove', evt => {
          ctx.lineTo(evt.x, evt.y);
          ctx.stroke();
        });
      });
      ]]>
    </script>
  </head>
  <body>
    <section class="card">
      <h1>Hello from HtmlML</h1>
      <canvas id="draw" width="400" height="200" />
    </section>
  </body>
</html>
```

HtmlML parses the markup, applies classes and inline styles, wires `<canvas>` pointers to JavaScript, and allows scripts to manipulate the resulting visual tree.

## Using JavaScript.Avalonia directly

`JintAvaloniaHost` is the entry point for integrating JavaScript into any Avalonia window:

```csharp
public partial class MainWindow : Window
{
    private readonly JintAvaloniaHost _jsHost;

    public MainWindow()
    {
        InitializeComponent();
        _jsHost = new JintAvaloniaHost(this);

        _jsHost.ExecuteScriptText("""
const label = document.getElementById('OutputText');
const button = document.getElementById('RunButton');

if (button && label) {
  button.addEventListener('click', () => {
    label.textContent = 'Button clicked from JavaScript!';
    setTimeout(() => label.textContent = 'Ready', 1000);
  });
}
""");
    }
}
```

### Event payloads

Handlers receive simple objects that expose `handled` flags for two-way communication:

```js
textBox.addEventListener('keydown', evt => {
  if (evt.key === 'Enter') {
    evt.handled = true; // stop Avalonia routing
  }
});
```

| Event | Payload |
| --- | --- |
| `pointer*`, `mouse*`, `click` | `{ x, y, button?, handled }` |
| `keydown`, `keyup` | `{ key?, handled }` |
| `textinput`, `input` | `{ text?, handled }` |

## Architecture Overview

```
HtmlML (markup + HTML elements)
 ├─ Core (HtmlElementBase, styling helpers)
 ├─ Html (HTML-like control set, canvas)
 └─ JavaScript (Html-specific document bridging)

JavaScript.Avalonia (standalone library)
 ├─ JintAvaloniaHost (engine lifecycle, timers, document binding)
 └─ AvaloniaDomDocument / AvaloniaDomElement (DOM traversal, events, attribute handling)
```

HtmlML builds on JavaScript.Avalonia by overriding the generic DOM to inject HTML-specific behaviour (canvas wrappers, style parsing, etc.).

## Roadmap

- Publish official NuGet packages for HtmlML and JavaScript.Avalonia.
- Expand HTML element coverage and CSS support.
- Provide additional samples (MVVM integration, hybrid C#/JS applications).
- Improve documentation and API reference.

## Contributing

Contributions, bug reports, and feature requests are welcome! Please open an issue or submit a pull request. When contributing code:

1. Fork the repository and create a feature branch.
2. Run `dotnet build` to ensure the solution compiles.
3. Include tests or sample updates when applicable.
4. Describe the motivation and details in your PR.

## License

Both HtmlML and JavaScript.Avalonia are distributed under the terms of the [GNU Affero General Public License v3.0](https://www.gnu.org/licenses/agpl-3.0.html).

If your organisation requires a different licensing arrangement, please reach out to discuss commercial options.

## Acknowledgements

- [AvaloniaUI](https://github.com/AvaloniaUI/Avalonia) for the cross-platform UI framework.
- [Jint](https://github.com/sebastienros/jint) for the embeddable JavaScript engine.
- [AngleSharp](https://anglesharp.github.io/) for HTML/CSS parsing used by HtmlML.

---

© Wiesław Šoltés. All rights reserved.
