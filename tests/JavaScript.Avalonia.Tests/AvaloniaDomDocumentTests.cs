using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Jint.Native;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public class AvaloniaDomDocumentTests
{
    [AvaloniaFact]
    public void Body_ReturnsRootElement()
    {
        var panel = new StackPanel();
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var body = HostTestUtilities.GetElement(host.Document.body);

        Assert.Same(panel, body.Control);
    }

    [AvaloniaFact]
    public void GetElementById_ReturnsMatchingControl()
    {
        var panel = new StackPanel();
        var button = new Button { Name = "target" };
        panel.Children.Add(button);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var result = HostTestUtilities.GetElement(host.Document.getElementById("target"));

        Assert.Same(button, result.Control);
    }

    [AvaloniaFact]
    public void QuerySelector_SupportsIdClassAndTag()
    {
        var panel = new StackPanel();
        var button = new Button { Name = "action" };
        button.Classes.Add("primary");
        var text = new TextBlock();
        panel.Children.Add(button);
        panel.Children.Add(text);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var byId = HostTestUtilities.GetElement(host.Document.querySelector("#action"));
        var byClass = HostTestUtilities.GetElement(host.Document.querySelector(".primary"));
        var byTag = HostTestUtilities.GetElement(host.Document.querySelector("textblock"));

        Assert.Same(button, byId.Control);
        Assert.Same(button, byClass.Control);
        Assert.Same(text, byTag.Control);
    }

    [AvaloniaFact]
    public void QuerySelectorAll_ReturnsAllMatches()
    {
        var panel = new StackPanel();
        var first = new TextBlock { Name = "first" };
        first.Classes.Add("item");
        var second = new TextBlock { Name = "second" };
        second.Classes.Add("item");
        panel.Children.Add(first);
        panel.Children.Add(second);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var matches = host.Document.querySelectorAll(".item");

        Assert.Equal(2, matches.Length);
        Assert.Same(first, HostTestUtilities.GetElement(matches[0]).Control);
        Assert.Same(second, HostTestUtilities.GetElement(matches[1]).Control);
    }

    [AvaloniaFact]
    public void CreateElement_ResolvesAvaloniaControlType()
    {
        var (host, _) = HostTestUtilities.CreateHost();

        var element = HostTestUtilities.GetElement(host.Document.createElement("stack-panel"));

        Assert.IsType<StackPanel>(element.Control);
    }

    [AvaloniaFact]
    public void CreateElement_ReturnsNullForUnknownTag()
    {
        var (host, _) = HostTestUtilities.CreateHost();

        Assert.Null(host.Document.createElement("unknown-tag"));
    }

    [AvaloniaFact]
    public void AppendChild_AddsToPanel()
    {
        var panel = new StackPanel();
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var body = HostTestUtilities.GetElement(host.Document.body);
        var child = HostTestUtilities.GetElement(host.Document.createElement("TextBlock"));
        var appended = body.appendChild(child);

        Assert.Same(child, appended);
        Assert.Contains(child.Control, panel.Children);
    }

    [AvaloniaFact]
    public void AppendChild_SetsContentForContentControl()
    {
        var root = new ContentControl();
        var (host, _) = HostTestUtilities.CreateHost(root);

        var body = HostTestUtilities.GetElement(host.Document.body);
        var child = HostTestUtilities.GetElement(host.Document.createElement("Border"));
        body.appendChild(child);

        Assert.Same(child.Control, root.Content);
    }

    [AvaloniaFact]
    public void Remove_DetachesControlFromParent()
    {
        var panel = new StackPanel();
        var childControl = new Border();
        panel.Children.Add(childControl);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var child = HostTestUtilities.GetElement(host.Document.querySelector("Border"));
        child.remove();

        Assert.DoesNotContain(childControl, panel.Children);
    }

    [AvaloniaFact]
    public void GetAndSetAttribute_ManageStandardAttributes()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var element = HostTestUtilities.GetElement(host.Document.createElement("Button"));

        element.setAttribute("id", "my-button");
        element.setAttribute("class", "primary secondary");
        element.setAttribute("title", "Tooltip");
        element.setAttribute("content", "Click Me");

        var button = Assert.IsType<Button>(element.Control);
        Assert.Equal("my-button", button.Name);
        Assert.Contains("primary", button.Classes);
        Assert.Contains("secondary", button.Classes);
        Assert.Equal("Click Me", button.Content);
        Assert.Equal("Tooltip", ToolTip.GetTip(button)?.ToString());
        Assert.Equal("my-button", element.getAttribute("id"));
        Assert.Equal("primary secondary", element.getAttribute("class"));
        Assert.Equal("Tooltip", element.getAttribute("title"));
        Assert.Equal("Click Me", element.getAttribute("content"));
    }

    [AvaloniaFact]
    public void SetAttribute_ConvertsNumericValues()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var element = HostTestUtilities.GetElement(host.Document.createElement("Border"));

        element.setAttribute("width", "123.5");
        element.setAttribute("height", "42");

        var border = Assert.IsType<Border>(element.Control);
        Assert.Equal(123.5, border.Width);
        Assert.Equal(42, border.Height);
        Assert.Equal(border.Width.ToString(CultureInfo.CurrentCulture), element.getAttribute("width"));
        Assert.Equal(border.Height.ToString(CultureInfo.CurrentCulture), element.getAttribute("height"));
    }

    [AvaloniaFact]
    public void ClassList_ModifiesClasses()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var element = HostTestUtilities.GetElement(host.Document.createElement("Border"));

        element.classListAdd("one");
        element.classListAdd("two");
        element.classListRemove("one");
        element.classListToggle("two");
        element.classListToggle("three");

        var border = Assert.IsType<Border>(element.Control);
        Assert.DoesNotContain("one", border.Classes);
        Assert.DoesNotContain("two", border.Classes);
        Assert.Contains("three", border.Classes);
    }

    [AvaloniaFact]
    public void TextContent_ReadsAndWritesTextBlock()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var element = HostTestUtilities.GetElement(host.Document.createElement("TextBlock"));

        element.textContent = "Hello";

        Assert.Equal("Hello", element.textContent);
        Assert.Equal("Hello", Assert.IsType<TextBlock>(element.Control).Text);
    }

    [AvaloniaFact]
    public void AddEventListener_WiresClickEvent()
    {
        var panel = new StackPanel();
        var button = new Button();
        panel.Children.Add(button);
        var (host, _) = HostTestUtilities.CreateHost(panel);
        var element = HostTestUtilities.GetElement(host.Document.querySelector("Button"));

        var count = 0;
        var callback = JsValue.FromObject(host.Engine, new Action(() => count++));

        element.addEventListener("click", callback);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, count);

        element.removeEventListener("click", callback);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, count);
    }

    [AvaloniaFact]
    public void PointerEvents_PassThroughEventInfo()
    {
        var panel = new StackPanel();
        var border = new Border();
        panel.Children.Add(border);
        var (host, _) = HostTestUtilities.CreateHost(panel);
        var element = HostTestUtilities.GetElement(host.Document.querySelector("Border"));

        var handled = false;
        var callback = JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var info = Assert.IsType<AvaloniaDomElement.PointerEventInfo>(arg);
            Assert.Equal("LeftButtonPressed", info.button);
            info.handled = true;
            handled = true;
        }));

        element.addEventListener("pointerdown", callback);

        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var args = new PointerPressedEventArgs(
            border,
            pointer,
            border,
            new Point(5, 6),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);

        border.RaiseEvent(args);

        Assert.True(handled);
        Assert.True(args.Handled);

        element.removeEventListener("pointerdown", callback);
    }

    [AvaloniaFact]
    public void KeyEvents_SetHandledFlagWhenRequested()
    {
        var panel = new StackPanel();
        var textBox = new TextBox();
        panel.Children.Add(textBox);
        var (host, _) = HostTestUtilities.CreateHost(panel);
        var element = HostTestUtilities.GetElement(host.Document.querySelector("TextBox"));

        var handled = false;
        var callback = JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var info = Assert.IsType<AvaloniaDomElement.KeyEventInfo>(arg);
            Assert.Equal("A", info.key);
            info.handled = true;
            handled = true;
        }));

        element.addEventListener("keydown", callback);

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = textBox,
            Key = Key.A
        };

        textBox.RaiseEvent(args);

        Assert.True(handled);
        Assert.True(args.Handled);

        element.removeEventListener("keydown", callback);
    }

    [AvaloniaFact]
    public void TextInputEvents_ForwardTextValue()
    {
        var panel = new StackPanel();
        var textBox = new TextBox();
        panel.Children.Add(textBox);
        var (host, _) = HostTestUtilities.CreateHost(panel);
        var element = HostTestUtilities.GetElement(host.Document.querySelector("TextBox"));

        string? observed = null;
        var callback = JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var info = Assert.IsType<AvaloniaDomElement.TextInputEventInfo>(arg);
            observed = info.text;
            info.handled = true;
        }));

        var args = new TextInputEventArgs
        {
            Text = "abc"
        };

        var method = typeof(AvaloniaDomElement).GetMethod("HandleTextInputEvent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        method!.Invoke(element, new object[] { callback, args });

        Assert.Equal("abc", observed);
        Assert.True(args.Handled);
    }
}
