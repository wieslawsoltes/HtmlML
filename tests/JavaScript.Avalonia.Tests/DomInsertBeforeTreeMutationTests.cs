using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class DomInsertBeforeTreeMutationTests
{
    [AvaloniaFact]
    public void SelfReferenceAndRejectedReferencePreserveTheExistingTree()
    {
        var root = new CssLayoutPanel { Width = 400, Height = 300 };
        var window = new Window { Width = 400, Height = 300, Content = root };
        using var host = new AvaloniaBrowserHost(window);
        var document = host.Document;
        var body = HostTestUtilities.GetElement(document.body);
        var parent = Element("div", "parent");
        var first = Element("span", "first");
        var moved = Element("span", "moved");
        var last = Element("span", "last");
        parent.appendChild(first);
        parent.appendChild(moved);
        parent.appendChild(last);
        body.appendChild(parent);

        Assert.Same(moved, parent.insertBefore(moved, moved));
        Assert.Same(parent, moved.parentNode);
        Assert.Equal(new[] { "first", "moved", "last" }, ChildIds(parent));

        var other = Element("div", "other");
        var foreign = Element("span", "foreign");
        other.appendChild(foreign);
        body.appendChild(other);

        Assert.Throws<InvalidOperationException>(() => parent.insertBefore(moved, foreign));
        Assert.Same(parent, moved.parentNode);
        Assert.Equal(new[] { "first", "moved", "last" }, ChildIds(parent));

        AvaloniaDomElement Element(string tag, string id)
        {
            var element = HostTestUtilities.GetElement(document.createElement(tag));
            element.id = id;
            return element;
        }
    }

    [AvaloniaFact]
    public void ExistingChildrenCanBeReorderedAndReparentedBeforeAReference()
    {
        var root = new CssLayoutPanel { Width = 400, Height = 300 };
        var window = new Window { Width = 400, Height = 300, Content = root };
        using var host = new AvaloniaBrowserHost(window);
        var document = host.Document;
        var body = HostTestUtilities.GetElement(document.body);
        var source = HostTestUtilities.GetElement(document.createElement("div"));
        var target = HostTestUtilities.GetElement(document.createElement("div"));
        var a = HostTestUtilities.GetElement(document.createElement("span"));
        var b = HostTestUtilities.GetElement(document.createElement("span"));
        var c = HostTestUtilities.GetElement(document.createElement("span"));
        var reference = HostTestUtilities.GetElement(document.createElement("span"));
        a.id = "a";
        b.id = "b";
        c.id = "c";
        reference.id = "reference";
        source.appendChild(a);
        source.appendChild(b);
        source.appendChild(c);
        target.appendChild(reference);
        body.appendChild(source);
        body.appendChild(target);

        source.insertBefore(a, c);
        Assert.Equal(new[] { "b", "a", "c" }, ChildIds(source));
        source.insertBefore(c, b);
        Assert.Equal(new[] { "c", "b", "a" }, ChildIds(source));

        target.insertBefore(a, reference);
        Assert.Equal(new[] { "c", "b" }, ChildIds(source));
        Assert.Equal(new[] { "a", "reference" }, ChildIds(target));
        Assert.Same(target, a.parentNode);
    }

    [AvaloniaFact]
    public void RelatedMutationsPreserveIdentityOrderingAndFailureAtomicity()
    {
        var root = new CssLayoutPanel { Width = 400, Height = 300 };
        var window = new Window { Width = 400, Height = 300, Content = root };
        using var host = new AvaloniaBrowserHost(window);
        var document = host.Document;
        var body = HostTestUtilities.GetElement(document.body);
        var parent = Element("div", "parent");
        var first = Element("span", "first");
        var moved = Element("span", "moved");
        var last = Element("span", "last");
        var foreign = Element("span", "foreign");
        parent.appendChild(first);
        parent.appendChild(moved);
        parent.appendChild(last);
        body.appendChild(parent);

        Assert.Throws<InvalidOperationException>(() => parent.removeChild(foreign));
        Assert.Equal(new[] { "first", "moved", "last" }, ChildIds(parent));
        Assert.Same(parent, moved.parentNode);

        Assert.Same(moved, parent.replaceChild(moved, moved));
        Assert.Equal(new[] { "first", "moved", "last" }, ChildIds(parent));
        Assert.Same(parent, moved.parentNode);

        Assert.Same(last, parent.replaceChild(first, last));
        Assert.Equal(new[] { "moved", "first" }, ChildIds(parent));
        Assert.Null(last.parentNode);

        parent.replaceChildren(first, moved);
        Assert.Equal(new[] { "first", "moved" }, ChildIds(parent));
        Assert.Same(parent, first.parentNode);
        Assert.Same(parent, moved.parentNode);

        AvaloniaDomElement Element(string tag, string id)
        {
            var element = HostTestUtilities.GetElement(document.createElement(tag));
            element.id = id;
            return element;
        }
    }

    [AvaloniaFact]
    public void AncestorInsertionIsRejectedWithoutChangingTheTree()
    {
        var root = new CssLayoutPanel { Width = 400, Height = 300 };
        var window = new Window { Width = 400, Height = 300, Content = root };
        using var host = new AvaloniaBrowserHost(window);
        var document = host.Document;
        var body = HostTestUtilities.GetElement(document.body);
        var ancestor = HostTestUtilities.GetElement(document.createElement("div"));
        var child = HostTestUtilities.GetElement(document.createElement("div"));
        var grandchild = HostTestUtilities.GetElement(document.createElement("div"));
        ancestor.appendChild(child);
        child.appendChild(grandchild);
        body.appendChild(ancestor);

        Assert.Throws<InvalidOperationException>(() => grandchild.appendChild(ancestor));
        Assert.Throws<InvalidOperationException>(() => child.replaceChild(ancestor, grandchild));
        Assert.Same(body, ancestor.parentNode);
        Assert.Same(ancestor, child.parentNode);
        Assert.Same(child, grandchild.parentNode);
    }

    private static string[] ChildIds(AvaloniaDomElement parent)
        => parent.children.Cast<AvaloniaDomElement>().Select(child => child.id).ToArray();
}
