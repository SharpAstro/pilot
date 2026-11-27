using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pilot.Node.Toasts;

/// <summary>Shows and withdraws the node's desktop toasts (#1356); a fake records what it is asked, the real one calls the WinRT toast API in process.</summary>
public interface IToastRunner
{
    /// <summary>Shows the toast <paramref name="xml"/> under <paramref name="tag"/>, in group <c>pilot</c>; a toast of the same tag is replaced.</summary>
    Task Show(string tag, string xml, CancellationToken cancel = default);

    /// <summary>Withdraws the toast of <paramref name="tag"/>, in group <c>pilot</c>, from the notification centre.</summary>
    Task Remove(string tag, CancellationToken cancel = default);
}

/// <summary>
/// Shows and withdraws toasts by calling the WinRT toast API in process (#1376), under pilot's own AUMID (<see cref="PilotAumid"/>), where
/// the PowerShell runner rested on Windows PowerShell 5.1 having the WinRT projection (it fails on some machines with 0x80131047). The calls
/// go through the raw ABI by function pointers on each interface's vtable, as <c>MachineNative</c> does for DXGI: AOT-safe, no CsWinRT, no
/// TFM change. A vtable starts with IUnknown (3 slots) and IInspectable (3 more), so an interface's own methods begin at slot 6. A failed
/// HRESULT throws an <see cref="InvalidOperationException"/> naming the call. The calls run off the caller's thread and hold no lock. A
/// no-op off Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WinRtToastRunner : IToastRunner
{
    /// <summary>The group every toast of the node is in, so a tag stands for one card.</summary>
    public const string Group = "pilot";

    private static readonly Guid IidXmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");
    private static readonly Guid IidXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static readonly Guid IidToastNotificationFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid IidToastNotification2 = new("9DFB9FD1-143A-490E-90BF-B9FBA7132DE7");
    private static readonly Guid IidManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    private static readonly Guid IidManagerStatics2 = new("7AB93C52-0E48-4750-BA9D-1A4113981847");
    private static readonly Guid IidToastNotifier = new("75927B93-03F3-41EC-91D3-6E5BAC1B38E7");
    private static readonly Guid IidHistory = new("5CADDC63-01D3-4C97-986F-0533483FEE14");

    private const string XmlDocumentClass = "Windows.Data.Xml.Dom.XmlDocument";
    private const string ToastNotificationClass = "Windows.UI.Notifications.ToastNotification";
    private const string ManagerClass = "Windows.UI.Notifications.ToastNotificationManager";

    private const int RpcEChangedMode = unchecked((int)0x80010106);

    /// <inheritdoc />
    public Task Show(string tag, string xml, CancellationToken cancel = default) =>
        OperatingSystem.IsWindows() ? Task.Run(() => ShowCore(tag, xml), cancel) : Task.CompletedTask;

    /// <inheritdoc />
    public Task Remove(string tag, CancellationToken cancel = default) =>
        OperatingSystem.IsWindows() ? Task.Run(() => RemoveCore(tag), cancel) : Task.CompletedTask;

    private static void ShowCore(string tag, string xml)
    {
        using var refs = new Refs();
        var toast = Build(refs, tag, xml);
        var statics = Factory(refs, ManagerClass, IidManagerStatics);
        var notifier = Call(refs, "IToastNotificationManagerStatics.CreateToastNotifierWithId", (o) =>
        {
            var aumid = refs.HString(PilotAumid.Id);
            return ((delegate* unmanaged<nint, nint, nint*, int>)Slot(statics, 7))(statics, aumid, o);
        });
        Check(((delegate* unmanaged<nint, nint, int>)Slot(notifier, 6))(notifier, toast), "IToastNotifier.Show");
    }

    private static void RemoveCore(string tag)
    {
        using var refs = new Refs();
        var statics2 = Factory(refs, ManagerClass, IidManagerStatics2);
        var history = Call(refs, "IToastNotificationManagerStatics2.get_History", (o) =>
            ((delegate* unmanaged<nint, nint*, int>)Slot(statics2, 6))(statics2, o));
        var hTag = refs.HString(tag);
        var hGroup = refs.HString(Group);
        var hAumid = refs.HString(PilotAumid.Id);
        Check(((delegate* unmanaged<nint, nint, nint, nint, int>)Slot(history, 8))(history, hTag, hGroup, hAumid),
            "IToastNotificationHistory.RemoveGroupedTagWithId");
    }

    /// <summary>
    /// Builds the notification of <paramref name="xml"/>, sets its tag and group, and reads both back; shows nothing. What a test calls to
    /// prove the interface ids and the vtable slots in milliseconds with no process and no toast.
    /// </summary>
    internal static (string Tag, string Group) BuildAndReadBack(string tag, string xml)
    {
        using var refs = new Refs();
        var toast = Build(refs, tag, xml);
        var toast2 = Qi(refs, toast, IidToastNotification2, "IToastNotification.QueryInterface(IToastNotification2)");
        return (ReadString(refs, toast2, 7, "IToastNotification2.get_Tag"), ReadString(refs, toast2, 9, "IToastNotification2.get_Group"));
    }

    private static nint Build(Refs refs, string tag, string xml)
    {
        Initialize();
        var instance = Call(refs, "RoActivateInstance(XmlDocument)", (o) =>
            Native.RoActivateInstance(refs.HString(XmlDocumentClass), o));
        var io = Qi(refs, instance, IidXmlDocumentIO, "XmlDocument.QueryInterface(IXmlDocumentIO)");
        Check(((delegate* unmanaged<nint, nint, int>)Slot(io, 6))(io, refs.HString(xml)), "IXmlDocumentIO.LoadXml");
        var document = Qi(refs, instance, IidXmlDocument, "XmlDocument.QueryInterface(IXmlDocument)");

        var factory = Factory(refs, ToastNotificationClass, IidToastNotificationFactory);
        var toast = Call(refs, "IToastNotificationFactory.CreateToastNotification", (o) =>
            ((delegate* unmanaged<nint, nint, nint*, int>)Slot(factory, 6))(factory, document, o));

        var toast2 = Qi(refs, toast, IidToastNotification2, "IToastNotification.QueryInterface(IToastNotification2)");
        Check(((delegate* unmanaged<nint, nint, int>)Slot(toast2, 6))(toast2, refs.HString(tag)), "IToastNotification2.put_Tag");
        Check(((delegate* unmanaged<nint, nint, int>)Slot(toast2, 8))(toast2, refs.HString(Group)), "IToastNotification2.put_Group");
        return toast;
    }

    // The MTA; a pool thread usually has it already (S_FALSE), and a thread that chose its own apartment (RPC_E_CHANGED_MODE) still can call WinRT.
    private static void Initialize()
    {
        const int MtaType = 1;
        var hr = Native.RoInitialize(MtaType);
        if (hr < 0 && hr != RpcEChangedMode)
        {
            Check(hr, "RoInitialize");
        }
    }

    private static nint Factory(Refs refs, string className, Guid iid) =>
        Call(refs, $"RoGetActivationFactory({className})", (o) =>
        {
            var id = iid;
            return Native.RoGetActivationFactory(refs.HString(className), &id, o);
        });

    private static nint Qi(Refs refs, nint obj, Guid iid, string call) =>
        Call(refs, call, (o) =>
        {
            var id = iid;
            return ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, &id, o);
        });

    private static string ReadString(Refs refs, nint obj, int slot, string call)
    {
        var h = Call(refs, call, (o) => ((delegate* unmanaged<nint, nint*, int>)Slot(obj, slot))(obj, o));
        uint length;
        var raw = Native.WindowsGetStringRawBuffer(h, &length);
        return new string(raw, 0, (int)length);
    }

    private delegate int Producer(nint* result);

    // Runs a call that yields a pointer; checks its HRESULT, and hands the pointer to refs so it is released in the finally of the caller's using.
    private static nint Call(Refs refs, string call, Producer produce)
    {
        nint result;
        var hr = produce(&result);
        Check(hr, call);
        refs.Own(result);
        return result;
    }

    private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];

    private static void Check(int hr, string call)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{call} failed: HRESULT 0x{hr:X8}");
        }
    }

    /// <summary>The pointers and strings one call made, released in reverse order when it ends (success or not).</summary>
    private sealed class Refs : IDisposable
    {
        private readonly List<(nint Pointer, bool IsString)> _owned = [];

        public void Own(nint pointer)
        {
            if (pointer != 0) _owned.Add((pointer, false));
        }

        public nint HString(string text)
        {
            fixed (char* chars = text)
            {
                nint h;
                var hr = Native.WindowsCreateString(chars, (uint)text.Length, &h);
                Check(hr, "WindowsCreateString");
                _owned.Add((h, true));
                return h;
            }
        }

        public void Dispose()
        {
            for (var i = _owned.Count - 1; i >= 0; i--)
            {
                var (pointer, isString) = _owned[i];
                if (isString) Native.WindowsDeleteString(pointer);
                else ((delegate* unmanaged<nint, uint>)Slot(pointer, 2))(pointer);
            }
            _owned.Clear();
        }
    }

    private static class Native
    {
        [DllImport("combase.dll")]
        public static extern int RoInitialize(int initType);

        [DllImport("combase.dll")]
        public static extern int RoActivateInstance(nint activatableClassId, nint* instance);

        [DllImport("combase.dll")]
        public static extern int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);

        [DllImport("combase.dll")]
        public static extern int WindowsCreateString(char* source, uint length, nint* hstring);

        [DllImport("combase.dll")]
        public static extern int WindowsDeleteString(nint hstring);

        [DllImport("combase.dll")]
        public static extern char* WindowsGetStringRawBuffer(nint hstring, uint* length);
    }
}
