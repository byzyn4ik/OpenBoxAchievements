using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenBoxAchievements
{
    /// Controller for the app's ordinary windows (Settings, the wizard):
    /// D-pad/left stick move focus, A presses or toggles, B closes. Only the
    /// window in front reads the pad.
    static class Pad
    {
        [StructLayout(LayoutKind.Sequential)]
        struct State { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

        [DllImport("xinput1_4.dll")]
        static extern int XInputGetState(int user, out State state);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        static ushort Read(out short lx, out short ly)
        {
            ushort buttons = 0;
            lx = ly = 0;
            for (int user = 0; user < 4; ++user)
            {
                if (XInputGetState(user, out var state) != 0) continue;
                buttons |= state.Buttons;
                if (Math.Abs((int)state.LX) > Math.Abs((int)lx)) lx = state.LX;
                if (Math.Abs((int)state.LY) > Math.Abs((int)ly)) ly = state.LY;
            }
            return buttons;
        }

        public static void Attach(Window window)
        {
            // Buttons held when the window opens (the press that opened it) do nothing.
            ushort last = Read(out _, out _);
            int held = -1;
            long next = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (s, e) =>
            {
                ushort buttons = Read(out short lx, out short ly);
                if (GetForegroundWindow() != new System.Windows.Interop.WindowInteropHelper(window).Handle) { last = buttons; return; }
                ushort pressed = (ushort)(buttons & ~last);
                last = buttons;
                if ((pressed & 0x2000) != 0) { window.Close(); return; }                     // B
                if ((pressed & 0x1000) != 0)                                                 // A
                {
                    switch (Keyboard.FocusedElement)
                    {
                        case RadioButton radio: radio.IsChecked = true; break;
                        case ToggleButton toggle: toggle.IsChecked = toggle.IsChecked != true; break;
                        case ButtonBase button: button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
                        default: First(window); break;
                    }
                    return;
                }
                int direction = (buttons & 0x0001) != 0 || ly > 16000 ? 0 : (buttons & 0x0002) != 0 || ly < -16000 ? 1
                    : (buttons & 0x0004) != 0 || lx < -16000 ? 2 : (buttons & 0x0008) != 0 || lx > 16000 ? 3 : -1;
                long now = Environment.TickCount;
                if (direction < 0) { held = -1; return; }
                if (direction == held && now < next) return;
                next = now + (direction == held ? 110 : 320);
                held = direction;
                if (!window.IsKeyboardFocusWithin || !(Keyboard.FocusedElement is UIElement focused)) { First(window); return; }
                var way = new[] { FocusNavigationDirection.Up, FocusNavigationDirection.Down, FocusNavigationDirection.Left, FocusNavigationDirection.Right }[direction];
                if (!focused.MoveFocus(new TraversalRequest(way)))
                    focused.MoveFocus(new TraversalRequest(direction == 0 || direction == 2 ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
            };
            window.Loaded += (s, e) => timer.Start();
            window.Closed += (s, e) => timer.Stop();
        }

        static void First(Window window) => window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
}
