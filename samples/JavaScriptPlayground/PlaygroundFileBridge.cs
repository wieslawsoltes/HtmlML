using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using JavaScript.Avalonia;
using Jint.Native;

namespace JavaScriptPlayground;

internal sealed class PlaygroundFileBridge
{
    private readonly JintAvaloniaHost _host;
    private readonly Window _owner;

    public PlaygroundFileBridge(JintAvaloniaHost host, Window owner)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public void openPdfDocument(JsValue callback)
    {
        if (callback == JsValue.Undefined || callback == JsValue.Null)
        {
            return;
        }

        _ = OpenPdfDocumentAsync(callback);
    }

    private async Task OpenPdfDocumentAsync(JsValue callback)
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
                    var bytes = memory.ToArray();
                    // PDF.js can parse some external PDFs but fail to render their pages in Jint's fake-worker path.
                    // A host thumbnail keeps the playground preview useful instead of showing compressed PDF bytes.
                    var previewImageBase64 = await TryCreatePdfPreviewImageBase64Async(file, bytes);
                    result = PlaygroundPdfFileResult.Success(file.Name, Convert.ToBase64String(bytes), previewImageBase64);
                }
            }
        }
        catch (Exception ex)
        {
            result = PlaygroundPdfFileResult.Error(ex.Message);
        }

        InvokeCallback(callback, result);
    }

    private void InvokeCallback(JsValue callback, PlaygroundPdfFileResult result)
    {
        try
        {
            _host.Engine.Invoke(callback, result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PDF file callback failed: {ex}");
        }
    }

    private static async Task<string> TryCreatePdfPreviewImageBase64Async(IStorageFile file, byte[] bytes)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return string.Empty;
        }

        var quickLookPath = "/usr/bin/qlmanage";
        if (!File.Exists(quickLookPath))
        {
            return string.Empty;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "JavaScriptPlaygroundPdfPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var sourcePath = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                sourcePath = Path.Combine(tempDirectory, "document.pdf");
                await File.WriteAllBytesAsync(sourcePath, bytes);
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = quickLookPath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("-t");
            process.StartInfo.ArgumentList.Add("-s");
            process.StartInfo.ArgumentList.Add("1400");
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(tempDirectory);
            process.StartInfo.ArgumentList.Add(sourcePath);

            if (!process.Start())
            {
                return string.Empty;
            }

            var waitForExit = process.WaitForExitAsync();
            var completed = await Task.WhenAny(waitForExit, Task.Delay(TimeSpan.FromSeconds(8)));
            if (completed != waitForExit)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return string.Empty;
            }

            if (process.ExitCode != 0)
            {
                return string.Empty;
            }

            var imagePath = Directory.EnumerateFiles(tempDirectory, "*.png").FirstOrDefault();
            return imagePath is null ? string.Empty : Convert.ToBase64String(await File.ReadAllBytesAsync(imagePath));
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }
}

internal sealed class PlaygroundPdfFileResult
{
    public bool success { get; private init; }

    public bool cancelled { get; private init; }

    public string name { get; private init; } = string.Empty;

    public string dataBase64 { get; private init; } = string.Empty;

    public string previewImageBase64 { get; private init; } = string.Empty;

    public string error { get; private init; } = string.Empty;

    public static PlaygroundPdfFileResult Success(string name, string dataBase64, string? previewImageBase64 = null) => new()
    {
        success = true,
        name = name ?? string.Empty,
        dataBase64 = dataBase64 ?? string.Empty,
        previewImageBase64 = previewImageBase64 ?? string.Empty
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
