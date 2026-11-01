using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

/// <summary>
/// The idea images' routes: a client uploads an image it pasted into an idea (<c>POST /api/v1/attachments?ext=</c>), and the
/// node serves one by its id, from its own copy or, for one only GitHub holds, from the repo's <see cref="IdeaImages.Branch"/> branch.
/// </summary>
internal static class IdeaImageRoutes
{
    public static void Map(WebApplication app) =>
        app.MapPost(NodeWire.AttachmentsRoute, async (HttpContext context, string? ext, Attachments attachments, CancellationToken cancellationToken) =>
        {
            IResult Refuse(string why, int status) =>
                Results.Json(ResponseEnvelope<string>.Fail(why, status), PilotJsonContext.Default.ResponseEnvelopeString, statusCode: status);

            if (Attachments.IdeaImageExtension(ext) is not { } kind)
            {
                return Refuse("ext is the image's extension: png, jpg, jpeg, gif, webp or bmp", 400);
            }
            if (context.Request.ContentLength > IdeaImages.MaxBytes)
            {
                return Refuse($"an idea's image is at most {IdeaImages.MaxBytes / (1024 * 1024)} MB", 413);
            }
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (body.Length + read > IdeaImages.MaxBytes)
                {
                    return Refuse($"an idea's image is at most {IdeaImages.MaxBytes / (1024 * 1024)} MB", 413);
                }
                body.Write(buffer, 0, read);
            }
            if (body.Length == 0)
            {
                return Refuse("the image's bytes are the request's body", 400);
            }
            try
            {
                return Results.Json(ResponseEnvelope<string>.Ok(attachments.AddIdeaImage(body.ToArray(), kind)), PilotJsonContext.Default.ResponseEnvelopeString);
            }
            catch (FlightException ex)
            {
                return Refuse(ex.Message, 500);
            }
        });

    /// <summary>The idea image <paramref name="id"/> as a file; 404 when the node holds none and no idea names one on GitHub.</summary>
    public static async Task<IResult> ServeAsync(string id, NodeState state, Attachments attachments, RepoCatalog repos, IGitHub github, CancellationToken cancellationToken)
    {
        if (attachments.FindIdeaImage(id) is { } held)
        {
            return Results.File(held.Path, Attachments.ContentType(held.Ext));
        }
        foreach (var idea in state.Ideas())
        {
            foreach (var image in idea.Images.Where(i => i.Copy == id))
            {
                if (IdeaImages.CopyOf(image.Url) is not { } copy || IdeaImages.MarkerIn(image.Url) is not null || Attachments.IdeaImageExtension(copy.Ext) is not { } ext
                    || repos.Repos().FirstOrDefault(r => string.Equals(r.Name, idea.Repo, StringComparison.OrdinalIgnoreCase)) is not { } repo)
                {
                    continue;
                }
                try
                {
                    if (await github.IdeaImageAsync(repo.Path, id, ext, cancellationToken) is { } bytes)
                    {
                        attachments.SaveIdeaImage(id, ext, bytes);
                        return Results.File(attachments.PathOf(Attachments.IdeaOwner, id, ext), Attachments.ContentType(ext));
                    }
                }
                catch (FlightException)
                {
                    // GitHub cannot be asked now: no copy, and the next request tries again.
                }
            }
        }
        return Results.NotFound();
    }
}
