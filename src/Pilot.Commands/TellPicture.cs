using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// A picture that goes with a tell (PIL1366), and the file checks a card's image shares: one helper for the CLI (<c>pilot tell --attachment</c>)
/// and the GUI's chat. Everything is checked before anything is uploaded, and a node whose wire cannot take a picture is refused in words,
/// since an older node would drop the picture without a word.
/// </summary>
public static class TellPicture
{
    /// <summary>The wire that first carries a tell's picture.</summary>
    public const int MinWire = 32;

    /// <summary>The image extensions the node keeps.</summary>
    public static readonly string[] Extensions = ["png", "jpg", "jpeg", "gif", "webp", "bmp"];

    /// <summary>Why <paramref name="node"/> cannot take a tell's picture, or null when it can.</summary>
    public static string? WireProblem(NodeInfo node) =>
        node.WireVersion >= MinWire
            ? null
            : $"that node runs an older pilot (wire {node.WireVersion}, a picture needs {MinWire}) that cannot take a picture with a tell: update it, or tell without the picture";

    /// <summary>Why a picture named <paramref name="name"/> of <paramref name="length"/> bytes with extension <paramref name="ext"/> cannot be sent, or null.</summary>
    public static string? Problem(string name, string? ext, long length)
    {
        if (ext is null || !Extensions.Contains(ext.TrimStart('.').ToLowerInvariant()))
        {
            return $"{name} is not an image: use a {string.Join(", ", Extensions)} file";
        }
        if (length == 0)
        {
            return $"{name} is empty";
        }
        return length > IdeaImages.MaxBytes
            ? $"{name} is {length / (1024 * 1024)} MB: a picture is at most {IdeaImages.MaxBytes / (1024 * 1024)} MB"
            : null;
    }

    /// <summary>
    /// A file's bytes uploaded to the node: the <c>&lt;id&gt;.&lt;ext&gt;</c> a command's <see cref="CommandRequest.Attachment"/> takes. The file is
    /// checked first (an image, not empty, within the size), so a refused one uploads nothing. A problem is said in words.
    /// </summary>
    public static async Task<(string? Image, string? Problem)> UploadFileAsync(NodeClient client, string path, string label, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (Problem(label, ext, new FileInfo(path).Length) is { } problem)
        {
            return (null, problem);
        }
        return await UploadAsync(client, await File.ReadAllBytesAsync(path, cancellationToken), ext, label, cancellationToken);
    }

    /// <summary>Uploads <paramref name="bytes"/> (checked first) and answers the <c>&lt;id&gt;.&lt;ext&gt;</c> to send.</summary>
    public static async Task<(string? Image, string? Problem)> UploadAsync(NodeClient client, byte[] bytes, string ext, string label, CancellationToken cancellationToken)
    {
        if (Problem(label, ext, bytes.Length) is { } problem)
        {
            return (null, problem);
        }
        ext = ext.TrimStart('.').ToLowerInvariant();
        var uploaded = await client.UploadAttachmentAsync(bytes, ext, cancellationToken);
        return uploaded is { Success: true, Response: { Length: > 0 } id }
            ? ($"{id}.{ext}", null)
            : (null, $"could not upload {label}: {uploaded.Error}");
    }

    /// <summary>
    /// Tells the watchtower (or <c>card/&lt;id&gt;</c>) <paramref name="words"/> with a picture: the node's wire is asked first, the picture is
    /// checked, uploaded to this same client, and the tell sent with it under <paramref name="requestId"/>. A refusal comes back as the problem
    /// and nothing is uploaded or told.
    /// </summary>
    public static async Task<(CommandResult? Result, string? Problem)> SendAsync(NodeClient client, string target, string words, byte[] bytes, string ext, string who, string requestId, CancellationToken cancellationToken)
    {
        if (WireProblem(await client.GetNodeAsync(cancellationToken)) is { } old)
        {
            return (null, old);
        }
        var (image, problem) = await UploadAsync(client, bytes, ext, "the picture", cancellationToken);
        if (image is null)
        {
            return (null, problem);
        }
        var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = target, Args = [words], Who = who, RequestId = requestId, Attachment = image }, cancellationToken);
        return envelope.Response is { } result
            ? (result, result.Error)
            : (null, envelope.Error ?? "the node did not answer the tell");
    }
}
