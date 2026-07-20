using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using JavaScript.Avalonia;

namespace JavaScriptPlayground;

internal sealed class PlaygroundFileBridge
{
    private readonly Window _owner;
    private readonly Action<object, PlaygroundPdfFileResult> _invokeCallback;

    public PlaygroundFileBridge(
        Window owner,
        Action<object, PlaygroundPdfFileResult> invokeCallback)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _invokeCallback = invokeCallback ?? throw new ArgumentNullException(nameof(invokeCallback));
    }

    public void openPdfDocument(object callback)
    {
        if (callback is null)
        {
            return;
        }

        _ = OpenPdfDocumentAsync(callback);
    }

    private async Task OpenPdfDocumentAsync(object callback)
    {
        PlaygroundPdfFileResult result;

        try
        {
            if (!_owner.StorageProvider.CanOpen)
            {
                result = PlaygroundPdfFileResult.Error("Opening files is not supported by this platform.");
            }
            else
            {
                var pdfType = new FilePickerFileType("PDF document")
                {
                    Patterns = new[] { "*.pdf" },
                    MimeTypes = new[] { "application/pdf" },
                    AppleUniformTypeIdentifiers = new[] { "com.adobe.pdf" }
                };

                var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open PDF document",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { pdfType, FilePickerFileTypes.All }
                });

                var file = files.FirstOrDefault();
                if (file is null)
                {
                    result = PlaygroundPdfFileResult.Cancelled();
                }
                else
                {
                    await using var stream = await file.OpenReadAsync();
                    using var memory = new MemoryStream();
                    await stream.CopyToAsync(memory);
                    result = PlaygroundPdfFileResult.Success(file.Name, Convert.ToBase64String(memory.ToArray()));
                }
            }
        }
        catch (Exception ex)
        {
            result = PlaygroundPdfFileResult.Error(ex.Message);
        }

        InvokeCallback(callback, result);
    }

    private void InvokeCallback(object callback, PlaygroundPdfFileResult result)
    {
        try
        {
            _invokeCallback(callback, result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PDF file callback failed: {ex}");
        }
    }
}

internal sealed class PlaygroundPdfFileResult
{
    public bool success { get; private init; }

    public bool cancelled { get; private init; }

    public string name { get; private init; } = string.Empty;

    public string dataBase64 { get; private init; } = string.Empty;

    public string error { get; private init; } = string.Empty;

    public static PlaygroundPdfFileResult Success(string name, string dataBase64) => new()
    {
        success = true,
        name = name ?? string.Empty,
        dataBase64 = dataBase64 ?? string.Empty
    };

    public static PlaygroundPdfFileResult Cancelled() => new()
    {
        cancelled = true
    };

    public static PlaygroundPdfFileResult Error(string message) => new()
    {
        error = message ?? string.Empty
    };
}
