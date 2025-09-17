using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media;
using Jint.Native;
using Jint.Runtime;
using JavaScript.Avalonia;
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
    public void CreateTextNode_ProducesTextBlockWrapper()
    {
        var (host, _) = HostTestUtilities.CreateHost();

        var node = Assert.IsType<AvaloniaDomTextNode>(host.Document.createTextNode("Hello"));

        Assert.Equal("Hello", node.data);
        Assert.Equal("Hello", node.textContent);
        Assert.IsType<TextBlock>(node.Control);
    }

    [AvaloniaFact]
    public void CreateTextNode_AppendsToContainer()
    {
        var panel = new StackPanel();
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var body = HostTestUtilities.GetElement(host.Document.body);
        var node = Assert.IsType<AvaloniaDomTextNode>(host.Document.createTextNode("Sample"));

        var appended = body.appendChild(node);

        Assert.Same(node, appended);
        var textBlock = Assert.IsType<TextBlock>(node.Control);
        Assert.Contains(textBlock, panel.Children);
        Assert.Equal("Sample", textBlock.Text);
    }

    [AvaloniaFact]
    public void CreateTextNode_PreservesLiteralCharacters()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        const string literal = "<StackPanel /> & text";

        var node = Assert.IsType<AvaloniaDomTextNode>(host.Document.createTextNode(literal));

        Assert.Equal(literal, node.data);
        Assert.Equal(literal, ((TextBlock)node.Control).Text);
    }

    [AvaloniaFact]
    public void InsertBefore_InsertsAtCorrectPosition()
    {
        var panel = new StackPanel();
        var first = new TextBlock { Name = "first" };
        var second = new TextBlock { Name = "second" };
        panel.Children.Add(first);
        panel.Children.Add(second);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var container = HostTestUtilities.GetElement(host.Document.body);
        var newChild = HostTestUtilities.GetElement(host.Document.createElement("TextBlock"));
        var reference = HostTestUtilities.GetElement(host.Document.getElementById("second"));

        var inserted = container.insertBefore(newChild, reference);

        Assert.Same(newChild, inserted);
        Assert.Equal(3, panel.Children.Count);
        Assert.Same(first, panel.Children[0]);
        Assert.Same(newChild.Control, panel.Children[1]);
        Assert.Same(second, panel.Children[2]);
    }

    [AvaloniaFact]
    public void InsertBefore_AppendsWhenReferenceIsNull()
    {
        var panel = new StackPanel();
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var container = HostTestUtilities.GetElement(host.Document.body);
        var first = HostTestUtilities.GetElement(host.Document.createElement("TextBlock"));
        var second = HostTestUtilities.GetElement(host.Document.createElement("TextBlock"));

        container.insertBefore(first, null);
        container.insertBefore(second, null);

        Assert.Equal(2, panel.Children.Count);
        Assert.Same(first.Control, panel.Children[0]);
        Assert.Same(second.Control, panel.Children[1]);
    }

    [AvaloniaFact]
    public void RemoveChild_RemovesFromVisualTree()
    {
        var panel = new StackPanel();
        var childControl = new TextBlock();
        panel.Children.Add(childControl);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var container = HostTestUtilities.GetElement(host.Document.body);
        var child = HostTestUtilities.GetElement(host.Document.querySelector("TextBlock"));

        var removed = container.removeChild(child);

        Assert.Same(child, removed);
        Assert.Empty(panel.Children);
    }

    [AvaloniaFact]
    public void ReplaceChild_ReplacesExistingChild()
    {
        var panel = new StackPanel();
        var oldControl = new TextBlock { Name = "old" };
        panel.Children.Add(oldControl);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var container = HostTestUtilities.GetElement(host.Document.body);
        var oldChild = HostTestUtilities.GetElement(host.Document.getElementById("old"));
        var newChild = HostTestUtilities.GetElement(host.Document.createElement("Border"));

        var result = container.replaceChild(newChild, oldChild);

        Assert.Same(oldChild, result);
        Assert.Single(panel.Children);
        Assert.Same(newChild.Control, panel.Children[0]);
        Assert.DoesNotContain(oldControl, panel.Children);
    }

    [AvaloniaFact]
    public void AppendChild_ReparentsExistingChild()
    {
        var root = new StackPanel();
        var source = new StackPanel { Name = "source" };
        var target = new StackPanel { Name = "target" };
        var moved = new TextBlock { Name = "moved" };
        source.Children.Add(moved);
        root.Children.Add(source);
        root.Children.Add(target);
        var (host, _) = HostTestUtilities.CreateHost(root);

        var targetElement = HostTestUtilities.GetElement(host.Document.getElementById("target"));
        var child = HostTestUtilities.GetElement(host.Document.getElementById("moved"));

        var appended = targetElement.appendChild(child);

        Assert.Same(child, appended);
        Assert.Single(target.Children);
        Assert.Empty(source.Children);
        Assert.Same(moved, target.Children[0]);
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
    public void SetAttribute_ParsesBrushAndThickness()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var element = HostTestUtilities.GetElement(host.Document.createElement("Border"));

        element.setAttribute("background", "#ff0000");
        element.setAttribute("padding", "4,8");
        element.setAttribute("borderBrush", "#0000ff");

        var border = Assert.IsType<Border>(element.Control);
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(border.Background);
        Assert.Equal(Colors.Red, brush.Color);
        Assert.Equal(new Thickness(4, 8, 4, 8), border.Padding);
        Assert.IsAssignableFrom<ISolidColorBrush>(border.BorderBrush);
    }

    [AvaloniaFact]
    public void StyleSetProperty_UsesAvaloniaConversion()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var element = HostTestUtilities.GetElement(host.Document.createElement("Border"));

        element.style.setProperty("background", "#00ff00");

        var border = Assert.IsType<Border>(element.Control);
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(border.Background);
        Assert.Equal(Colors.Lime, brush.Color);
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
            var info = Assert.IsType<DomPointerEvent>(arg);
            Assert.Equal(0, info.button);
            Assert.Equal("mouse", info.pointerType);
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
            var info = Assert.IsType<DomKeyboardEvent>(arg);
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
            var info = Assert.IsType<DomTextInputEvent>(arg);
            observed = info.data;
            info.handled = true;
        }));

        element.addEventListener("textinput", callback);

        var args = new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Source = textBox,
            Text = "abc"
        };

        textBox.RaiseEvent(args);

        Assert.Equal("abc", observed);
        Assert.True(args.Handled);

        element.removeEventListener("textinput", callback);
    }

    [AvaloniaFact]
    public async Task DocumentReadyState_TransitionsAndEvent()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = JsValue.FromObject(host.Engine, new Action(() => tcs.TrySetResult(true)));
        host.Document.addEventListener("DOMContentLoaded", callback);

        Assert.Equal("loading", host.Document.readyState);

        Dispatcher.UIThread.RunJobs();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("complete", host.Document.readyState);
        host.Document.removeEventListener("DOMContentLoaded", callback);
    }

    [AvaloniaFact]
    public void FormsImagesLinks_ReturnExpectedElements()
    {
        var panel = new StackPanel();
        panel.Children.Add(new SampleForm());
        panel.Children.Add(new Image());
        panel.Children.Add(new HyperlinkButton());
        var (host, _) = HostTestUtilities.CreateHost(panel);

        Assert.Single(host.Document.forms);
        Assert.Single(host.Document.images);
        Assert.Single(host.Document.links);
    }

    [AvaloniaFact]
    public void GetElementsByClassNameAndTagName_ReturnsMatches()
    {
        var panel = new StackPanel();
        var first = new TextBlock();
        first.Classes.Add("tagged");
        var second = new TextBlock();
        second.Classes.Add("tagged");
        panel.Children.Add(first);
        panel.Children.Add(second);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var byClass = host.Document.getElementsByClassName("tagged");
        var byTag = host.Document.getElementsByTagName("TextBlock");

        Assert.Equal(2, byClass.Length);
        Assert.Equal(2, byTag.Length);
    }

    [AvaloniaFact]
    public void Dataset_AllowsPropertyAccess()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var element = HostTestUtilities.GetElement(host.Document.createElement("Border"));

        element.setAttribute("data-user-id", "42");
        element.dataset.set("statusFlag", "active");

        host.Engine.SetValue("el", element);
        host.Engine.Execute("var datasetUser = el.dataset.userId; var datasetStatus = el.dataset.statusFlag;");

        Assert.Equal("42", element.getAttribute("data-user-id"));
        Assert.Equal("active", element.getAttribute("data-status-flag"));
        Assert.Equal("42", Convert.ToString(host.Engine.GetValue("datasetUser").ToObject(), CultureInfo.InvariantCulture));
        Assert.Equal("active", Convert.ToString(host.Engine.GetValue("datasetStatus").ToObject(), CultureInfo.InvariantCulture));

        Assert.True(element.dataset.delete("statusFlag"));
        Assert.Null(element.getAttribute("data-status-flag"));
    }

    [AvaloniaFact]
    public void StyleManipulation_SetsControlProperties()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var element = HostTestUtilities.GetElement(host.Document.createElement("Border"));
        var border = Assert.IsType<Border>(element.Control);

        host.Engine.SetValue("el", element);
        host.Engine.Execute("el.style.setProperty('width', '75'); el.style.height = '25';");

        Assert.Equal(75, border.Width);
        Assert.Equal(25, border.Height);

        element.setAttribute("style", "width: 100; height: 40;");
        Assert.Equal(100, border.Width);
        Assert.Equal(40, border.Height);
        Assert.Contains("width: 100", element.getAttribute("style"));
    }

    [AvaloniaFact]
    public void ClassList_SupportsMultipleTokens()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var element = HostTestUtilities.GetElement(host.Document.createElement("Border"));
        var border = Assert.IsType<Border>(element.Control);

        host.Engine.SetValue("el", element);
        host.Engine.Execute("el.classList.add('one', 'two'); el.classList.toggle('two'); el.classList.toggle('three', true);");

        Assert.Contains("one", border.Classes);
        Assert.DoesNotContain("two", border.Classes);
        Assert.Contains("three", border.Classes);
    }

    [AvaloniaFact]
    public void BoundingClientRect_ReflectsControlBounds()
    {
        var panel = new StackPanel();
        var border = new Border();
        panel.Children.Add(border);
        border.Measure(new Size(100, 50));
        border.Arrange(new Rect(5, 6, 100, 50));
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var element = HostTestUtilities.GetElement(host.Document.querySelector("Border"));
        var rect = Assert.IsType<DomRect>(element.getBoundingClientRect());

        Assert.Equal(100, rect.width);
        Assert.Equal(50, rect.height);
        Assert.Equal(5, element.offsetLeft);
        Assert.Equal(6, element.offsetTop);
    }

    [AvaloniaFact]
    public void ParentChildNavigation_ProvidesHierarchy()
    {
        var panel = new StackPanel();
        var border = new Border();
        panel.Children.Add(border);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var parent = HostTestUtilities.GetElement(host.Document.querySelector("StackPanel"));
        var child = HostTestUtilities.GetElement(host.Document.querySelector("Border"));

        Assert.Same(parent, child.parentElement);
        Assert.Equal(1, parent.childElementCount);
        Assert.Same(child, parent.firstElementChild);
        Assert.Null(child.previousElementSibling);
    }

    [AvaloniaFact]
    public void EventListenerOnce_RemovesHandlerAfterInvocation()
    {
        var panel = new StackPanel();
        var button = new Button();
        panel.Children.Add(button);
        var (host, _) = HostTestUtilities.CreateHost(panel);
        var element = HostTestUtilities.GetElement(host.Document.querySelector("Button"));

        var count = 0;
        var callback = JsValue.FromObject(host.Engine, new Action(() => count++));
        var options = JsValue.FromObject(host.Engine, new { once = true });
        element.addEventListener("click", callback, options);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, count);
    }

    [AvaloniaFact]
    public void StopPropagation_PreventsParentHandler()
    {
        var panel = new StackPanel();
        var border = new Border();
        panel.Children.Add(border);
        var (host, _) = HostTestUtilities.CreateHost(panel);
        var parentElement = HostTestUtilities.GetElement(host.Document.querySelector("StackPanel"));
        var childElement = HostTestUtilities.GetElement(host.Document.querySelector("Border"));

        var parentCalls = 0;
        var childCalls = 0;

        var parentCallback = JsValue.FromObject(host.Engine, new Action<object>(_ => parentCalls++));
        var childCallback = JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            childCalls++;
            var info = Assert.IsType<DomPointerEvent>(arg);
            info.stopPropagation();
        }));

        parentElement.addEventListener("pointerdown", parentCallback);
        childElement.addEventListener("pointerdown", childCallback);

        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var args = new PointerPressedEventArgs(
            border,
            pointer,
            border,
            new Point(0, 0),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);

        border.RaiseEvent(args);

        Assert.Equal(1, childCalls);
        Assert.Equal(0, parentCalls);

        childElement.removeEventListener("pointerdown", childCallback);
        parentElement.removeEventListener("pointerdown", parentCallback);
    }

    [AvaloniaFact]
    public void PointerEvent_CaptureAndBubbleOrder()
    {
        var parent = new StackPanel();
        var child = new Border();
        parent.Children.Add(child);
        var (host, _) = HostTestUtilities.CreateHost(parent);

        var order = new List<string>();

        var captureOptions = JsValue.FromObject(host.Engine, new { capture = true });

        host.Document.addEventListener("pointerdown", JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var evt = Assert.IsType<DomPointerEvent>(arg);
            order.Add($"doc:{evt.eventPhase}");
            Assert.Same(host.Document, evt.currentTarget);
            Assert.Equal(DomEventPhase.CapturingPhase, evt.eventPhase);
        })), captureOptions);

        host.Document.addEventListener("pointerdown", JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var evt = Assert.IsType<DomPointerEvent>(arg);
            order.Add($"doc:{evt.eventPhase}");
            Assert.Same(host.Document, evt.currentTarget);
            Assert.Equal(DomEventPhase.BubblingPhase, evt.eventPhase);
        })));

        var parentElement = HostTestUtilities.GetElement(host.Document.querySelector("StackPanel"));
        parentElement.addEventListener("pointerdown", JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var evt = Assert.IsType<DomPointerEvent>(arg);
            order.Add($"parent:{evt.eventPhase}");
            Assert.Same(parentElement, evt.currentTarget);
            Assert.Equal(DomEventPhase.CapturingPhase, evt.eventPhase);
        })), captureOptions);

        parentElement.addEventListener("pointerdown", JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var evt = Assert.IsType<DomPointerEvent>(arg);
            order.Add($"parent:{evt.eventPhase}");
            Assert.Same(parentElement, evt.currentTarget);
            Assert.Equal(DomEventPhase.BubblingPhase, evt.eventPhase);
        })));

        var childElement = HostTestUtilities.GetElement(host.Document.querySelector("Border"));
        childElement.addEventListener("pointerdown", JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var evt = Assert.IsType<DomPointerEvent>(arg);
            order.Add($"child:{evt.eventPhase}");
            Assert.Same(childElement, evt.currentTarget);
            Assert.Equal(childElement, evt.target);
            Assert.Equal(DomEventPhase.AtTarget, evt.eventPhase);
        })), captureOptions);

        childElement.addEventListener("pointerdown", JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var evt = Assert.IsType<DomPointerEvent>(arg);
            order.Add($"child:{evt.eventPhase}");
            Assert.Same(childElement, evt.currentTarget);
            Assert.Equal(childElement, evt.target);
            Assert.Equal(DomEventPhase.AtTarget, evt.eventPhase);
        })));

        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var args = new PointerPressedEventArgs(
            child,
            pointer,
            child,
            new Point(1, 1),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);

        child.RaiseEvent(args);

        Assert.Equal(new[]
        {
            "doc:CapturingPhase",
            "parent:CapturingPhase",
            "child:AtTarget",
            "child:AtTarget",
            "parent:BubblingPhase",
            "doc:BubblingPhase"
        }, order);
    }

    [AvaloniaFact]
    public void PassiveListener_PreventDefaultIgnored()
    {
        var border = new Border();
        var (host, _) = HostTestUtilities.CreateHost(border);
        var element = HostTestUtilities.GetElement(host.Document.querySelector("Border"));

        var passiveOptions = JsValue.FromObject(host.Engine, new { passive = true });
        var passiveDefaultPrevented = false;

        element.addEventListener("pointerdown", JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var evt = Assert.IsType<DomPointerEvent>(arg);
            evt.preventDefault();
            passiveDefaultPrevented = evt.defaultPrevented;
        })), passiveOptions);

        var bubblePrevented = false;
        element.addEventListener("pointerdown", JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var evt = Assert.IsType<DomPointerEvent>(arg);
            evt.preventDefault();
            bubblePrevented = evt.defaultPrevented;
        })));

        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var args = new PointerPressedEventArgs(
            border,
            pointer,
            border,
            new Point(0, 0),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);

        border.RaiseEvent(args);

        Assert.False(passiveDefaultPrevented);
        Assert.True(bubblePrevented);
        Assert.True(args.Handled);
    }

    [AvaloniaFact]
    public void StopImmediatePropagation_BlocksSubsequentListeners()
    {
        var border = new Border();
        var (host, _) = HostTestUtilities.CreateHost(border);
        var element = HostTestUtilities.GetElement(host.Document.querySelector("Border"));

        var firstCalled = false;
        var secondCalled = false;

        element.addEventListener("pointerdown", JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var evt = Assert.IsType<DomPointerEvent>(arg);
            firstCalled = true;
            evt.stopImmediatePropagation();
        })));

        element.addEventListener("pointerdown", JsValue.FromObject(host.Engine, new Action<object>(_ => secondCalled = true)));

        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var args = new PointerPressedEventArgs(
            border,
            pointer,
            border,
            new Point(0, 0),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);

        border.RaiseEvent(args);

        Assert.True(firstCalled);
        Assert.False(secondCalled);
        Assert.True(args.Handled);
    }

    [AvaloniaFact]
    public void DispatchEvent_CustomEventSupportsDetail()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var body = HostTestUtilities.GetElement(host.Document.body);
        var element = HostTestUtilities.GetElement(host.Document.createElement("Border"));
        body.appendChild(element);

        var detail = 0;
        var defaultPreventedAtListener = false;

        element.addEventListener("custom", JsValue.FromObject(host.Engine, new Action<object>(arg =>
        {
            var evt = Assert.IsType<DomSyntheticEvent>(arg);
            detail = Convert.ToInt32(evt.detail, CultureInfo.InvariantCulture);
            evt.preventDefault();
            defaultPreventedAtListener = evt.defaultPrevented;
            Assert.Same(element, evt.target);
        })));

        var eventValue = host.Engine.Evaluate("({ type: 'custom', bubbles: true, cancelable: true, detail: 42 })");
        var result = element.dispatchEvent(eventValue);

        Assert.False(result);
        Assert.Equal(42, detail);
        Assert.True(defaultPreventedAtListener);
        host.Engine.SetValue("evtInspect", eventValue);
        var preventedValue = host.Engine.Evaluate("evtInspect.defaultPrevented");
        Assert.True(TypeConverter.ToBoolean(preventedValue));
    }

    [AvaloniaFact]
    public void NodeMetadata_ExposesDomInformation()
    {
        var panel = new StackPanel();
        var text = new TextBlock { Text = "Hello" };
        panel.Children.Add(text);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        var body = HostTestUtilities.GetElement(host.Document.body);
        Assert.Equal(1, body.nodeType);
        Assert.Equal("BODY", body.nodeName);
        Assert.Same(host.Document, body.ownerDocument);
        Assert.True(body.hasChildNodes);

        var firstChild = body.firstChild;
        Assert.NotNull(firstChild);
        Assert.Equal(text, firstChild!.Control);

        var textNode = Assert.IsType<AvaloniaDomTextNode>(host.Document.createTextNode("Hi"));
        Assert.Equal(3, textNode.nodeType);
        Assert.Equal("#TEXT", textNode.nodeName);
        Assert.Equal("Hi", textNode.nodeValue);

        body.appendChild(textNode);
        Assert.Equal(textNode, body.lastChild);
        Assert.Contains(textNode, body.childNodes);
    }

    [AvaloniaFact]
    public void DocumentElement_HeadAndTitle()
    {
        var (host, window) = HostTestUtilities.CreateHost();

        Assert.Equal("HTML", host.Document.documentElement.nodeName);
        Assert.Equal("HEAD", host.Document.head.nodeName);
        Assert.Same(host.Document.documentElement, host.Document.head.parentElement);

        host.Document.title = "Sample";
        Assert.Equal("Sample", host.Document.title);
        Assert.Equal("Sample", window.Title);

        var script = HostTestUtilities.GetElement(host.Document.createElement("Border"));
        host.Document.head.appendChild(script);
        Assert.Contains(script, host.Document.head.childNodes);
    }

    [AvaloniaFact]
    public async Task Document_LoadAndUnload_EventsFire()
    {
        var (host, window) = HostTestUtilities.CreateHost();

        var loadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unloadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        host.Document.addEventListener("load", JsValue.FromObject(host.Engine, new Action(() => loadTcs.TrySetResult(true))));

        Dispatcher.UIThread.RunJobs();
        await loadTcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        host.Document.addEventListener("unload", JsValue.FromObject(host.Engine, new Action(() => unloadTcs.TrySetResult(true))));

        window.Close();
        Dispatcher.UIThread.RunJobs();
        await unloadTcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class SampleForm : Control
    {
    }
}
