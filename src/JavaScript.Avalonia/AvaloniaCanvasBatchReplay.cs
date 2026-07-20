using System;
using System.Collections.Generic;
using System.IO;

namespace JavaScript.Avalonia;

/// <summary>
/// Adapts portable Canvas packet replay to the retained Avalonia drawing context.
/// The struct target keeps the batch crossing allocation-free.
/// </summary>
internal static class AvaloniaCanvasBatchReplay
{
    public static int Replay(
        CanvasRenderingContext2D context,
        ReadOnlySpan<double> values,
        IReadOnlyList<string> strings)
    {
        if (CanvasDrawingSurface.EnableNativeCanvasHotPath)
        {
            return ReplayNative(context, values, strings);
        }

        var target = new ReplayTarget(context);
        return CanvasPacketReplay.Replay(ref target, values, strings);
    }

    /// <summary>
    /// Avalonia's live chart path keeps the validated packet loop monomorphic.
    /// The portable replay remains available to other backends and diagnostics,
    /// while avoiding one generic-interface dispatch for every canvas opcode.
    /// </summary>
    private static int ReplayNative(
        CanvasRenderingContext2D context,
        ReadOnlySpan<double> packet,
        IReadOnlyList<string> strings)
    {
        var index = 0;
        var operationCount = 0;
        while (index < packet.Length)
        {
            if (packet.Length - index < 2)
            {
                throw new InvalidDataException(
                    $"Canvas2D batch ended with an incomplete command header at value {index} of {packet.Length}.");
            }

            var rawOpcode = packet[index++];
            var rawArgumentCount = packet[index++];
            if (!double.IsFinite(rawOpcode)
                || rawOpcode < 0
                || rawOpcode > int.MaxValue
                || rawOpcode != Math.Truncate(rawOpcode))
            {
                throw new InvalidDataException($"Canvas2D batch contains invalid opcode {rawOpcode}.");
            }
            if (!double.IsFinite(rawArgumentCount)
                || rawArgumentCount < 0
                || rawArgumentCount > int.MaxValue
                || rawArgumentCount != Math.Truncate(rawArgumentCount))
            {
                throw new InvalidDataException(
                    $"Canvas2D batch opcode {(int)rawOpcode} contains invalid argument count {rawArgumentCount}.");
            }

            var opcode = (int)rawOpcode;
            var argumentCount = (int)rawArgumentCount;
            if (argumentCount > packet.Length - index)
            {
                throw new InvalidDataException(
                    $"Canvas2D batch opcode {opcode} requests {argumentCount} arguments " +
                    $"but only {packet.Length - index} values remain.");
            }

            var values = packet.Slice(index, argumentCount);
            index += argumentCount;
            ReplayNativeOperation(context, opcode, values, strings);
            operationCount++;
        }

        return operationCount;
    }

    private static void ReplayNativeOperation(
        CanvasRenderingContext2D context,
        int opcode,
        ReadOnlySpan<double> values,
        IReadOnlyList<string> strings)
    {
        switch (opcode)
        {
            case 1: context.save(); break;
            case 2: context.restore(); break;
            case 3: context.resetTransform(); break;
            case 4: context.setTransform(values[0], values[1], values[2], values[3], values[4], values[5]); break;
            case 5: context.transform(values[0], values[1], values[2], values[3], values[4], values[5]); break;
            case 6: context.translate(values[0], values[1]); break;
            case 7: context.scale(values[0], values[1]); break;
            case 8: context.rotate(values[0]); break;
            case 9: context.beginPath(); break;
            case 10: context.closePath(); break;
            case 11: context.moveTo(values[0], values[1]); break;
            case 12: context.lineTo(values[0], values[1]); break;
            case 13: context.bezierCurveTo(values[0], values[1], values[2], values[3], values[4], values[5]); break;
            case 14: context.quadraticCurveTo(values[0], values[1], values[2], values[3]); break;
            case 15: context.arc(values[0], values[1], values[2], values[3], values[4], values[5] != 0); break;
            case 16: context.arcTo(values[0], values[1], values[2], values[3], values[4]); break;
            case 17: context.rect(values[0], values[1], values[2], values[3]); break;
            case 18: context.clip(); break;
            case 19:
            {
                var segments = values.Slice(1, (int)values[0]);
                if (CanvasDrawingSurface.EnableLineDashReplayDeduplication)
                {
                    context.SetLineDash(segments);
                }
                else
                {
                    context.setLineDash(segments.ToArray());
                }
                break;
            }
            case 20: context.stroke(); break;
            case 21: context.fill(); break;
            case 22: context.fillRect(values[0], values[1], values[2], values[3]); break;
            case 23: context.strokeRect(values[0], values[1], values[2], values[3]); break;
            case 24: context.clearRect(values[0], values[1], values[2], values[3]); break;
            case 25: context.fillText(StringAt(strings, values[0]), values[1], values[2]); break;
            case 26: context.strokeText(StringAt(strings, values[0]), values[1], values[2]); break;
            case 40: context.fillStyle = StringAt(strings, values[0]); break;
            case 41: context.strokeStyle = StringAt(strings, values[0]); break;
            case 42: context.lineWidth = values[0]; break;
            case 43: context.lineCap = StringAt(strings, values[0]); break;
            case 44: context.lineJoin = StringAt(strings, values[0]); break;
            case 45: context.miterLimit = values[0]; break;
            case 46: context.globalAlpha = values[0]; break;
            case 47: context.lineDashOffset = values[0]; break;
            case 48: context.font = StringAt(strings, values[0]); break;
            case 49: context.textAlign = StringAt(strings, values[0]); break;
            case 50: context.textBaseline = StringAt(strings, values[0]); break;
            case 51: context.imageSmoothingEnabled = values[0] != 0; break;
            case 52: context.imageSmoothingQuality = StringAt(strings, values[0]); break;
            case 53: context.globalCompositeOperation = StringAt(strings, values[0]); break;
            case 54: context.shadowColor = StringAt(strings, values[0]); break;
            case 55: context.shadowBlur = values[0]; break;
            case 56: context.shadowOffsetX = values[0]; break;
            case 57: context.shadowOffsetY = values[0]; break;
            default: throw new InvalidOperationException($"Unknown Canvas2D batch opcode {opcode}.");
        }
    }

    private static string StringAt(IReadOnlyList<string> strings, double index)
    {
        if (!double.IsFinite(index)
            || index < 0
            || index >= strings.Count
            || index != Math.Truncate(index))
        {
            throw new InvalidDataException(
                $"Canvas2D batch string index {index} is outside the packet string table " +
                $"containing {strings.Count} entries.");
        }

        return strings[(int)index];
    }

    private readonly struct ReplayTarget(CanvasRenderingContext2D context) : ICanvasPacketReplayTarget
    {
        public void Save() => context.save();
        public void Restore() => context.restore();
        public void ResetTransform() => context.resetTransform();
        public void SetTransform(double a, double b, double c, double d, double e, double f) => context.setTransform(a, b, c, d, e, f);
        public void Transform(double a, double b, double c, double d, double e, double f) => context.transform(a, b, c, d, e, f);
        public void Translate(double x, double y) => context.translate(x, y);
        public void Scale(double x, double y) => context.scale(x, y);
        public void Rotate(double angle) => context.rotate(angle);
        public void BeginPath() => context.beginPath();
        public void ClosePath() => context.closePath();
        public void MoveTo(double x, double y) => context.moveTo(x, y);
        public void LineTo(double x, double y) => context.lineTo(x, y);
        public void BezierCurveTo(double cp1X, double cp1Y, double cp2X, double cp2Y, double x, double y) => context.bezierCurveTo(cp1X, cp1Y, cp2X, cp2Y, x, y);
        public void QuadraticCurveTo(double cpX, double cpY, double x, double y) => context.quadraticCurveTo(cpX, cpY, x, y);
        public void Arc(double x, double y, double radius, double startAngle, double endAngle, bool counterClockwise) => context.arc(x, y, radius, startAngle, endAngle, counterClockwise);
        public void ArcTo(double x1, double y1, double x2, double y2, double radius) => context.arcTo(x1, y1, x2, y2, radius);
        public void Rect(double x, double y, double width, double height) => context.rect(x, y, width, height);
        public void Clip() => context.clip();
        public void SetLineDash(ReadOnlySpan<double> segments)
        {
            if (CanvasDrawingSurface.EnableLineDashReplayDeduplication)
            {
                context.SetLineDash(segments);
            }
            else
            {
                context.setLineDash(segments.ToArray());
            }
        }
        public void Stroke() => context.stroke();
        public void Fill() => context.fill();
        public void FillRect(double x, double y, double width, double height) => context.fillRect(x, y, width, height);
        public void StrokeRect(double x, double y, double width, double height) => context.strokeRect(x, y, width, height);
        public void ClearRect(double x, double y, double width, double height) => context.clearRect(x, y, width, height);
        public void FillText(string text, double x, double y) => context.fillText(text, x, y);
        public void StrokeText(string text, double x, double y) => context.strokeText(text, x, y);
        public void SetFillStyle(string value) => context.fillStyle = value;
        public void SetStrokeStyle(string value) => context.strokeStyle = value;
        public void SetLineWidth(double value) => context.lineWidth = value;
        public void SetLineCap(string value) => context.lineCap = value;
        public void SetLineJoin(string value) => context.lineJoin = value;
        public void SetMiterLimit(double value) => context.miterLimit = value;
        public void SetGlobalAlpha(double value) => context.globalAlpha = value;
        public void SetLineDashOffset(double value) => context.lineDashOffset = value;
        public void SetFont(string value) => context.font = value;
        public void SetTextAlign(string value) => context.textAlign = value;
        public void SetTextBaseline(string value) => context.textBaseline = value;
        public void SetImageSmoothingEnabled(bool value) => context.imageSmoothingEnabled = value;
        public void SetImageSmoothingQuality(string value) => context.imageSmoothingQuality = value;
        public void SetGlobalCompositeOperation(string value) => context.globalCompositeOperation = value;
        public void SetShadowColor(string value) => context.shadowColor = value;
        public void SetShadowBlur(double value) => context.shadowBlur = value;
        public void SetShadowOffsetX(double value) => context.shadowOffsetX = value;
        public void SetShadowOffsetY(double value) => context.shadowOffsetY = value;
    }
}
