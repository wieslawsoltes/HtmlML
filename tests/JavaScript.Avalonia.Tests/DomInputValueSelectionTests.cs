using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class DomInputValueSelectionTests
{
    [AvaloniaFact]
    public void ChangedValueIdlAssignmentCollapsesSelectionAtEndWhileSameValuePreservesIt()
    {
        var root = new CssLayoutPanel { Width = 320, Height = 80 };
        var window = new Window { Width = 320, Height = 80, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        try
        {
            var input = HostTestUtilities.GetElement(host.Document.createElement("input"));
            HostTestUtilities.GetElement(host.Document.body).appendChild(input);
            var textBox = Assert.IsType<DomTextInputControl>(input.Control);

            input.value = "abcdefghij";

            Assert.Equal(10, input.selectionStart);
            Assert.Equal(10, input.selectionEnd);
            Assert.Equal(10, textBox.CaretIndex);
            Assert.Equal("none", input.selectionDirection);

            Assert.True(input.focus());
            Assert.Equal(10, input.selectionStart);
            Assert.Equal(10, input.selectionEnd);
            Assert.Equal(10, textBox.CaretIndex);

            input.setSelectionRange(2, 4, "backward");
            input.value = "abcdefghij";

            Assert.Equal(2, input.selectionStart);
            Assert.Equal(4, input.selectionEnd);
            Assert.Equal("backward", input.selectionDirection);

            input.value = "klmnopqrst";

            Assert.Equal(10, input.selectionStart);
            Assert.Equal(10, input.selectionEnd);
            Assert.Equal(10, textBox.CaretIndex);
            Assert.Equal("none", input.selectionDirection);

            input.setSelectionRange(2, 4);
            input.value = "x";

            Assert.Equal(1, input.selectionStart);
            Assert.Equal(1, input.selectionEnd);
            Assert.Equal(1, textBox.CaretIndex);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
