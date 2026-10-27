using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace OpenBoxAchievements
{
    /// Xbox One style "Achievement unlocked" toast: a green circle with the
    /// trophy pops in at the bottom centre, the bar slides out with the text,
    /// holds, then folds back. Click-through and never takes focus.
    sealed class Toast : Window
    {
        public static bool Sound = true;
        static readonly Queue<(Achievement, string, bool)> queue = new Queue<(Achievement, string, bool)>();
        static bool showing;
        const double Height_ = 84, Circle = 84, BarWidth = 520;

        /// `last`: the unlock that completes the game (the Mythic sound).
        public static void Show(Achievement achievement, string icon, bool last = false)
        {
            queue.Enqueue((achievement, icon, last));
            if (!showing) Next();
        }

        static void Next()
        {
            if (queue.Count == 0) { showing = false; return; }
            showing = true;
            var (achievement, icon, last) = queue.Dequeue();
            new Toast(achievement, icon, last).Show();
        }

        readonly Border bar;
        readonly Grid circle;
        readonly StackPanel text;
        readonly Achievement achievement;
        readonly bool last;

        Toast(Achievement achievement, string icon, bool last)
        {
            this.achievement = achievement;
            this.last = last;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = BarWidth + 40;
            Height = Height_ + 40;
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Bottom - Height - 48;

            var root = new Grid();
            bar = new Border
            {
                Height = Height_, Width = Circle, HorizontalAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(Height_ / 2), Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x10, 0x10)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.6 },
            };
            text = new StackPanel { Margin = new Thickness(Circle + 18, 0, 28, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            text.Children.Add(new TextBlock { Text = Lang.T("Achievement unlocked", "Досягнення відкрито"), Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 17 });
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            line.Children.Add(new TextBlock { Text = "Ⓖ " + achievement.Gamerscore, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), FontFamily = new FontFamily("Segoe UI"), FontSize = 16 });
            line.Children.Add(new TextBlock { Text = "  –  " + achievement.Name, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), FontFamily = new FontFamily("Segoe UI"), FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = BarWidth - Circle - 120 });
            text.Children.Add(line);
            var content = new Grid { ClipToBounds = true };
            content.Children.Add(text);
            bar.Child = content;
            root.Children.Add(bar);

            circle = new Grid { Width = Circle, Height = Circle, HorizontalAlignment = HorizontalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0, 0) };
            circle.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)) });
            if (icon != null && File.Exists(icon))
            {
                circle.Children.Add(new Ellipse
                {
                    Margin = new Thickness(5),
                    Fill = new ImageBrush(new BitmapImage(new Uri(icon))) { Stretch = Stretch.UniformToFill },
                });
            }
            else circle.Children.Add(Trophy());
            root.Children.Add(circle);
            Content = root;

            SourceInitialized += (s, e) =>
            {
                // Click-through, no activation, kept out of Alt+Tab.
                var handle = new WindowInteropHelper(this).Handle;
                int style = GetWindowLong(handle, -20);
                SetWindowLong(handle, -20, style | 0x20 | 0x80 | 0x08000000);
            };
            Loaded += (s, e) => Animate();
        }

        static UIElement Trophy() => new System.Windows.Shapes.Path
        {
            Fill = Brushes.White, Stretch = Stretch.Uniform, Margin = new Thickness(22),
            Data = Geometry.Parse("M6,2 H18 V4 H22 V8 C22,11 20,13 17.3,13.6 C16.4,15.5 14.8,16.8 13,17.1 V20 H17 V22 H7 V20 H11 V17.1 C9.2,16.8 7.6,15.5 6.7,13.6 C4,13 2,11 2,8 V4 H6 Z M4,6 V8 C4,9.6 5,11 6.3,11.5 C6.1,10.7 6,9.9 6,9 V6 Z M18,6 V9 C18,9.9 17.9,10.7 17.7,11.5 C19,11 20,9.6 20,8 V6 Z"),
        };

        void Animate()
        {
            var scale = (ScaleTransform)circle.RenderTransform;
            var pop = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            if (Sound && !XboxSound(achievement, last)) Chime();

            // Open at 0.42 s, hold until 5.8 s, fold, fade out.
            double open = 420, opened = 840, fold = 5950, folded = 6330, gone = 6600;
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            var width = new DoubleAnimationUsingKeyFrames();
            width.KeyFrames.Add(new DiscreteDoubleKeyFrame(Circle, Time(0)));
            width.KeyFrames.Add(new DiscreteDoubleKeyFrame(Circle, Time(open)));
            width.KeyFrames.Add(new EasingDoubleKeyFrame(BarWidth, Time(opened), easeOut));
            width.KeyFrames.Add(new DiscreteDoubleKeyFrame(BarWidth, Time(fold)));
            width.KeyFrames.Add(new EasingDoubleKeyFrame(Circle, Time(folded), easeIn));
            bar.BeginAnimation(WidthProperty, width);

            var shifted = new Thickness(0, 0, BarWidth - Circle, 0);
            var margin = new ThicknessAnimationUsingKeyFrames();
            margin.KeyFrames.Add(new DiscreteThicknessKeyFrame(new Thickness(0), Time(open)));
            margin.KeyFrames.Add(new EasingThicknessKeyFrame(shifted, Time(opened), easeOut));
            margin.KeyFrames.Add(new DiscreteThicknessKeyFrame(shifted, Time(fold)));
            margin.KeyFrames.Add(new EasingThicknessKeyFrame(new Thickness(0), Time(folded), easeIn));
            circle.BeginAnimation(MarginProperty, margin);

            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, Time(0)));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, Time(opened - 140)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, Time(opened + 160)));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, Time(fold - 200)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, Time(fold)));
            text.BeginAnimation(OpacityProperty, fade);

            var close = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(gone - folded)) { BeginTime = TimeSpan.FromMilliseconds(folded) };
            close.Completed += (s, e) => { Close(); Next(); };
            BeginAnimation(OpacityProperty, close);
        }

        static KeyTime Time(double milliseconds) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds));

        static MediaPlayer player;

        /// The unlock sound of the Xbox app installed on this PC, read from its
        /// package at run time (nothing is shipped). False when it is missing.
        static bool XboxSound(Achievement achievement, bool last)
        {
            try
            {
                string root = null;
                using (var packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                    if (packages != null)
                        foreach (var name in packages.GetSubKeyNames())
                            if (name.StartsWith("Microsoft.GamingApp_", StringComparison.OrdinalIgnoreCase) && name.Contains("_x64_"))
                                using (var package = packages.OpenSubKey(name))
                                    root = package?.GetValue("PackageRootFolder") as string ?? root;
                if (root == null) return false;
                bool rare = achievement.RarityPercent > 0 && achievement.RarityPercent < 10;
                string folder = System.IO.Path.Combine(root, "SocialPublicAssets", "Notifications");
                string file = System.IO.Path.Combine(folder, last ? "MythicAchievementUnlocked.mp3" :
                                                     rare ? "RareAchievementUnlocked.mp3" : "AchievementUnlocked.mp3");
                if (!File.Exists(file)) file = System.IO.Path.Combine(folder, "AchievementUnlocked.mp3");
                if (!File.Exists(file)) return false;
                player = new MediaPlayer();
                player.Open(new Uri(file));
                player.Play();
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                                          error is System.Security.SecurityException || error is InvalidOperationException)
            {
                return false;
            }
        }

        /// A short two-note chime, synthesised (no Microsoft audio is used).
        static void Chime()
        {
            const int rate = 44100;
            var samples = new List<short>();
            foreach (var (frequency, length) in new[] { (880.0, 0.11), (1318.5, 0.32) })
            {
                int count = (int)(rate * length);
                for (int i = 0; i < count; ++i)
                {
                    double t = (double)i / rate, envelope = Math.Min(1, t * 80) * Math.Exp(-t * 7);
                    double value = Math.Sin(2 * Math.PI * frequency * t) * 0.6 + Math.Sin(4 * Math.PI * frequency * t) * 0.15;
                    samples.Add((short)(value * envelope * 9000));
                }
            }
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);
            int bytes = samples.Count * 2;
            writer.Write(0x46464952); writer.Write(36 + bytes); writer.Write(0x45564157);
            writer.Write(0x20746D66); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(0x61746164); writer.Write(bytes);
            foreach (var s in samples) writer.Write(s);
            stream.Position = 0;
            new SoundPlayer(stream).Play();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr handle, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr handle, int index, int value);
    }
}
