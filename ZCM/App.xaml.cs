using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using ZCM.Controls;

namespace ZCM;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        // Enable text selection for SelectableLabel
        LabelHandler.Mapper.AppendToMapping(
            "SelectableLabelMapping",
            (handler, view) =>
            {
                if (view is SelectableLabel)
                {
#if WINDOWS
                    handler.PlatformView.IsTextSelectionEnabled = true;
#elif ANDROID
                    handler.PlatformView.SetTextIsSelectable(true);
#endif
                }
            });

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[UNHANDLED] {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[TASK] {e.Exception}");
            e.SetObserved();
        };
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());

#if WINDOWS
        window.HandlerChanged += (s, e) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

                if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                }
            }
        };
#endif

        return window;
    }
}


