namespace HtmlML.Core;

/// <summary>
/// Typed geometry seam for engine adapters. Implementations write x, y, width,
/// height, right, bottom, client width, and client height without boxing.
/// </summary>
public interface IHtmlMlGeometryWriter
{
    bool TryWriteGeometry(HtmlMlNodeId nodeId, Span<double> destination);
}

public readonly record struct HtmlMlEventPacket(
    HtmlMlNodeId Target,
    int EventType,
    double X,
    double Y,
    int Flags);

public interface IHtmlMlEventPacketSink
{
    void Dispatch(in HtmlMlEventPacket packet);
}

/// <summary>
/// Batch boundary used by JavaScript engines to transfer Canvas operations without
/// per-operation reflection, boxing, or dictionary-shaped calls.
/// </summary>
public interface IHtmlMlCanvasPacketSink
{
    void Replay(ReadOnlySpan<double> values, IReadOnlyList<string> strings);
}
