# JavaScript.Avalonia

JavaScript.Avalonia hosts a [Jint](https://github.com/sebastienros/jint) JavaScript engine inside any Avalonia `TopLevel`. It exposes a browser-like programming model—`window`, `document`, DOM traversal, timers, animation frames, and event wiring—so you can script Avalonia UI the same way you would a web page.

The library was originally extracted from the HtmlML project and is now a standalone, reusable component for any Avalonia application that would benefit from JavaScript-driven behaviour.

## Features

- **Embedded JavaScript runtime** with configurable `JintAvaloniaHost`.
- **DOM-style document API** (`document.getElementById`, `querySelector(All)`, `createElement`, `document.body`).
- **Control wrapper** (`AvaloniaDomElement`) exposing `appendChild`, `remove`, `setAttribute`, `classList*`, `textContent` and more.
- **Event bridge** that maps common DOM event names to Avalonia routed events and delivers strongly-typed event payloads back to JavaScript.
- **Timers and animation** via `window.setTimeout`, `window.requestAnimationFrame`, and `window.cancelAnimationFrame` bound to Avalonia’s dispatcher and `TopLevel.RequestAnimationFrame`.
- **Console output** routed to `System.Console.WriteLine` for easy debugging.
- **Extensibility hooks**: override `CreateDocument`, supply a custom element factory, or derive your own `AvaloniaDomElement` to add behaviour.

## Getting Started

1. Add a reference to the `JavaScript.Avalonia` project or package.
2. Create a `JintAvaloniaHost` inside your window or control.
3. Execute script text or load scripts from URIs.

```csharp
using JavaScript.Avalonia;

public partial class MainWindow : Window
{
    private readonly JintAvaloniaHost _js;

    public MainWindow()
    {
        InitializeComponent();
        _js = new JintAvaloniaHost(this);

        _js.ExecuteScriptText("""
const output = document.getElementById('StatusText');
if (output) {
  output.textContent = 'Hello from JavaScript';
}
""");
    }
}
```

Scripts can be embedded strings, loaded from files, or resolved through Avalonia’s `avares://` resource scheme using `ExecuteScriptUri`.

## DOM API

The default `AvaloniaDomDocument` exposes familiar DOM-like methods:

| API | Description |
| --- | --- |
| `document.getElementById(id)` | Walks the Avalonia visual/content tree and returns a wrapped control matching the `Name`/`id`. |
| `document.querySelector(selector)` | Supports `#id`, `.class`, or type-name selectors. |
| `document.querySelectorAll(selector)` | Returns an array of wrapped controls that match the selector. |
| `document.createElement(tag)` | Creates a new Avalonia control. Native tags such as `Button`, `TextBlock`, etc. resolve to the corresponding Avalonia control type; unknown tags fall back to reflection-based lookup in `Avalonia.Controls`. |
| `document.body` | Returns the root control (`TopLevel.Content`) wrapped in an `AvaloniaDomElement`. |

Each `AvaloniaDomElement` exposes:

- `appendChild(child)` and `remove()` for manipulating the visual tree.
- `setAttribute(name, value)` / `getAttribute(name)` for standard attributes (`id`, `class`, `title`, others map to Avalonia properties if they exist).
- `classListAdd`, `classListRemove`, `classListToggle` for manipulating `Classes`.
- `textContent` getter/setter when the underlying control is a `TextBlock`.

> **Extending the DOM**: Derive from `AvaloniaDomDocument` to customise control creation or wrapping. HtmlML, for example, overrides `CreateDocument` in `JintHost` so `<canvas>` elements return a special wrapper.

## Event Bridge

`AvaloniaDomElement.addEventListener(type, handler)` wires JavaScript callbacks into Avalonia routed events. Supported event names include:

- `pointerdown` / `mousedown`
- `pointermove` / `mousemove`
- `pointerup` / `mouseup`
- `pointerenter` / `mouseenter`
- `pointerleave` / `mouseleave`
- `click` (uses `Button.Click` when available, otherwise falls back to `PointerReleased`)
- `keydown`
- `keyup`
- `textinput` / `input`

Example usage:

```js
button.addEventListener('click', () => console.log('Clicked!'));

textBox.addEventListener('keydown', evt => {
  console.log(`Key pressed: ${evt.key}`);
  evt.preventDefault();
});

canvas.addEventListener('pointermove', evt => {
  console.log(`Pointer at ${evt.x}, ${evt.y}`);
});
```

Handlers now receive DOM-style event objects. Every event exposes `type`, `target`, `currentTarget`, `timeStamp`, `defaultPrevented`, and the helper methods `stopPropagation()`, `stopImmediatePropagation()`, and `preventDefault()`. Listener options (`capture`, `once`, `passive`) mirror the browser and participate in capture/target/bubble ordering.

Pointer, keyboard, and text-input events extend the base payload:

```ts
interface DomPointerEvent {
  type: string;
  target: object | null;
  currentTarget: object | null;
  eventPhase: 1 | 2 | 3;
  pointerId: number;
  pointerType: string;
  x: number;
  y: number;
  button: number;
  buttons: number;
  altKey: boolean;
  ctrlKey: boolean;
  shiftKey: boolean;
  metaKey: boolean;
  defaultPrevented: boolean;
  preventDefault(): void;
  stopPropagation(): void;
  stopImmediatePropagation(): void;
}

interface DomKeyboardEvent {
  type: string;
  key: string | null;
  code: string | null;
  altKey: boolean;
  ctrlKey: boolean;
  shiftKey: boolean;
  metaKey: boolean;
  defaultPrevented: boolean;
  preventDefault(): void;
}

interface DomTextInputEvent {
  type: string;
  data: string | null;
  preventDefault(): void;
}
```

Synthetic events are supported via `dispatchEvent()`. Pass either a string type or a plain object (`{ type, bubbles, cancelable, detail }`). `dispatchEvent` returns `false` when any listener calls `preventDefault()` and also reflects the outcome by updating the supplied object's `defaultPrevented` property.

Remove listeners with `element.removeEventListener(type, handler)`; the same function reference must be supplied.

The document root now exposes `document.documentElement`, `document.head`, and `document.title` in addition to `document.body`, making it easy to append metadata, adjust the window title, or inspect the DOM hierarchy.

## Timers and Animation

`window.setTimeout` schedules callbacks using Avalonia’s dispatcher (`DispatcherTimer`). The callback runs on the UI thread.

`window.requestAnimationFrame` integrates with `TopLevel.RequestAnimationFrame` and passes the frame timestamp (milliseconds) to the callback. Use `window.cancelAnimationFrame(id)` to cancel pending frames.

These helpers are also assigned to `globalThis`, so `setTimeout` and `requestAnimationFrame` work without the `window.` prefix.

## Console Output

Scripts can call `console.log(...)` and the message will be written to `System.Console`. Override `CreateConsoleObject()` if you need custom logging.

## Customising the Host

`JintAvaloniaHost` can be customised by deriving and overriding:

- `ConfigureEngineOptions(Options options)` – tweak Jint configuration (modules, limits, etc.).
- `CreateDocument()` – provide a specialised DOM layer (e.g., HtmlML’s HTML-specific elements).
- `CreateWindowObject()` and `CreateConsoleObject()` – expose additional APIs to JavaScript.

You can also supply a factory to the constructor if you prefer composition over inheritance:

```csharp
var host = new JintAvaloniaHost(this, h => new CustomDocument(h));
```

## Full Example

```csharp
public class ScriptedWindow : Window
{
    private readonly JintAvaloniaHost _js = null!;

    public ScriptedWindow()
    {
        InitializeComponent();
        _js = new JintAvaloniaHost(this);

        _js.ExecuteScriptText("""
const label = document.getElementById('OutputText');
const button = document.getElementById('RunButton');

if (button && label) {
  button.addEventListener('click', () => {
    label.textContent = 'Button clicked from JavaScript!';
    window.setTimeout(() => label.textContent = 'Ready', 1000);
  });
}
""");
    }
}
```

## HtmlML Integration

HtmlML’s `JintHost` inherits from `JintAvaloniaHost`, overrides `CreateDocument()`, and layers additional HTML-specific behaviour (canvas bindings, CSS application, etc.) on top of the shared infrastructure. The same approach can be used to build other frameworks on top of JavaScript.Avalonia.

## License

JavaScript.Avalonia is distributed under the MIT license. See [LICENSE](../../LICENSE).

For more examples, explore the `samples/JavaScriptHostSample` project, which demonstrates manipulating a plain Avalonia window entirely from JavaScript.
