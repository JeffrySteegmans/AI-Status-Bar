using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIStatusBar;

public sealed class App : Application
{
    readonly SessionStore store;
    readonly BarOptions opt;
    BarWindow? window;

    public App(SessionStore store, BarOptions opt)
    {
        this.store = store;
        this.opt = opt;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new BarWindow(store, opt);
    }
}
