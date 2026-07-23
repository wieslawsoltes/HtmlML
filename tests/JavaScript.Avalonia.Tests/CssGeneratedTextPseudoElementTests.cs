using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssGeneratedTextPseudoElementTests
{
    [AvaloniaFact]
    public void LiteralBlockBeforeAddsAnIntrinsicPaintedLineInNativeAndPortableLayout()
    {
        var root = new CssLayoutPanel { Width = 200, Height = 100 };
        CssLayout.SetNativeLayoutHotPath(root, true);
        var window = new Window { Width = 200, Height = 100, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { margin: 0; }
            #generated { width: 100px; }
            #generated::before { content: "Filler text"; display: block; }
            """;
        document.head.appendChild(style);
        var generated = Append(document, "generated");
        generated.textContent = "Filler text";

        try
        {
            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.IsType<CssLayoutPanel>(generated.Control);
            var authoredLine = Assert.Single(panel.Children.OfType<TextBlock>());
            var lineHeight = authoredLine.DesiredSize.Height;
            var pseudo = Assert.IsType<CssGeneratedPseudoElement>(panel.BeforePseudoElement);
            var intrinsic = pseudo.ResolveFlowSize(100, 100);
            Assert.True(intrinsic.Width > 0);
            Assert.Equal(authoredLine.DesiredSize.Width, intrinsic.Width, 6);
            Assert.Equal(lineHeight, intrinsic.Height, 6);
            Assert.Equal(2 * lineHeight, generated.getBoundingClientRect().height, 6);

            var body = Assert.IsType<CssLayoutPanel>(HostTestUtilities.GetElement(document.body).Control);
            var portable = AvaloniaCssLayoutProjection.Capture(body, new Size(200, 100));
            Assert.Equal(2 * lineHeight, portable.GetBox(panel).BorderBox.Height, 6);

            using var frame = Assert.IsAssignableFrom<Bitmap>(window.CaptureRenderedFrame());
            Assert.True(CountDarkPixels(frame, new PixelRect(0, 0, 100, (int)Math.Ceiling(lineHeight))) > 0);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void LiteralBeforeIsAnIntrinsicCenteredFlexItemInNativeAndPortableLayout()
    {
        var root = new CssLayoutPanel { Width = 300, Height = 50 };
        CssLayout.SetNativeLayoutHotPath(root, true);
        var window = new Window { Width = 300, Height = 50, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { height: 50px; margin: 0; width: 300px; }
            #flex {
              align-items: flex-end;
              display: flex;
              height: 50px;
              justify-content: space-between;
              width: 300px;
            }
            #flex::before { align-self: center; background: yellow; content: 'b'; }
            """;
        document.head.appendChild(style);
        var flex = Append(document, "flex");
        flex.textContent = "x";

        try
        {
            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.IsType<CssLayoutPanel>(flex.Control);
            var pseudo = Assert.IsType<CssGeneratedPseudoElement>(panel.BeforePseudoElement);
            Assert.True(pseudo.TryResolvePaintRect(
                new Rect(panel.Bounds.Size),
                new Rect(panel.Bounds.Size),
                before: true,
                out var native));
            Assert.True(native.Width > 0);
            Assert.True(native.Height > 0);
            Assert.Equal((50 - native.Height) / 2, native.Y, 6);

            var body = Assert.IsType<CssLayoutPanel>(HostTestUtilities.GetElement(document.body).Control);
            var portable = AvaloniaCssLayoutProjection.Capture(body, new Size(300, 50));
            Assert.True(portable.TryGetPseudoBox(panel, before: true, out var portablePseudo));
            Assert.Equal(native.X, portablePseudo.BorderBox.X, 6);
            Assert.Equal(native.Y, portablePseudo.BorderBox.Y, 6);
            Assert.Equal(native.Width, portablePseudo.BorderBox.Width, 6);
            Assert.Equal(native.Height, portablePseudo.BorderBox.Height, 6);

            using var frame = Assert.IsAssignableFrom<Bitmap>(window.CaptureRenderedFrame());
            Assert.True(CountDarkPixels(frame, new PixelRect(
                (int)Math.Floor(native.X),
                (int)Math.Floor(native.Y),
                Math.Max(1, (int)Math.Ceiling(native.Width)),
                Math.Max(1, (int)Math.Ceiling(native.Height)))) > 0);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void InlineGeneratedWhitespaceContributesAdvanceBeforeFollowingFragment()
    {
        var root = new CssLayoutPanel { Width = 500, Height = 60 };
        var window = new Window { Width = 500, Height = 60, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { margin: 0; }
            .item { display: inline; }
            #status > .item:not(:last-child)::after { content: " "; }
            #reference { position: absolute; visibility: hidden; }
            """;
        document.head.appendChild(style);
        var status = Append(document, "p", "status");
        var first = Append(document, "span", "first", status);
        first.textContent = "All's well — market is open.";
        var second = Append(document, "span", "second", status);
        second.className = "item";
        second.textContent = "It'll close soon.";
        var reference = Append(document, "span", "reference");
        reference.className = "item";
        reference.textContent = "All's well — market is open.";

        try
        {
            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var initialFirst = first.getBoundingClientRect();
            var initialReference = reference.getBoundingClientRect();
            Assert.InRange(
                Math.Abs(initialFirst.width - initialReference.width),
                0,
                1);

            first.className = "item";
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var firstRect = first.getBoundingClientRect();
            var secondRect = second.getBoundingClientRect();
            var referenceRect = reference.getBoundingClientRect();
            var pseudo = Assert.IsType<CssGeneratedPseudoElement>(
                Assert.IsType<CssLayoutPanel>(first.Control).AfterPseudoElement);
            Assert.True(pseudo.IntrinsicSize.Width >= 2);
            Assert.True(
                firstRect.width - referenceRect.width >= 2,
                $"first={firstRect.width}; reference={referenceRect.width}; pseudo={pseudo.IntrinsicSize.Width}");
            Assert.Equal(firstRect.right, secondRect.left, 6);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static AvaloniaDomElement Append(AvaloniaDomDocument document, string id)
    {
        return Append(document, "div", id);
    }

    private static AvaloniaDomElement Append(
        AvaloniaDomDocument document,
        string tag,
        string id,
        AvaloniaDomElement? parent = null)
    {
        var element = HostTestUtilities.GetElement(document.createElement(tag));
        element.id = id;
        (parent ?? HostTestUtilities.GetElement(document.body)).appendChild(element);
        return element;
    }

    private static int CountDarkPixels(Bitmap bitmap, PixelRect rect)
    {
        var bytes = new byte[rect.Width * rect.Height * 4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(rect, handle.AddrOfPinnedObject(), bytes.Length, rect.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        var count = 0;
        for (var index = 0; index < bytes.Length; index += 4)
        {
            var first = bytes[index];
            var second = bytes[index + 1];
            var third = bytes[index + 2];
            if (first < 96 && second < 96 && third < 96)
            {
                count++;
            }
        }
        return count;
    }
}
