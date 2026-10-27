using Microsoft.Gaming.XboxGameBar;
using System;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace OpenBoxWidget
{
    /// Started only by Xbox Game Bar (ms-gamebarwidget activation): one window
    /// per widget instance, holding the achievements page.
    sealed partial class App : Application
    {
        XboxGameBarWidget widget;

        public App()
        {
            InitializeComponent();
            // Gamepad focus (XY navigation) rather than a mouse-like cursor.
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            XboxGameBarWidgetActivatedEventArgs widgetArgs = null;
            if (args.Kind == ActivationKind.Protocol &&
                args is IProtocolActivatedEventArgs protocol &&
                protocol.Uri.Scheme.Equals("ms-gamebarwidget", StringComparison.OrdinalIgnoreCase))
                widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
            if (widgetArgs == null || !widgetArgs.IsLaunchActivation) return;
            var frame = new Frame();
            Window.Current.Content = frame;
            widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, frame);
            frame.Navigate(typeof(WidgetPage), widget);
            Window.Current.Closed += (s, e) => widget = null;
            Window.Current.Activate();
        }

        /// Opened from the Start menu instead of Game Bar: a note to use Win+G.
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var frame = new Frame();
            Window.Current.Content = frame;
            frame.Navigate(typeof(WidgetPage), null);
            Window.Current.Activate();
        }
    }
}
