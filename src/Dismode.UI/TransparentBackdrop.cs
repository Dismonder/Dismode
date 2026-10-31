using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SystemDispatcherQueue = global::Windows.System.DispatcherQueue;
using SystemDispatcherQueueController = global::Windows.System.DispatcherQueueController;
using WinComposition = global::Windows.UI.Composition;

namespace Dismode.UI;

// Tlo okna, ktorego nie widac.
//
// WinUI 3 rysuje okno przez DirectComposition, wiec klucz koloru okna
// warstwowego (LWA_COLORKEY) go nie dotyczy: czarne tlo zostawalo czarne.
// Przezroczystosc trzeba oddac kompozytorowi systemu jako pedzel tla okna,
// tak jak robi to Mica, tylko z kolorem w pelni przezroczystym.
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private static SystemDispatcherQueueController? s_dispatcherQueueController;
    private WinComposition.Compositor? _compositor;

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        EnsureSystemDispatcherQueue();
        _compositor ??= new WinComposition.Compositor();
        connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(
            global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(
        ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }

    // Kompozytor systemu wymaga kolejki Windows.System na watku, a watek
    // WinUI ma tylko kolejke Microsoft.UI.Dispatching.
    private static void EnsureSystemDispatcherQueue()
    {
        if (SystemDispatcherQueue.GetForCurrentThread() is not null
            || s_dispatcherQueueController is not null)
        {
            return;
        }

        DispatcherQueueOptions options = new()
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = DqtypeThreadCurrent,
            ApartmentType = DqtatComNone,
        };
        Marshal.ThrowExceptionForHR(
            CreateDispatcherQueueController(options, out nint controller));
        s_dispatcherQueueController =
            SystemDispatcherQueueController.FromAbi(controller);
    }

    private const int DqtypeThreadCurrent = 2;
    private const int DqtatComNone = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        out nint dispatcherQueueController);
}
