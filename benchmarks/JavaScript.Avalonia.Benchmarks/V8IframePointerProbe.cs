using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JavaScript.Avalonia;
using JavaScript.Avalonia.ClearScript;

namespace JavaScript.Avalonia.Benchmarks;

/// <summary>
/// Product-free reproduction for native hit testing and DOM pointer-event
/// delivery across a virtual iframe boundary.
/// </summary>
internal static class V8IframePointerProbe
{
    internal static int Run()
    {
        BenchmarkApp.EnsureInitialized();
        var root = new CssLayoutPanel { ClipToBounds = true };
        var window = new Window
        {
            Width = 420,
            Height = 260,
            Content = root
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var host = new AvaloniaBrowserHost(window);
        using var runtime = new ClearScriptV8Runtime(
            host,
            new ClearScriptV8RuntimeOptions { EnableTrustedSameOriginContextSharing = true });

        try
        {
            runtime.Execute("""
                const iframe = document.createElement('iframe');
                iframe.id = 'pointer-frame';
                iframe.style.position = 'absolute';
                iframe.style.left = '35px';
                iframe.style.top = '28px';
                iframe.style.width = '320px';
                iframe.style.height = '180px';
                document.body.appendChild(iframe);
                const markup = `<!doctype html><html><head><style>
                  html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; }
                  #frame-root { position: relative; width: 100%; height: 100%; }
                  #hit-button { position: absolute; left: 70px; top: 55px; width: 140px; height: 54px; }
                </style></head><body><div id="frame-root"><button id="hit-button">Hit me</button></div><script>
                  window.__pointerProbe = {
                    down: 0, up: 0, mouseUp: 0, lostCapture: 0, click: 0,
                    target: '', clientX: -1, clientY: -1,
                    targetIdentity: false, currentTargetIdentity: false, managedMarker: '',
                    domException: false
                  };
                  const abortError = new DOMException('Aborted', 'AbortError');
                  __pointerProbe.domException = abortError instanceof Error &&
                    abortError.name === 'AbortError' && abortError.message === 'Aborted';
                  const button = document.getElementById('hit-button');
                  button.__managedMarker = 'v8-managed-button';
                  button.addEventListener('pointerdown', function (event) {
                    __pointerProbe.down++;
                    __pointerProbe.target = event.target && event.target.id || '';
                    __pointerProbe.clientX = event.clientX;
                    __pointerProbe.clientY = event.clientY;
                    __pointerProbe.targetIdentity = event.target === button;
                    __pointerProbe.currentTargetIdentity = event.currentTarget === button && this === button;
                    __pointerProbe.managedMarker = event.target && event.target.__managedMarker || '';
                    button.setPointerCapture(event.pointerId);
                  });
                  button.addEventListener('lostpointercapture', function () { __pointerProbe.lostCapture++; });
                  window.addEventListener('pointerup', function () { __pointerProbe.up++; });
                  window.addEventListener('mouseup', function () { __pointerProbe.mouseUp++; });
                  button.addEventListener('click', function () { __pointerProbe.click++; });
                </` + `script></body></html>`;
                iframe.src = URL.createObjectURL(new Blob([markup], { type: 'text/html' }));
                window.__pointerFrame = iframe;
                """, "v8-iframe-pointer-owner.js");

            var started = Stopwatch.StartNew();
            VirtualIframeDomDocument? frameDocument = null;
            AvaloniaDomElement? button = null;
            while (started.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(4);
                Dispatcher.UIThread.RunJobs();
                var iframe = host.Document.querySelector("#pointer-frame") as AvaloniaDomElement;
                frameDocument = iframe?.contentDocument as VirtualIframeDomDocument;
                button = frameDocument?.querySelector("#hit-button") as AvaloniaDomElement;
                if (button is not null && button.Control.Bounds.Width > 0 && button.Control.Bounds.Height > 0)
                {
                    break;
                }
            }

            if (frameDocument is null || button is null)
            {
                Console.Error.WriteLine("V8 iframe pointer repro failed: frame/button did not initialize.");
                return 1;
            }

            using var renderedFrame = window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();

            var localCenter = new Point(button.Control.Bounds.Width / 2, button.Control.Bounds.Height / 2);
            var windowPoint = button.Control.TranslatePoint(localCenter, window);
            if (windowPoint is null)
            {
                Console.Error.WriteLine("V8 iframe pointer repro failed: button did not translate into the window.");
                return 1;
            }

            var hit = window.InputHitTest(windowPoint.Value) as Control;
            if (hit is not null)
            {
                RaiseMouseClick(hit, windowPoint.Value, window);
            }
            Dispatcher.UIThread.RunJobs();

            var buttonWasHit = ReferenceEquals(hit, button.Control);
            var down = Convert.ToInt32(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.down"));
            var click = Convert.ToInt32(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.click"));
            var up = Convert.ToInt32(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.up"));
            var mouseUp = Convert.ToInt32(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.mouseUp"));
            var lostCapture = Convert.ToInt32(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.lostCapture"));
            var domException = Convert.ToBoolean(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.domException"));
            var target = Convert.ToString(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.target"));
            var clientX = Convert.ToDouble(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.clientX"));
            var clientY = Convert.ToDouble(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.clientY"));
            var managedIdentity = Convert.ToBoolean(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.targetIdentity && " +
                "window.__pointerFrame.contentWindow.__pointerProbe.currentTargetIdentity"));
            var managedMarker = Convert.ToString(runtime.Engine.Evaluate(
                "window.__pointerFrame.contentWindow.__pointerProbe.managedMarker"));
            var hitElement = hit is null ? null : frameDocument.WrapControl(hit);
            var visualPath = DescribeVisualPath(button.Control);
            var visualsAtPoint = string.Join(" -> ", window.GetVisualsAt(windowPoint.Value)
                .OfType<Control>()
                .Select(control => $"{control.GetType().Name}[{control.Bounds}]"));
            var frameRoot = (Control)button.Control.GetVisualParent()!;
            var frameBody = (Control)frameRoot.GetVisualParent()!;
            var bodyPoint = button.Control.TranslatePoint(localCenter, frameBody)!.Value;
            var bodyVisualsAtPoint = string.Join(" -> ", frameBody.GetVisualsAt(bodyPoint)
                .OfType<Control>()
                .Select(control => $"{control.GetType().Name}[{control.Bounds}]"));
            var passed = buttonWasHit && down == 1 && up == 1 && mouseUp == 1
                         && lostCapture == 1 && click == 1 && managedIdentity && domException
                         && string.Equals(managedMarker, "v8-managed-button", StringComparison.Ordinal);
            Console.WriteLine(
                $"V8 virtual iframe native pointer: {(passed ? "pass" : "fail")}; " +
                $"point={windowPoint.Value}, hit={hit?.GetType().Name ?? "<null>"}/" +
                $"{hitElement?.tagName ?? "<none>"}#{hitElement?.id ?? ""}, " +
                $"button-bounds={button.Control.Bounds}, visible={button.Control.IsVisible}, " +
                $"hit-visible={button.Control.IsHitTestVisible}, opacity={button.Control.Opacity:F1}, " +
                $"background={(button.Control as Panel)?.Background?.GetType().Name ?? "<null>"}, " +
                $"parent={button.Control.Parent?.GetType().Name ?? "<null>"}, " +
                $"down/up/mouseup/lost={down}/{up}/{mouseUp}/{lostCapture}, click={click}, " +
                $"dom-exception={domException}, " +
                $"target={target}, client={clientX:F0},{clientY:F0}, " +
                $"managed-identity={managedIdentity}, marker={managedMarker}; visuals={visualsAtPoint}; " +
                $"body-point={bodyPoint}, body-visuals={bodyVisualsAtPoint}; path={visualPath}");
            return passed && string.Equals(target, "hit-button", StringComparison.Ordinal)
                ? 0
                : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"V8 iframe pointer repro failed: {exception}");
            return 1;
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string DescribeVisualPath(Control control)
    {
        var entries = new List<string>();
        for (Control? current = control; current is not null; current = current.GetVisualParent() as Control)
        {
            entries.Add(
                $"{current.GetType().Name}[{current.Bounds};visible={current.IsVisible};" +
                $"hit={current.IsHitTestVisible};clip={current.ClipToBounds};" +
                $"background={(current as Panel)?.Background?.GetType().Name ?? "<null>"};" +
                $"children={current.GetVisualChildren().Count()}]");
        }
        return string.Join(" <- ", entries);
    }

    private static void RaiseMouseClick(Control target, Point windowPoint, Window window)
    {
        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        target.RaiseEvent(new PointerPressedEventArgs(
            target,
            pointer,
            window,
            windowPoint,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(
            target,
            pointer,
            window,
            windowPoint,
            1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left));
    }
}
