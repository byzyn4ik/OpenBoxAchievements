using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

namespace OpenBoxWidget
{
    /// The widget, laid out as the native Xbox Achievements one: the game with
    /// its gamerscore and count, a filter and sort, then every achievement
    /// with its points, progress line and unlock date. The data is
    /// LocalState\summary.json, written by OpenBox Achievements and rechecked
    /// every 3 s; "All achievements" opens OpenBox's own page.
    public sealed partial class WidgetPage : Page
    {
        static readonly SolidColorBrush White = new SolidColorBrush(Colors.White);
        static readonly SolidColorBrush Muted = new SolidColorBrush(Color.FromArgb(0xc0, 0xff, 0xff, 0xff));
        static readonly SolidColorBrush Track = new SolidColorBrush(Color.FromArgb(0x50, 0xff, 0xff, 0xff));
        static readonly SolidColorBrush Dark = new SolidColorBrush(Color.FromArgb(0xff, 0x2a, 0x2b, 0x2f));
        const string TrophyPath = "M7,3 H17 V9 A5,5 0 0 1 7,9 Z M7,5 H4 V7 A3,3 0 0 0 7,10 M17,5 H20 V7 A3,3 0 0 1 17,10 M12,14 V18 M8,21 H16 M9,18 H15 V21";
        const string DiamondPath = "M6,3 H18 L22,9 L12,21 L2,9 Z M2,9 H22 M9,3 L7,9 L12,21 L17,9 L15,3";

        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        DateTimeOffset shown;
        Summary summary;
        bool oldestFirst;

        /// The language of OpenBox Achievements (summary.json's "lang"); before
        /// it has written one, the Windows display language. English or Ukrainian.
        bool ukrainian = WindowsUkrainian();
        static bool WindowsUkrainian() =>
            Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault()?.StartsWith("uk", StringComparison.OrdinalIgnoreCase) == true;
        string T(string english, string ukrainianText) => ukrainian ? ukrainianText : english;

        /// The page's own texts in the current language.
        void Localize()
        {
            AllText.Text = T("All achievements", "Усі досягнення");
            string[] filters = { T("All", "Усі"), T("Locked", "Закриті"), T("Unlocked", "Відкриті"), T("Rare", "Рідкісні") };
            if (Filter.Items[0] is ComboBoxItem first && (string)first.Content != filters[0])
            {
                // Reselected, so the closed box shows the new text too.
                int selected = Filter.SelectedIndex;
                for (int i = 0; i < filters.Length && i < Filter.Items.Count; ++i)
                    if (Filter.Items[i] is ComboBoxItem item) item.Content = filters[i];
                Filter.SelectedIndex = -1;
                Filter.SelectedIndex = selected;
            }
            ToolTipService.SetToolTip(SortButton, T("Sort", "Сортування"));
            NoGameTitle.Text = T("No active game", "Немає активної гри");
            NoGameHint.Text = summary == null
                ? T("Start OpenBox Achievements (it starts with the game) to see your achievements here.",
                    "Запустіть OpenBox Achievements (він запускається разом із грою), щоб бачити тут свої досягнення.")
                : T("Start a game to see its achievements here.", "Запустіть гру, щоб бачити тут її досягнення.");
        }

        public WidgetPage()
        {
            InitializeComponent();
            timer.Tick += async (s, e) => await Refresh();
            // The controller: the D-pad and left stick move focus between the
            // controls and down the list (A presses, B is Game Bar's).
            PreviewKeyDown += (s, e) =>
            {
                var direction = Direction(e.Key);
                if (direction.HasValue && Windows.UI.Xaml.Input.FocusManager.TryMoveFocus(direction.Value)) e.Handled = true;
            };
        }

        Microsoft.Gaming.XboxGameBar.XboxGameBarWidget widget;

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            widget = e.Parameter as Microsoft.Gaming.XboxGameBar.XboxGameBarWidget;
            if (widget != null)
            {
                widget.MaxWindowSize = new Windows.Foundation.Size(900, 1600);
                widget.CompactModeEnabledChanged += async (w, o) => await Fit("compact mode changed");
                Window.Current.SizeChanged += (w, o) => Diagnose("size changed");
                await Fit("opened");
            }
            await Refresh();
            timer.Start();
            AllButton.Focus(FocusState.Programmatic); // where a gamepad starts
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e) => timer.Stop();

        async System.Threading.Tasks.Task Refresh()
        {
            var folder = ApplicationData.Current.LocalFolder;
            var file = await folder.TryGetItemAsync("summary.json") as StorageFile;
            if (file == null)
            {
                NoGame.Visibility = Visibility.Visible;
                summary = null;
                ukrainian = WindowsUkrainian();
                Localize();
                GameRow.Visibility = FilterRow.Visibility = List.Visibility = Visibility.Collapsed;
                return;
            }
            var changed = (await file.GetBasicPropertiesAsync()).DateModified;
            if (changed == shown) return;
            try { summary = Summary.Parse(await FileIO.ReadTextAsync(file)); }
            catch (Exception) { return; } // being written: the next tick reads it
            shown = changed;
            ukrainian = summary.Lang.Length > 0 ? summary.Lang == "uk" : WindowsUkrainian();
            Localize();
            bool active = summary.Active;
            NoGame.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            GameRow.Visibility = FilterRow.Visibility = List.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (!active) return;
            ShowGame(folder.Path);
            ShowList();
        }

        void ShowGame(string folder)
        {
            GameName.Text = summary.Game;
            BoxArt.Source = summary.BoxArt.Length > 0
                ? new BitmapImage(new Uri(System.IO.Path.Combine(folder, "art", summary.BoxArt))) { CreateOptions = BitmapCreateOptions.IgnoreImageCache }
                : null;
            GameStats.Children.Clear();
            GameStats.Children.Add(GIcon(22));
            GameStats.Children.Add(new TextBlock { Text = $"{summary.Earned}/{summary.Gamerscore}", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
            GameStats.Children.Add(Glyph(TrophyPath, 20));
            GameStats.Children.Add(new TextBlock { Text = $"{summary.Unlocked}/{summary.Total}", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        }

        void ShowList()
        {
            if (summary == null) return;
            IEnumerable<Summary.Achievement> items = summary.Achievements;
            switch (Filter.SelectedIndex)
            {
                case 1: items = items.Where(a => !a.IsUnlocked); break;
                case 2: items = items.Where(a => a.IsUnlocked); break;
                case 3: items = items.Where(a => a.Rare); break;
            }
            // As the native widget: the latest unlocks first, then the locked
            // ones furthest along. Sort turns it round.
            var ordered = items.OrderByDescending(a => a.IsUnlocked).ThenByDescending(a => a.Unlocked).ThenByDescending(a => a.Progress).ToList();
            if (oldestFirst) ordered.Reverse();
            List.Items.Clear();
            foreach (var a in ordered) List.Items.Add(Row(a));
            if (ordered.Count == 0)
                List.Items.Add(new TextBlock { Text = T("Nothing here yet.", "Тут поки нічого немає."), Foreground = Muted, Margin = new Thickness(0, 12, 0, 0) });
        }

        UIElement Row(Summary.Achievement a)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 12) };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(new TextBlock { Text = a.Name, FontSize = 18, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var points = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0) };
            if (a.Rare) points.Children.Add(Glyph(DiamondPath, 20));
            points.Children.Add(GIcon(22));
            points.Children.Add(new TextBlock { Text = a.Gamerscore.ToString(), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(points, 1);
            top.Children.Add(points);
            panel.Children.Add(top);
            if (a.Description.Length > 0)
                panel.Children.Add(new TextBlock { Text = a.Description, FontSize = 15, Foreground = Muted, TextWrapping = TextWrapping.WrapWholeWords, Margin = new Thickness(0, 4, 0, 0) });
            panel.Children.Add(Line(a.IsUnlocked ? 1 : a.Progress));
            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition());
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string left = a.IsUnlocked ? T("Unlocked ", "Відкрито ") + a.Unlocked.ToLocalTime().ToString("d") : a.Text.Length > 0 ? a.Text : T("Locked", "Закрито");
            bottom.Children.Add(new TextBlock { Text = left, FontSize = 15, Foreground = Muted });
            var percent = new TextBlock { Text = (int)Math.Floor((a.IsUnlocked ? 1 : a.Progress) * 100) + "%", FontSize = 15, Foreground = Muted };
            Grid.SetColumn(percent, 1);
            bottom.Children.Add(percent);
            panel.Children.Add(bottom);
            return panel;
        }

        static UIElement Line(double value)
        {
            value = Math.Max(0, Math.Min(1, value));
            var grid = new Grid { Height = 3, Margin = new Thickness(0, 12, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(value, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - value, GridUnitType.Star) });
            var track = new Rectangle { Fill = Track };
            Grid.SetColumnSpan(track, 2);
            grid.Children.Add(track);
            grid.Children.Add(new Rectangle { Fill = White });
            return grid;
        }

        static FrameworkElement GIcon(double size)
        {
            var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(new Ellipse { Fill = White });
            grid.Children.Add(new TextBlock { Text = "G", FontSize = size * 0.6, FontWeight = FontWeights.Bold, Foreground = Dark, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            return grid;
        }

        static FrameworkElement Glyph(string data, double size) => new Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            Stroke = White, StrokeThickness = 1.6, Stretch = Stretch.Uniform, Width = size, Height = size,
            StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
        };

        static Windows.UI.Xaml.Input.FocusNavigationDirection? Direction(Windows.System.VirtualKey key)
        {
            switch (key)
            {
                case Windows.System.VirtualKey.GamepadDPadUp:
                case Windows.System.VirtualKey.GamepadLeftThumbstickUp: return Windows.UI.Xaml.Input.FocusNavigationDirection.Up;
                case Windows.System.VirtualKey.GamepadDPadDown:
                case Windows.System.VirtualKey.GamepadLeftThumbstickDown: return Windows.UI.Xaml.Input.FocusNavigationDirection.Down;
                case Windows.System.VirtualKey.GamepadDPadLeft:
                case Windows.System.VirtualKey.GamepadLeftThumbstickLeft: return Windows.UI.Xaml.Input.FocusNavigationDirection.Left;
                case Windows.System.VirtualKey.GamepadDPadRight:
                case Windows.System.VirtualKey.GamepadLeftThumbstickRight: return Windows.UI.Xaml.Input.FocusNavigationDirection.Right;
                default: return null;
            }
        }

        /// The size of Game Bar's own panels: in compact mode (the controller
        /// view) as its Home panel, else the standard widget size. Game Bar
        /// keeps a widget's last size, so this is asked for on every start.
        async System.Threading.Tasks.Task Fit(string why)
        {
            // Compact mode applies the manifest size (465x615, its Home panel's);
            // elsewhere the widget may be taller.
            if (widget.CompactModeEnabled) { Diagnose(why + ": compact, manifest size"); return; }
            var size = new Windows.Foundation.Size(465, 860);
            bool resized = false;
            try { resized = await widget.TryResizeWindowAsync(size); } catch (Exception) { }
            Diagnose(why + ": asked " + size.Width + "x" + size.Height + " -> " + resized);
        }

        /// LocalState\size.txt: what Game Bar gave the widget, to tune Fit.
        async void Diagnose(string what)
        {
            try
            {
                var b = Window.Current.Bounds;
                var w = widget?.WindowBounds;
                string line = DateTimeOffset.Now.ToString("HH:mm:ss") + " " + what + "; compact=" + widget?.CompactModeEnabled +
                              " window=" + b.Width + "x" + b.Height + " bounds=" + w?.Width + "x" + w?.Height +
                              " scale=" + Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel + "\r\n";
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("size.txt", CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(file, line);
            }
            catch (Exception) { }
        }

        void OnFilter(object sender, SelectionChangedEventArgs e) => ShowList();

        void OnSort(object sender, RoutedEventArgs e)
        {
            oldestFirst = !oldestFirst;
            ShowList();
        }

        async void OnAll(object sender, RoutedEventArgs e)
        {
            string game = summary?.Id.Length > 0 ? summary.Id : "";
            var uri = new Uri("openbox-achievements:show" + (game.Length > 0 ? "?game=" + game : ""));
            // Through Game Bar when hosted there: it brings the launched app forward.
            if (widget != null) await widget.LaunchUriAsync(uri);
            else await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }
}

