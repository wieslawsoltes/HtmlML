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

[CollectionDefinition("Avalonia raster authority", DisableParallelization = true)]
public sealed class AvaloniaRasterAuthorityCollection
{
    public const string Name = "Avalonia raster authority";
}

[Collection(AvaloniaRasterAuthorityCollection.Name)]
public sealed class CssTextSpacingTests
{
    [AvaloniaFact]
    public void WordSpacingChangesIntrinsicInlineWidthAndInvalidatesAfterMutation()
    {
        var root = new CssLayoutPanel { Width = 400, Height = 80 };
        var window = new Window { Width = 400, Height = 80, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { background: white; margin: 0; }
            .row { display: block; height: 24px; }
            .measure { display: inline-block; font: 20px/24px Arial, sans-serif; white-space: nowrap; }
            #spaced { word-spacing: 6px; }
            """;
        document.head.appendChild(style);
        var plainRow = Append(document, "div", "plain-row");
        plainRow.className = "row";
        var plain = Append(document, "span", "plain", plainRow);
        plain.className = "measure";
        plain.textContent = "A B";
        var spacedRow = Append(document, "div", "spaced-row");
        spacedRow.className = "row";
        var spaced = Append(document, "span", "spaced", spacedRow);
        spaced.className = "measure";
        spaced.textContent = "A B";

        try
        {
            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var text = Assert.IsType<DomTextBlockControl>(
                Assert.IsType<AvaloniaDomTextNode>(spaced.firstChild).Control);
            Assert.Equal(6, text.WordSpacing, 6);
            Assert.Equal(6, spaced.getBoundingClientRect().width - plain.getBoundingClientRect().width, 6);

            spaced.style.setProperty("word-spacing", "10px");
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(10, text.WordSpacing, 6);
            Assert.Equal(10, spaced.getBoundingClientRect().width - plain.getBoundingClientRect().width, 6);

            using var frame = Assert.IsAssignableFrom<Bitmap>(window.CaptureRenderedFrame());
            var plainPaintWidth = DarkBoundsWidth(frame, new PixelRect(0, 0, 120, 24));
            var spacedPaintWidth = DarkBoundsWidth(frame, new PixelRect(0, 24, 120, 24));
            Assert.InRange(spacedPaintWidth - plainPaintWidth, 6, 12);

            var body = Assert.IsType<CssLayoutPanel>(HostTestUtilities.GetElement(document.body).Control);
            var portable = AvaloniaCssLayoutProjection.Capture(body, new Size(400, 80));
            Assert.Equal(
                10,
                portable.GetBox(spaced.Control).BorderBox.Width
                - portable.GetBox(plain.Control).BorderBox.Width,
                6);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void CollapsedInterElementTextNodeContributesOneInlineAdvance()
    {
        var root = new CssLayoutPanel { Width = 400, Height = 80 };
        var window = new Window { Width = 400, Height = 80, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { margin: 0; }
            .row { display: block; font: 20px/24px Arial, sans-serif; white-space: nowrap; }
            .measure { display: inline-block; }
            """;
        document.head.appendChild(style);
        var spaced = Append(document, "div", "spaced");
        spaced.className = "row";
        var left = Append(document, "span", "left", spaced);
        left.className = "measure";
        left.textContent = "left";
        var right = Append(document, "span", "right", spaced);
        right.className = "measure";
        right.textContent = "right";
        var whitespace = Assert.IsAssignableFrom<AvaloniaDomElement>(document.createTextNode(" "));
        spaced.insertBefore(whitespace, right);

        var joined = Append(document, "div", "joined");
        joined.className = "row";
        var joinedLeft = Append(document, "span", "joined-left", joined);
        joinedLeft.className = "measure";
        joinedLeft.textContent = "left";
        var joinedRight = Append(document, "span", "joined-right", joined);
        joinedRight.className = "measure";
        joinedRight.textContent = "right";

        try
        {
            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var directGap = right.getBoundingClientRect().left - left.getBoundingClientRect().right;
            Assert.InRange(directGap, 3, 8);
            Assert.Equal(
                0,
                joinedRight.getBoundingClientRect().left - joinedLeft.getBoundingClientRect().right,
                6);

            var body = Assert.IsType<CssLayoutPanel>(HostTestUtilities.GetElement(document.body).Control);
            var portable = AvaloniaCssLayoutProjection.Capture(body, new Size(400, 80));
            var portableGap = portable.GetBox(right.Control).BorderBox.Left
                              - portable.GetBox(left.Control).BorderBox.Right;
            Assert.InRange(portableGap, 3, 8);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ZeroFontSizeInterElementWhitespaceContributesNoAdvance()
    {
        var root = new CssLayoutPanel { Width = 120, Height = 40 };
        var window = new Window { Width = 120, Height = 40, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { margin: 0; }
            .row { display: block; font-size: 0; white-space: nowrap; }
            .box { display: inline-block; width: 4px; height: 4px; }
            """;
        document.head.appendChild(style);
        var row = Append(document, "div", "row");
        row.className = "row";
        var left = Append(document, "span", "left", row);
        left.className = "box";
        var right = Append(document, "span", "right", row);
        right.className = "box";
        var whitespace = Assert.IsType<AvaloniaDomTextNode>(document.createTextNode(" "));
        row.insertBefore(whitespace, right);
        var joined = Append(document, "div", "joined");
        joined.className = "row";
        var joinedLeft = Append(document, "span", "joined-left", joined);
        joinedLeft.className = "box";
        var joinedRight = Append(document, "span", "joined-right", joined);
        joinedRight.className = "box";

        try
        {
            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var text = Assert.IsType<DomTextBlockControl>(whitespace.Control);
            Assert.False(text.CssAllowsLayout);
            Assert.Equal(
                joinedRight.getBoundingClientRect().left - joinedLeft.getBoundingClientRect().right,
                right.getBoundingClientRect().left - left.getBoundingClientRect().right,
                6);

            var body = Assert.IsType<CssLayoutPanel>(HostTestUtilities.GetElement(document.body).Control);
            var portable = AvaloniaCssLayoutProjection.Capture(body, new Size(120, 40));
            Assert.Equal(
                portable.GetBox(joinedRight.Control).BorderBox.Left
                - portable.GetBox(joinedLeft.Control).BorderBox.Right,
                portable.GetBox(right.Control).BorderBox.Left
                - portable.GetBox(left.Control).BorderBox.Right,
                6);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
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

    private static int DarkBoundsWidth(Bitmap bitmap, PixelRect rect)
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

        var minimum = rect.Width;
        var maximum = -1;
        for (var y = 0; y < rect.Height; y++)
        {
            for (var x = 0; x < rect.Width; x++)
            {
                var index = (y * rect.Width + x) * 4;
                if (bytes[index] >= 96
                    || bytes[index + 1] >= 96
                    || bytes[index + 2] >= 96)
                {
                    continue;
                }
                minimum = Math.Min(minimum, x);
                maximum = Math.Max(maximum, x);
            }
        }
        return maximum >= minimum ? maximum - minimum + 1 : 0;
    }
}
