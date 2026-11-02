using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Ideas page's pasted and dropped images: the target editor, the upload and what the hint under it says.
internal sealed partial class IdeasView<TSurface>
{
    // The box an image goes to: the open Edit's, else the note box; none on a page that cannot send.
    private TextEditorState? PasteTarget => Send is null || Upload is null ? null : _open is { Mode: Mode.Edit } ? EditInput : NoteInput;

    /// <summary>Ctrl+V in <paramref name="editor"/>: an image on the clipboard is uploaded and its marker put at the caret. False when it holds none, and the paste is the text's.</summary>
    internal bool PasteImage(TextEditorState editor)
    {
        if (Upload is null || GetClipboardImage?.Invoke() is not { } image)
        {
            return false;
        }
        Run(() => UploadAsync(editor, image.Bytes, image.Ext), "upload image");
        return true;
    }

    /// <summary>A file dropped on the window: an image goes into the open Edit or the note box like a paste; anything else is ignored. True when it was taken.</summary>
    public bool DropFile(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (PasteTarget is not { } editor || !ImageExtensions.Contains(ext))
        {
            return false;
        }
        Run(async () =>
        {
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PasteSay(editor, $"image not read: {ex.Message}");
                return;
            }
            await UploadAsync(editor, bytes, ext);
        }, "upload image");
        return true;
    }

    private async Task UploadAsync(TextEditorState editor, byte[] bytes, string ext)
    {
        Interlocked.Increment(ref _uploading);
        _pasteSaid = null;
        Changed?.Invoke();
        (string? Id, string? Error) result;
        try
        {
            result = await Upload!(bytes, ext, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            result = (null, ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _uploading);
        }
        if (result.Id is { Length: > 0 } id)
        {
            System.Collections.Immutable.ImmutableInterlocked.Enqueue(ref _markers, (editor, IdeaImages.Marker("screenshot", id, ext.ToLowerInvariant())));
            Changed?.Invoke();
        }
        else
        {
            PasteSay(editor, $"image not uploaded: {result.Error ?? "the node gave no id"}");
        }
    }

    private void PasteSay(TextEditorState editor, string text)
    {
        _pasteSaid = text;
        _pasteSaidIn = editor;
        Changed?.Invoke();
    }

    // The line under a box: while an image goes up it says so, a refused one is said under the box it was pasted in.
    private string Hint(TextEditorState editor, string idle, out RGBAColor32 color)
    {
        if (Volatile.Read(ref _uploading) > 0)
        {
            color = P.Held;
            return "● uploading image…";
        }
        if (_pasteSaid is { } said && ReferenceEquals(_pasteSaidIn, editor))
        {
            color = P.Failed;
            return said;
        }
        color = P.Dim;
        return idle;
    }
}
