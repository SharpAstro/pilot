using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Pilot.Buddy;

/// <summary>What the window knows of its GPU side (the renderer exposes no counters, so the host says what it asked for and what it can read).</summary>
/// <param name="SwapchainWidth">Swapchain width in pixels.</param>
/// <param name="SwapchainHeight">Swapchain height in pixels.</param>
/// <param name="SwapchainImages">Images in the swapchain, or null when the renderer does not say (it picks the driver's minimum plus one).</param>
/// <param name="DepthBuffer">Whether the main target carries a depth image.</param>
/// <param name="SdfPageDim">The side of one SDF atlas page, which the buddy asks for.</param>
/// <param name="WarmedGlyphs">How many different glyphs the cat has drawn so far.</param>
/// <param name="Alpha">How the window blends with the desktop (the renderer's AlphaPresentation).</param>
public sealed record BuddyGpuFacts(int SwapchainWidth, int SwapchainHeight, int? SwapchainImages, bool DepthBuffer, int SdfPageDim, int WarmedGlyphs, string Alpha);

/// <summary>What the mirror holds, counted.</summary>
public sealed record BuddyMirrorCounts(int Cards, int Sessions, int Flights, int Legs, int Merges, int Jobs, int Issues);

/// <summary>
/// The report <c>buddy_memory</c> serves (#1381 section 11): the managed heap (<see cref="GC.GetGCMemoryInfo()"/>), the process's private bytes and working
/// set, the font atlas (one R8 SDF page of the configured side, plus the bitmap atlas's first page), the swapchain and its depth buffer, and the mirror's
/// sizes. Read from the running cat, so the numbers are the live ones. Sources are injected so a test runs on fakes.
/// </summary>
public sealed class BuddyMemory(Func<BuddyGpuFacts?> gpu, Func<BuddyMirrorCounts> mirror, Func<(long Private, long WorkingSet)>? process = null, Func<GCMemoryInfo>? gc = null)
{
    /// <summary>The bitmap (emoji) atlas starts as one 512 x 512 RGBA page.</summary>
    private const long BitmapAtlasBytes = 512L * 512 * 4;

    private static (long Private, long WorkingSet) ReadProcess()
    {
        using var p = Process.GetCurrentProcess();
        return (p.PrivateMemorySize64, p.WorkingSet64);
    }

    /// <summary>The report as the JSON the tool answers with.</summary>
    public string Report()
    {
        var (privateBytes, workingSet) = (process ?? ReadProcess)();
        var info = (gc ?? (() => GC.GetGCMemoryInfo()))();
        var facts = gpu();
        var counts = mirror();
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("privateBytes", privateBytes);
            w.WriteNumber("workingSetBytes", workingSet);
            w.WriteStartObject("managedHeap");
            w.WriteNumber("heapSizeBytes", info.HeapSizeBytes);
            w.WriteNumber("committedBytes", info.TotalCommittedBytes);
            w.WriteNumber("fragmentedBytes", info.FragmentedBytes);
            // The live heap now (the figures above describe the last GC), and the settings the runtime really runs with, so the baked ones can be seen to have taken.
            w.WriteNumber("liveBytes", GC.GetTotalMemory(false));
            w.WriteBoolean("serverGc", System.Runtime.GCSettings.IsServerGC);
            w.WriteString("latencyMode", System.Runtime.GCSettings.LatencyMode.ToString());
            w.WriteNumber("collectionsGen0", GC.CollectionCount(0));
            w.WriteNumber("collectionsGen2", GC.CollectionCount(2));
            w.WriteEndObject();
            if (facts is null)
            {
                w.WriteNull("gpu");
            }
            else
            {
                w.WriteStartObject("gpu");
                w.WriteStartObject("fontAtlas");
                w.WriteNumber("bytes", (long)facts.SdfPageDim * facts.SdfPageDim + BitmapAtlasBytes);
                w.WriteNumber("sdfPageDim", facts.SdfPageDim);
                w.WriteNumber("glyphsDrawn", facts.WarmedGlyphs);
                w.WriteEndObject();
                w.WriteStartObject("swapchain");
                w.WriteNumber("width", facts.SwapchainWidth);
                w.WriteNumber("height", facts.SwapchainHeight);
                if (facts.SwapchainImages is { } images)
                {
                    w.WriteNumber("images", images);
                }
                else
                {
                    w.WriteNull("images");
                }
                w.WriteNumber("bytesPerImage", (long)facts.SwapchainWidth * facts.SwapchainHeight * 4);
                w.WriteEndObject();
                w.WriteBoolean("depthBuffer", facts.DepthBuffer);
                w.WriteString("alpha", facts.Alpha);
                w.WriteEndObject();
            }
            w.WriteStartObject("mirror");
            w.WriteNumber("cards", counts.Cards);
            w.WriteNumber("sessions", counts.Sessions);
            w.WriteNumber("flights", counts.Flights);
            w.WriteNumber("legs", counts.Legs);
            w.WriteNumber("merges", counts.Merges);
            w.WriteNumber("jobs", counts.Jobs);
            w.WriteNumber("issues", counts.Issues);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
