using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JavaScript.Avalonia;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssTableLayoutTests
{
    [AvaloniaFact]
    public void TableColumnsSuppressDescendantsInNativeAndPortableLayout()
    {
        var root = new CssLayoutPanel { Width = 240, Height = 120 };
        var window = new Window { Width = 240, Height = 120, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);

        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                x-col { display: table-column; }
                x-colgroup { display: table-column-group; }
                .target { display: block !important; visibility: visible !important; width: 100px; height: 100px; }
                """;
            document.head.appendChild(style);

            var column = HostTestUtilities.GetElement(document.createElement("x-col"));
            var columnTarget = HostTestUtilities.GetElement(document.createElement("div"));
            columnTarget.className = "target";
            column.appendChild(columnTarget);
            var columnGroup = HostTestUtilities.GetElement(document.createElement("x-colgroup"));
            var groupTarget = HostTestUtilities.GetElement(document.createElement("div"));
            groupTarget.className = "target";
            columnGroup.appendChild(groupTarget);
            HostTestUtilities.GetElement(document.body).appendChild(column);
            HostTestUtilities.GetElement(document.body).appendChild(columnGroup);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("table-column", document.getComputedStyle(column).getPropertyValue("display"));
            Assert.Equal("table-column-group", document.getComputedStyle(columnGroup).getPropertyValue("display"));
            Assert.Equal(0, columnTarget.offsetWidth);
            Assert.Equal(0, groupTarget.offsetWidth);

            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(240, 120));
            Assert.Equal(0, portable.GetBox(columnTarget.Control).BorderBox.Width);
            Assert.Equal(0, portable.GetBox(groupTarget.Control).BorderBox.Width);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DirectTableCellsUseOneAnonymousRowAndShrinkToFitPercentageTracks()
    {
        var root = new CssLayoutPanel { Width = 400, Height = 180 };
        var window = new Window { Width = 400, Height = 180, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);

        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                x-table { display: table; }
                x-td { display: table-cell; }
                """;
            document.head.appendChild(style);
            var table = HostTestUtilities.GetElement(document.createElement("x-table"));
            var percentageCell = HostTestUtilities.GetElement(document.createElement("x-td"));
            percentageCell.style.cssText = "width: 50%; height: 0px";
            var fixedCell = HostTestUtilities.GetElement(document.createElement("x-td"));
            fixedCell.style.cssText = "width: 50px; height: 100px";
            table.appendChild(percentageCell);
            table.appendChild(fixedCell);
            HostTestUtilities.GetElement(document.body).appendChild(table);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(100, table.offsetWidth);
            Assert.Equal(50, percentageCell.offsetWidth);
            Assert.Equal(100, percentageCell.offsetHeight);

            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(400, 180));
            Assert.Equal(50, portable.GetBox(percentageCell.Control).BorderBox.Width);
            Assert.Equal(100, portable.GetBox(percentageCell.Control).BorderBox.Height);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void HtmlTableUaDefaultsAndSharedColumnsMatchNativeAndPortableGeometry()
    {
        var root = new CssLayoutPanel { Width = 500, Height = 220 };
        var window = new Window { Width = 500, Height = 220, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);

        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                table { border-collapse: collapse; }
                tr.item { height: 32px; vertical-align: middle; }
                .icon { width: 28px; height: 20px; padding-left: 8px; }
                .label { width: 218px; height: 18px; }
                .line { height: 1px; margin: 6px 0; }
                """;
            document.head.appendChild(style);

            var table = HostTestUtilities.GetElement(document.createElement("table"));
            var body = HostTestUtilities.GetElement(document.createElement("tbody"));
            var item = HostTestUtilities.GetElement(document.createElement("tr"));
            item.className = "item";
            var itemIconCell = HostTestUtilities.GetElement(document.createElement("td"));
            var itemLabelCell = HostTestUtilities.GetElement(document.createElement("td"));
            var icon = HostTestUtilities.GetElement(document.createElement("div"));
            icon.className = "icon";
            var label = HostTestUtilities.GetElement(document.createElement("div"));
            label.className = "label";
            itemIconCell.appendChild(icon);
            itemLabelCell.appendChild(label);
            item.appendChild(itemIconCell);
            item.appendChild(itemLabelCell);

            var separator = HostTestUtilities.GetElement(document.createElement("tr"));
            var separatorIconCell = HostTestUtilities.GetElement(document.createElement("td"));
            var separatorLabelCell = HostTestUtilities.GetElement(document.createElement("td"));
            var line = HostTestUtilities.GetElement(document.createElement("div"));
            line.className = "line";
            separatorLabelCell.appendChild(line);
            separator.appendChild(separatorIconCell);
            separator.appendChild(separatorLabelCell);
            body.appendChild(item);
            body.appendChild(separator);
            table.appendChild(body);
            HostTestUtilities.GetElement(document.body).appendChild(table);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("table", table.tagName.ToLowerInvariant());
            Assert.Equal("table", document.getComputedStyle(table).getPropertyValue("display"));
            Assert.Equal("table-row-group", document.getComputedStyle(body).getPropertyValue("display"));
            Assert.Equal("table-row", document.getComputedStyle(item).getPropertyValue("display"));
            Assert.Equal("table-cell", document.getComputedStyle(itemIconCell).getPropertyValue("display"));
            Assert.Equal("middle", document.getComputedStyle(item).getPropertyValue("vertical-align"));

            var itemRect = item.getBoundingClientRect();
            var separatorRect = separator.getBoundingClientRect();
            var firstIconRect = itemIconCell.getBoundingClientRect();
            var firstLabelRect = itemLabelCell.getBoundingClientRect();
            var secondIconRect = separatorIconCell.getBoundingClientRect();
            var secondLabelRect = separatorLabelCell.getBoundingClientRect();
            Assert.Equal(254, table.getBoundingClientRect().width);
            Assert.Equal(32, itemRect.height);
            Assert.Equal(13, separatorRect.height);
            Assert.Equal(36, firstIconRect.width);
            Assert.Equal(218, firstLabelRect.width);
            Assert.Equal(firstIconRect.width, secondIconRect.width);
            Assert.Equal(firstLabelRect.x, secondLabelRect.x);
            Assert.Equal(firstIconRect.right, firstLabelRect.left);
            Assert.Equal(secondIconRect.right, secondLabelRect.left);
            Assert.Equal(itemRect.bottom, separatorRect.top);

            var portable = AvaloniaCssLayoutProjection.Capture(
                Assert.IsType<CssLayoutPanel>(table.Control),
                new Size(254, 45));
            Assert.Equal(36, portable.GetBox(itemIconCell.Control).BorderBox.Width);
            Assert.Equal(218, portable.GetBox(itemLabelCell.Control).BorderBox.Width);
            Assert.Equal(
                portable.GetBox(itemLabelCell.Control).BorderBox.X,
                portable.GetBox(separatorLabelCell.Control).BorderBox.X);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DefiniteSingleColumnTableStretchesAnonymousAutoFlexChildInNativeAndPortableLayout()
    {
        var root = new CssLayoutPanel { Width = 1000, Height = 100 };
        var window = new Window { Width = 1000, Height = 100, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);

        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                html, body { margin: 0; padding: 0; }
                .table { display: table; width: 100%; height: 40px; }
                .inner { display: flex; height: 40px; }
                .left { width: 120px; height: 40px; }
                .filler { flex-grow: 1; flex-basis: 0px; height: 40px; }
                .right { width: 80px; height: 40px; }
                """;
            document.head.appendChild(style);

            var table = HostTestUtilities.GetElement(document.createElement("div"));
            table.className = "table";
            var inner = HostTestUtilities.GetElement(document.createElement("div"));
            inner.className = "inner";
            var left = HostTestUtilities.GetElement(document.createElement("div"));
            left.className = "left";
            var filler = HostTestUtilities.GetElement(document.createElement("div"));
            filler.className = "filler";
            var right = HostTestUtilities.GetElement(document.createElement("button"));
            right.className = "right";
            inner.appendChild(left);
            inner.appendChild(filler);
            inner.appendChild(right);
            table.appendChild(inner);

            var body = HostTestUtilities.GetElement(document.body);
            body.appendChild(table);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var tableRect = table.getBoundingClientRect();
            var innerRect = inner.getBoundingClientRect();
            var rightRect = right.getBoundingClientRect();
            Assert.Equal(1000, tableRect.width);
            Assert.Equal(1000, Assert.Single(CssLayout.GetTableColumnWidths(table.Control)!));
            Assert.Equal(tableRect.width, innerRect.width);
            Assert.Equal(920, rightRect.left);
            Assert.Equal(tableRect.right, rightRect.right);

            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(1000, 100));
            var portableTable = portable.GetBox(table.Control).BorderBox;
            var portableInner = portable.GetBox(inner.Control).BorderBox;
            var portableRight = portable.GetBox(right.Control).BorderBox;
            Assert.Equal(1000, portableTable.Width);
            Assert.Equal(portableTable.Width, portableInner.Width);
            Assert.Equal(920, portableRight.X);
            Assert.Equal(portableTable.Right, portableRight.Right);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void FractionalExplicitCellsFillDefiniteSingleRowInNativeAndPortableLayout()
    {
        var root = new CssLayoutPanel { Width = 120, Height = 100 };
        var window = new Window { Width = 120, Height = 100, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);

        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                .table { display: table; height: 20px; }
                .cell { display: table-cell; }
                .reference { font-size: 0; height: 20px; }
                .reference-cell { display: inline-block; height: 20px; }
                """;
            document.head.appendChild(style);
            var table = HostTestUtilities.GetElement(document.createElement("div"));
            table.className = "table";
            var first = HostTestUtilities.GetElement(document.createElement("div"));
            first.className = "cell";
            first.style.cssText = "width: 3.6px";
            var second = HostTestUtilities.GetElement(document.createElement("div"));
            second.className = "cell";
            second.style.cssText = "width: 3.6px";
            table.appendChild(first);
            table.appendChild(second);
            HostTestUtilities.GetElement(document.body).appendChild(table);
            var reference = HostTestUtilities.GetElement(document.createElement("div"));
            reference.className = "reference";
            var referenceFirst = HostTestUtilities.GetElement(document.createElement("div"));
            referenceFirst.className = "reference-cell";
            referenceFirst.style.cssText = "width: 3.6px";
            var referenceSecond = HostTestUtilities.GetElement(document.createElement("div"));
            referenceSecond.className = "reference-cell";
            referenceSecond.style.cssText = "width: 3.6px";
            reference.appendChild(referenceFirst);
            reference.appendChild(referenceSecond);
            HostTestUtilities.GetElement(document.body).appendChild(reference);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(referenceFirst.getBoundingClientRect().width, first.getBoundingClientRect().width, 6);
            Assert.Equal(referenceSecond.getBoundingClientRect().width, second.getBoundingClientRect().width, 6);
            Assert.Equal(
                referenceSecond.getBoundingClientRect().right - referenceFirst.getBoundingClientRect().left,
                table.getBoundingClientRect().width,
                6);
            Assert.Equal(20, first.getBoundingClientRect().height);
            Assert.Equal(20, second.getBoundingClientRect().height);

            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(120, 100));
            Assert.Equal(
                portable.GetBox(referenceFirst.Control).BorderBox.Width,
                portable.GetBox(first.Control).BorderBox.Width,
                6);
            Assert.Equal(
                portable.GetBox(referenceSecond.Control).BorderBox.Width,
                portable.GetBox(second.Control).BorderBox.Width,
                6);
            Assert.Equal(20, portable.GetBox(first.Control).BorderBox.Height);
            Assert.Equal(20, portable.GetBox(second.Control).BorderBox.Height);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void FixedTableDistributesMultiColumnExcessBySpecifiedWidthClass()
    {
        var root = new CssLayoutPanel { Width = 400, Height = 100 };
        var window = new Window { Width = 400, Height = 100, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);

        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                html, body { margin: 0; padding: 0; }
                table {
                    width: 300px;
                    height: 20px;
                    border-collapse: collapse;
                    table-layout: fixed;
                }
                td { padding: 0; }
                td:nth-child(1) { width: 20px; }
                td:nth-child(2) { width: 10px; }
                td:nth-child(3) { width: 10%; }
                """;
            document.head.appendChild(style);

            var table = HostTestUtilities.GetElement(document.createElement("table"));
            var row = HostTestUtilities.GetElement(document.createElement("tr"));
            var first = HostTestUtilities.GetElement(document.createElement("td"));
            var second = HostTestUtilities.GetElement(document.createElement("td"));
            var percentage = HostTestUtilities.GetElement(document.createElement("td"));
            row.appendChild(first);
            row.appendChild(second);
            row.appendChild(percentage);
            table.appendChild(row);
            HostTestUtilities.GetElement(document.body).appendChild(table);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("fixed", document.getComputedStyle(table).getPropertyValue("table-layout"));
            Assert.Equal(300, table.getBoundingClientRect().width);
            Assert.Equal(180, first.getBoundingClientRect().width);
            Assert.Equal(90, second.getBoundingClientRect().width);
            Assert.Equal(30, percentage.getBoundingClientRect().width);
            Assert.Equal(table.getBoundingClientRect().right, percentage.getBoundingClientRect().right);

            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(400, 100));
            Assert.Equal(180, portable.GetBox(first.Control).BorderBox.Width);
            Assert.Equal(90, portable.GetBox(second.Control).BorderBox.Width);
            Assert.Equal(30, portable.GetBox(percentage.Control).BorderBox.Width);
            Assert.Equal(
                portable.GetBox(table.Control).BorderBox.Right,
                portable.GetBox(percentage.Control).BorderBox.Right);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void FixedTableGivesRemainingWidthToAutoColTrackInNativeAndPortableLayout()
    {
        var root = new CssLayoutPanel { Width = 400, Height = 100 };
        var window = new Window { Width = 400, Height = 100, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);

        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                html, body { margin: 0; padding: 0; }
                table { width: 300px; height: 20px; table-layout: fixed; }
                td { padding: 0; }
                """;
            document.head.appendChild(style);

            var table = HostTestUtilities.GetElement(document.createElement("table"));
            var colgroup = HostTestUtilities.GetElement(document.createElement("colgroup"));
            var fixedColumn = HostTestUtilities.GetElement(document.createElement("col"));
            fixedColumn.style.cssText = "width: 60px";
            var autoColumn = HostTestUtilities.GetElement(document.createElement("col"));
            var percentageColumn = HostTestUtilities.GetElement(document.createElement("col"));
            percentageColumn.style.cssText = "width: 20%";
            colgroup.appendChild(fixedColumn);
            colgroup.appendChild(autoColumn);
            colgroup.appendChild(percentageColumn);
            table.appendChild(colgroup);

            var row = HostTestUtilities.GetElement(document.createElement("tr"));
            var first = HostTestUtilities.GetElement(document.createElement("td"));
            var second = HostTestUtilities.GetElement(document.createElement("td"));
            var third = HostTestUtilities.GetElement(document.createElement("td"));
            row.appendChild(first);
            row.appendChild(second);
            row.appendChild(third);
            table.appendChild(row);
            HostTestUtilities.GetElement(document.body).appendChild(table);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(60, first.getBoundingClientRect().width);
            Assert.Equal(180, second.getBoundingClientRect().width);
            Assert.Equal(60, third.getBoundingClientRect().width);
            Assert.Equal(table.getBoundingClientRect().right, third.getBoundingClientRect().right);

            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(400, 100));
            Assert.Equal(60, portable.GetBox(first.Control).BorderBox.Width);
            Assert.Equal(180, portable.GetBox(second.Control).BorderBox.Width);
            Assert.Equal(60, portable.GetBox(third.Control).BorderBox.Width);
            Assert.Equal(
                portable.GetBox(table.Control).BorderBox.Right,
                portable.GetBox(third.Control).BorderBox.Right);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void MixedImproperTableChildrenCreateOrderedAnonymousRowsAndCells()
    {
        var root = new CssLayoutPanel { Width = 400, Height = 160 };
        var window = new Window { Width = 400, Height = 160, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);

        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                html, body { margin: 0; padding: 0; }
                body { padding-left: 10px; }
                x-table { display: table; }
                x-col { display: table-column; }
                x-td { display: table-cell; }
                """;
            document.head.appendChild(style);
            var table = HostTestUtilities.GetElement(document.createElement("x-table"));
            var first = HostTestUtilities.GetElement(document.createElement("span"));
            first.textContent = "1";
            var column = HostTestUtilities.GetElement(document.createElement("x-col"));
            column.style.cssText = "width: 50px";
            var cell = HostTestUtilities.GetElement(document.createElement("x-td"));
            cell.textContent = "1";
            var second = HostTestUtilities.GetElement(document.createElement("span"));
            second.textContent = "2";
            table.appendChild(first);
            table.appendChild(Assert.IsAssignableFrom<AvaloniaDomElement>(document.createTextNode(" ")));
            table.appendChild(column);
            table.appendChild(Assert.IsAssignableFrom<AvaloniaDomElement>(document.createTextNode(" ")));
            table.appendChild(cell);
            table.appendChild(Assert.IsAssignableFrom<AvaloniaDomElement>(document.createTextNode(" ")));
            table.appendChild(second);
            HostTestUtilities.GetElement(document.body).appendChild(table);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.True(cell.offsetLeft < 25, $"cell left was {cell.offsetLeft}");
            Assert.True(second.offsetLeft > 50, $"second left was {second.offsetLeft}");
            Assert.Same(HostTestUtilities.GetElement(document.body), second.offsetParent);

            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(400, 160));
            Assert.True(portable.GetBox(cell.Control).BorderBox.X < 25);
            Assert.True(portable.GetBox(second.Control).BorderBox.X > 50);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
