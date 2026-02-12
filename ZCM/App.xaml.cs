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
        return new Window(new AppShell());
    }
}
