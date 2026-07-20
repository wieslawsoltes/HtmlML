using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class DomCheckableInputRasterTests
{
    [AvaloniaFact]
    public void CustomCheckablesPreserveGeometryPaintHitAndState()
    {
        var root = new CssLayoutPanel { Width = 160, Height = 60 };
        var window = new Window
        {
            Width = 160,
            Height = 60,
            Background = Brush.Parse("#131722"),
            Content = root
        };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body {
                width: 160px;
                height: 60px;
                margin: 0;
                background: #131722;
            }
            .control {
                display: block;
                position: absolute;
                top: 10px;
                width: 18px;
                height: 18px;
            }
            .radio-one { left: 10px; }
            .radio-two { left: 40px; }
            .checkbox-control { left: 70px; }
            .switch-control { left: 105px; width: 38px; height: 20px; }
            .native-input {
                cursor: default;
                height: 100%;
                left: 0;
                margin: 0;
                opacity: 0;
                padding: 0;
                position: absolute;
                top: 0;
                width: 100%;
                z-index: 2;
            }
            .radio-view {
                background: transparent;
                border: 1px solid #f2f2f2;
                border-radius: 50%;
                box-sizing: border-box;
                display: block;
                height: 18px;
                left: 0;
                position: absolute;
                top: 0;
                width: 18px;
            }
            .radio-dot {
                background: #131722;
                border-radius: 50%;
                display: none;
                height: 6px;
                left: 50%;
                position: absolute;
                top: 50%;
                transform: translate(-50%, -50%);
                width: 6px;
            }
            .native-input:checked + .radio-view { background: #f2f2f2; }
            .native-input:checked + .radio-view .radio-dot { display: block; }
            .checkbox-view {
                background: transparent;
                border: 1px solid #f2f2f2;
                border-radius: 3px;
                box-sizing: border-box;
                display: block;
                height: 18px;
                left: 0;
                position: absolute;
                top: 0;
                width: 18px;
            }
            .check-mark {
                background: #131722;
                display: none;
                height: 4px;
                left: 5px;
                position: absolute;
                top: 7px;
                width: 8px;
            }
            .native-input:checked + .checkbox-view { background: #f2f2f2; }
            .native-input:checked + .checkbox-view .check-mark { display: block; }
            .switch-view {
                background: #6a6d78;
                border: 1px solid #6a6d78;
                border-radius: 20px;
                box-sizing: border-box;
                display: block;
                height: 20px;
                left: 0;
                position: absolute;
                top: 0;
                width: 38px;
            }
            .switch-thumb {
                background: #f2f2f2;
                border-radius: 50%;
                display: block;
                height: 14px;
                left: 0;
                position: absolute;
                top: 0;
                transform: translate(3px, 2px);
                width: 14px;
            }
            .native-input:checked + .switch-view { background: #f2f2f2; border-color: #f2f2f2; }
            .native-input:checked + .switch-view .switch-thumb {
                background: #131722;
                transform: translate(20px, 2px);
            }
            """;
        document.head.appendChild(style);

        var firstRadio = AppendControl(document, "radio-one", "radio", "source", "radio-view", "radio-dot");
        var secondRadio = AppendControl(document, "radio-two", "radio", "source", "radio-view", "radio-dot");
        var checkbox = AppendControl(document, "checkbox-control", "checkbox", null, "checkbox-view", "check-mark");
        var toggle = AppendControl(document, "switch-control", "checkbox", null, "switch-view", "switch-thumb");
        firstRadio.Input.@checked = true;

        window.Show();
        Flush(document);

        AssertControlGeometryAndHit(document, firstRadio, new Rect(10, 10, 18, 18));
        AssertControlGeometryAndHit(document, secondRadio, new Rect(40, 10, 18, 18));
        AssertControlGeometryAndHit(document, checkbox, new Rect(70, 10, 18, 18));
        AssertControlGeometryAndHit(document, toggle, new Rect(105, 10, 38, 20));

        using (var initial = Capture(root))
        {
            Assert.Equal(Color.Parse("#f2f2f2"), ReadPixel(initial, 14, 19));
            Assert.Equal(Color.Parse("#131722"), ReadPixel(initial, 19, 19));
            Assert.Equal(Color.Parse("#131722"), ReadPixel(initial, 44, 19));
            Assert.Equal(Color.Parse("#131722"), ReadPixel(initial, 73, 19));
            Assert.Equal(Color.Parse("#f2f2f2"), ReadPixel(initial, 111, 20));
            Assert.Equal(Color.Parse("#6a6d78"), ReadPixel(initial, 136, 20));
        }

        RaiseNativeClick(secondRadio.Input, window);
        RaiseNativeClick(checkbox.Input, window);
        RaiseNativeClick(toggle.Input, window);
        Flush(document);

        Assert.False(firstRadio.Input.@checked);
        Assert.True(secondRadio.Input.@checked);
        Assert.True(checkbox.Input.@checked);
        Assert.True(toggle.Input.@checked);
        using (var activated = Capture(root))
        {
            Assert.Equal(Color.Parse("#131722"), ReadPixel(activated, 14, 19));
            Assert.Equal(Color.Parse("#f2f2f2"), ReadPixel(activated, 44, 19));
            Assert.Equal(Color.Parse("#131722"), ReadPixel(activated, 49, 19));
            Assert.Equal(Color.Parse("#f2f2f2"), ReadPixel(activated, 73, 19));
            Assert.Equal(Color.Parse("#131722"), ReadPixel(activated, 79, 19));
            Assert.Equal(Color.Parse("#f2f2f2"), ReadPixel(activated, 111, 20));
            Assert.Equal(Color.Parse("#131722"), ReadPixel(activated, 131, 20));
        }

        RaiseNativeClick(checkbox.Input, window);
        RaiseNativeClick(toggle.Input, window);
        Flush(document);
        Assert.False(checkbox.Input.@checked);
        Assert.False(toggle.Input.@checked);
        using (var restored = Capture(root))
        {
            Assert.Equal(Color.Parse("#131722"), ReadPixel(restored, 73, 19));
            Assert.Equal(Color.Parse("#f2f2f2"), ReadPixel(restored, 111, 20));
            Assert.Equal(Color.Parse("#6a6d78"), ReadPixel(restored, 136, 20));
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static CheckableControl AppendControl(
        AvaloniaDomDocument document,
        string positionClass,
        string type,
        string? name,
        string viewClass,
        string? childClass = null)
    {
        var label = HostTestUtilities.GetElement(document.createElement("label"));
        label.className = $"control {positionClass}";
        var input = HostTestUtilities.GetElement(document.createElement("input"));
        input.className = "native-input";
        input.type = type;
        if (name is not null) input.name = name;
        var view = HostTestUtilities.GetElement(document.createElement("span"));
        view.className = viewClass;
        if (childClass is not null)
        {
            var child = HostTestUtilities.GetElement(document.createElement("span"));
            child.className = childClass;
            view.appendChild(child);
        }
        label.appendChild(input);
        label.appendChild(view);
        HostTestUtilities.GetElement(document.body).appendChild(label);
        return new CheckableControl(input, view);
    }

    private static void AssertControlGeometryAndHit(
        AvaloniaDomDocument document,
        CheckableControl control,
        Rect expected)
    {
        var inputRect = control.Input.getBoundingClientRect();
        var viewRect = control.View.getBoundingClientRect();
        Assert.Equal(expected, new Rect(inputRect.left, inputRect.top, inputRect.width, inputRect.height));
        Assert.Equal(expected, new Rect(viewRect.left, viewRect.top, viewRect.width, viewRect.height));
        Assert.Equal(0, control.Input.Control.Opacity);
        Assert.Same(
            control.Input,
            document.elementFromPoint(expected.Center.X, expected.Center.Y));
    }

    private static void RaiseNativeClick(AvaloniaDomElement input, Window window)
    {
        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var point = new Point(input.Control.Bounds.Width / 2, input.Control.Bounds.Height / 2);
        input.Control.RaiseEvent(new PointerPressedEventArgs(
            input.Control,
            pointer,
            window,
            point,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        input.Control.RaiseEvent(new PointerReleasedEventArgs(
            input.Control,
            pointer,
            window,
            point,
            1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left));
    }

    private static void Flush(AvaloniaDomDocument document)
    {
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();
        document.FlushPendingLayout();
    }

    private static RenderTargetBitmap Capture(CssLayoutPanel root)
    {
        var frame = new RenderTargetBitmap(new PixelSize(160, 60), new Vector(96, 96));
        frame.Render(root);
        return frame;
    }

    private static Color ReadPixel(Bitmap bitmap, int x, int y)
    {
        var bytes = new byte[4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }

        return bitmap.Format == PixelFormat.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    private sealed record CheckableControl(AvaloniaDomElement Input, AvaloniaDomElement View);
}
