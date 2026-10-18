// SPIKE: does the stack open a window on win-arm64 under AOT and draw text, and does --capture write a PNG.
using DIR.Lib;
using SdlVulkan.Renderer;
using SharpAstro.Png;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

if (args is ["--capture", var outPath, ..])
{
    using var cpu = new RgbaImageRenderer(1280, 800);
    var view = new SpikeView<RgbaImage>(cpu) { FontPath = fontPath };
    view.Render(new RectF32(0, 0, 1280, 800));
    File.WriteAllBytes(outPath, PngWriter.Encode(cpu.Surface.Pixels, 1280, 800));
    Console.WriteLine($"wrote {outPath}");
    return 0;
}

if (args is ["--gpu-capture", var gpuOut, ..])
{
    // The window's renderer (VkRenderer on the device the window would use), into an image instead of a swapchain.
    var instance = Spike.CreateInstance();
    using var octx = VulkanContext.CreateOffscreen(instance, 1280, 800);
    using var orenderer = new VkRenderer(octx, 1280, 800);
    var oview = new SpikeView<VulkanContext>(orenderer) { FontPath = fontPath };
    // Twice: the first frame rasterises the glyphs into the atlas's staging, the second draws them uploaded.
    for (var frame = 0; frame < 2; frame++)
    {
        orenderer.BeginOffscreenFrame(new RGBAColor32(0x12, 0x12, 0x18, 0xff));
        oview.Render(new RectF32(0, 0, 1280, 800));
        orenderer.EndOffscreenFrame();
        octx.WaitOffscreenFrameComplete();
    }
    File.WriteAllBytes(gpuOut, PngWriter.Encode(octx.ReadbackOffscreenRgba(), 1280, 800));
    Console.WriteLine($"wrote {gpuOut}");
    return 0;
}

using var window = SdlVulkanWindow.Create("pilot v2 spike", 1280, 800);
window.GetSizeInPixels(out var w, out var h);
using var ctx = VulkanContext.Create(window.Instance, window.Surface, (uint)w, (uint)h);
using var renderer = new VkRenderer(ctx, (uint)w, (uint)h);
var gpuView = new SpikeView<VulkanContext>(renderer) { FontPath = fontPath, DpiScale = window.DisplayScale };
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var loop = new SdlEventLoop(window, renderer)
{
    BackgroundColor = new RGBAColor32(0x12, 0x12, 0x18, 0xff),
    OnResize = (rw, rh) => { w = (int)rw; h = (int)rh; },
    OnRender = () => gpuView.Render(new RectF32(0, 0, w, h)),
    CheckNeedsRedraw = () => renderer.FontAtlasDirty,
};
Console.WriteLine("window open");
loop.Run(cts.Token);
Console.WriteLine("window closed");
return 0;

static unsafe class Spike
{
    public static VkInstance CreateInstance()
    {
        vkInitialize().CheckResult();
        VkInstanceCreateInfo ici = new();
        vkCreateInstance(&ici, null, out var instance).CheckResult();
        return instance;
    }
}

sealed class SpikeView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    public void Render(RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(
            Layout.Builder.VStack(
                Layout.Builder.Text("pilot v2 spike: text on the stack", 24f, new RGBAColor32(0xe0, 0xe0, 0xe8, 0xff)).RowH(40f),
                Layout.Builder.Spacer().Stretch())
            .Bg(new RGBAColor32(0x12, 0x12, 0x18, 0xff)).Pad(16f),
            bounds);
    }
}
