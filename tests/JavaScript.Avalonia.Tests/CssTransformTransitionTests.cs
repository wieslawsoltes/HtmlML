using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssTransformTransitionTests
{
    [AvaloniaFact]
    public void ClassChangeTransitionsTranslateAndScaleThroughIntermediateMatrices()
    {
        var panel = new CssLayoutPanel { Width = 320, Height = 180 };
        var window = new Window
        {
            Width = 320,
            Height = 180,
            Content = panel
        };
        var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using (host)
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                .indicator {
                    width: 100px;
                    height: 4px;
                    transform: translateX(0px) scaleX(.5);
                    transform-origin: left;
                    transition: transform 120ms linear;
                }
                .indicator.selected { transform: translateX(80px) scaleX(1); }
                """;
            document.head.appendChild(style);
            var indicator = HostTestUtilities.GetElement(document.createElement("div"));
            indicator.className = "indicator";
            HostTestUtilities.GetElement(document.body).appendChild(indicator);
            document.EnsureStylesCurrent();

            indicator.classList.add("selected");
            document.EnsureStylesCurrent();
            var starting = Assert.IsType<MatrixTransform>(indicator.Control.RenderTransform).Matrix;
            Assert.Equal(.5, starting.M11, 6);
            Assert.Equal(0, starting.M31, 6);

            indicator.AdvanceCssTransformTransitionForTest(TimeSpan.FromMilliseconds(60));
            var midpoint = Assert.IsType<MatrixTransform>(indicator.Control.RenderTransform).Matrix;
            Assert.Equal(.75, midpoint.M11, 6);
            Assert.Equal(40, midpoint.M31, 6);
            var computedMidpoint = document.getComputedStyle(indicator).getPropertyValue("transform");
            Assert.StartsWith("matrix(", computedMidpoint);
            Assert.NotEqual("matrix(1, 0, 0, 1, 80, 0)", computedMidpoint);

            indicator.AdvanceCssTransformTransitionForTest(TimeSpan.FromMilliseconds(120));
            var completed = Assert.IsType<MatrixTransform>(indicator.Control.RenderTransform).Matrix;
            Assert.Equal(1, completed.M11, 6);
            Assert.Equal(80, completed.M31, 6);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AncestorClassChangeTransitionsNativeRotationWhileComputedStyleIsFinal()
    {
        var panel = new CssLayoutPanel { Width = 320, Height = 180 };
        var window = new Window
        {
            Width = 320,
            Height = 180,
            Content = panel
        };
        var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using (host)
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                .toggler .icon {
                    transform: rotate(-180deg);
                    transition: transform .1s cubic-bezier(.06,.52,1,.54);
                }
                .closed .toggler .icon { transform: rotate(0); }
                """;
            document.head.appendChild(style);
            var wrapper = HostTestUtilities.GetElement(document.createElement("div"));
            var toggler = HostTestUtilities.GetElement(document.createElement("button"));
            toggler.className = "toggler";
            var icon = HostTestUtilities.GetElement(document.createElement("span"));
            icon.className = "icon";
            toggler.appendChild(icon);
            wrapper.appendChild(toggler);
            HostTestUtilities.GetElement(document.body).appendChild(wrapper);
            document.EnsureStylesCurrent();

            Assert.Equal(-180, Assert.IsType<RotateTransform>(icon.Control.RenderTransform).Angle);
            Assert.Contains("transform", document.getComputedStyle(icon).getPropertyValue("transition"));
            wrapper.classList.add("closed");
            document.EnsureStylesCurrent();

            // CSSOM exposes the currently applied transition matrix.
            var liveComputedStyle = document.getComputedStyle(icon);
            var startingMatrix = liveComputedStyle.getPropertyValue("transform");
            Assert.StartsWith("matrix(", startingMatrix);
            Assert.NotEqual("matrix(1, 0, 0, 1, 0, 0)", startingMatrix);
            var buildsAtStart = document.ComputedStyleSnapshotBuildCount;
            var nativeRotation = Assert.IsType<RotateTransform>(icon.Control.RenderTransform);
            Assert.Equal(-180, nativeRotation.Angle);
            var compositionVisual = Assert.IsAssignableFrom<CompositionVisual>(
                ElementComposition.GetElementVisual(icon.Control));
            Assert.InRange(compositionVisual.RotationAngle, MathF.PI - 0.001f, MathF.PI + 0.001f);

            icon.AdvanceCssTransformTransitionForTest(TimeSpan.FromMilliseconds(50));
            var midpointRotation = Assert.IsType<RotateTransform>(icon.Control.RenderTransform);
            Assert.Same(nativeRotation, midpointRotation);
            var midpoint = midpointRotation.Angle;
            Assert.InRange(midpoint, -89, -87);
            var midpointMatrix = document.getComputedStyle(icon).getPropertyValue("transform");
            Assert.StartsWith("matrix(", midpointMatrix);
            Assert.NotEqual(startingMatrix, midpointMatrix);
            var liveMidpointMatrix = liveComputedStyle.getPropertyValue("transform");
            Assert.StartsWith("matrix(", liveMidpointMatrix);
            Assert.NotEqual(startingMatrix, liveMidpointMatrix);
            Assert.Same(liveComputedStyle, document.getComputedStyle(icon));
            Assert.Equal(buildsAtStart, document.ComputedStyleSnapshotBuildCount);

            icon.AdvanceCssTransformTransitionForTest(TimeSpan.FromMilliseconds(100));
            Assert.Equal(0, Assert.IsType<RotateTransform>(icon.Control.RenderTransform).Angle);
            Assert.Equal("matrix(1, 0, 0, 1, 0, 0)", document.getComputedStyle(icon).getPropertyValue("transform"));
            Assert.Equal("matrix(1, 0, 0, 1, 0, 0)", liveComputedStyle.getPropertyValue("transform"));
            window.Close();
        }
    }
}
