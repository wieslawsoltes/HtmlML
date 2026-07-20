# HtmlML.Backend.Avalonia

The complete Avalonia backend for HtmlML. It owns Avalonia visual projection, native
layout integration, Canvas/SVG replay, input, focus, text/image services, clipboard,
window lifecycle, OpenGL surfaces, frame scheduling, and headless composition.

Applications reference this package directly. The implementation currently retains
the established `JavaScript.Avalonia` CLR namespace, but there is no separate
`JavaScript.Avalonia` package or assembly.
