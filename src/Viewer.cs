using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OpenBoxAchievements
{
    /// The achievements page of the Xbox app: ring artwork, title header with
    /// a game switcher, Gamerscore / Achievements / Time played, filter and
    /// sort dropdowns and a responsive grid of achievement cards. Works with
    /// mouse, keyboard and controller (XInput).
    sealed class Viewer : Window
    {
        static readonly Color PageColor = Color.FromRgb(0x17, 0x18, 0x1B);
        static readonly Brush Page = new SolidColorBrush(PageColor), Card = Rgb(0x27, 0x29, 0x2E), CardFocus = Rgb(0x48, 0x4B, 0x52),
            Muted = Rgb(0xB4, 0xB7, 0xBE), Faint = Rgb(0x55, 0x58, 0x5F), Outline = Rgb(0x7D, 0x80, 0x88),
            PopupBack = Rgb(0x1E, 0x1F, 0x23), Selected = Rgb(0x4A, 0x4D, 0x55), White = Brushes.White;
        static readonly FontFamily Font = new FontFamily("Segoe UI Variable Display, Segoe UI");
        static readonly FontFamily Bold = new FontFamily("Segoe UI Variable Display Semibold, Segoe UI Semibold");

        static string[] Filters => new[] { Lang.T("All achievements", "Усі досягнення"), Lang.T("Unlocked achievements", "Відкриті досягнення"), Lang.T("Locked achievements", "Закриті досягнення") };
        static string[] Sorts => new[] { Lang.T("By progress", "За прогресом"), Lang.T("By gamerscore", "За Gamerscore") };

        readonly List<Game> games;
        Game game;
        public Game Game => game;
        int filter, sort;
        readonly ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        readonly StackPanel content = new StackPanel();
        readonly WrapPanel cards = new WrapPanel();
        readonly Grid root = new Grid();
        readonly Grid backdrop = new Grid { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
        readonly List<Button> dropdowns = new List<Button>();
        // The open dropdown list: a layer over the page (a Popup is a separate
        // window, which a full-screen topmost page can cover).
        Grid openList;
        FrameworkElement openAnchor;
        UIElement detail;
        readonly DispatcherTimer pad = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        ushort lastButtons;
        long nextMove;
        int heldDirection = -1;

        readonly Action openSettings;
        readonly Action<Game> loadData;

        public Viewer(List<Game> games, Game selected, Action openSettings, Action<Game> loadData)
        {
            this.games = games;
            this.openSettings = openSettings;
            this.loadData = loadData;
            game = selected ?? games.FirstOrDefault();
            Title = "OpenBox Achievements";
            // Full screen and borderless, like the console's guide pages.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Width = 1440; Height = 900;
            Background = Page; FontFamily = Font; Foreground = White;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            // The rings belong to the header and scroll away with it, as in the Xbox app.
            var page = new Grid();
            page.Children.Add(backdrop);
            page.Children.Add(content);
            scroll.Content = page;
            scroll.Resources.Add(typeof(ScrollBar), DarkScrollBar());
            root.Children.Add(scroll);
            var close = Clickable(Glyph("M4,4 L20,20 M20,4 L4,20", 22, White, 2), 22, new Thickness(14));
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Top;
            close.Margin = new Thickness(0, 22, 26, 0);
            close.Background = new SolidColorBrush(Color.FromArgb(0x60, 0x20, 0x21, 0x25));
            close.Focusable = false;
            close.ToolTip = Lang.T("Close (B / Esc)", "Закрити (B / Esc)");
            close.Click += (s, e) => Close();
            root.Children.Add(close);
            var gear = Clickable(Glyph("M12,8.5 A3.5,3.5 0 1 1 11.99,8.5 Z M10.6,2 H13.4 L13.9,4.7 L16.2,5.7 L18.5,4.1 L20.4,6 L18.8,8.3 L19.8,10.6 L22.5,11.1 V13.9 L19.8,14.4 L18.8,16.7 L20.4,19 L18.5,20.9 L16.2,19.3 L13.9,20.3 L13.4,23 H10.6 L10.1,20.3 L7.8,19.3 L5.5,20.9 L3.6,19 L5.2,16.7 L4.2,14.4 L1.5,13.9 V11.1 L4.2,10.6 L5.2,8.3 L3.6,6 L5.5,4.1 L7.8,5.7 L10.1,4.7 Z", 22, White, 1.6), 22, new Thickness(14));
            gear.HorizontalAlignment = HorizontalAlignment.Right;
            gear.VerticalAlignment = VerticalAlignment.Top;
            gear.Margin = new Thickness(0, 22, 98, 0);
            gear.Background = new SolidColorBrush(Color.FromArgb(0x60, 0x20, 0x21, 0x25));
            gear.ToolTip = Lang.T("Settings (Menu button)", "Параметри (кнопка Menu)");
            gear.Click += (s, e) => openSettings();
            root.Children.Add(gear);
            Content = root;
            // Sizes are laid out for a 2600 px wide page; the page scales with the window.
            content.LayoutTransform = scale;
            SizeChanged += (s, e) => { Rescale(); LayoutCards(); };
            Loaded += (s, e) =>
            {
                Rescale();
                LayoutCards();
                // After the first layout: the first card focused, the page at the top.
                Dispatcher.BeginInvoke(new Action(() => { FocusFirst(); scroll.ScrollToTop(); }), DispatcherPriority.ContextIdle);
            };
            PreviewKeyDown += OnKey;
            AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Scrolling), true);
            pad.Tick += (s, e) => Pad();
            // Buttons already held when the page comes up (the A that opened it
            // from the game) count only after they are released.
            Activated += (s, e) => { lastButtons = HeldButtons(); pad.Start(); if (stale) Refresh(); };
            Deactivated += (s, e) => pad.Stop();
            Closed += (s, e) => pad.Stop();
            Build();
        }

        static SolidColorBrush Rgb(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

        readonly ScaleTransform scale = new ScaleTransform(1, 1);

        /// Units are the Xbox app's at 2.25x (its card title is 25 units, a
        /// card 620 wide): a fixed size, so wider screens get more columns.
        const double UnitScale = 1 / 2.25;
        const double CardWidth = 620, Gap = 48, SideMargin = 96;
        const int MaxColumns = 3; // as the Xbox app: at most three, centred

        void Rescale() => scale.ScaleX = scale.ScaleY = UnitScale;

        public void Select(Game selected)
        {
            if (selected != null && selected != game) { game = selected; filter = 0; Build(); }
        }

        bool stale;

        /// Progress changed: redrawn now if the page is in front, else when it next is.
        public void Stale()
        {
            stale = true;
            if (IsActive) Refresh();
        }

        public void Refresh()
        {
            stale = false;
            var focused = Keyboard.FocusedElement as FrameworkElement;
            string tag = focused?.Tag as string;
            Build();
            if (tag != null)
                cards.Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == tag)?.Focus();
        }

        // ---------------------------------------------------------------- art

        /// The Xbox achievements backdrop: concentric green and gold rings
        /// around a grey centre, fading into the page.
        static UIElement Rings()
        {
            var canvas = new Grid { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, Height = 820, ClipToBounds = true };
            var rings = new Grid { Width = 2600, Height = 2600, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1850, 0, 0) };
            var colors = new[] { (0x22, 0x2E, 0x12), (0x5B, 0x5A, 0x12), (0x2C, 0x55, 0x16), (0x1D, 0x40, 0x14), (0x42, 0x4F, 0x12), (0x1A, 0x34, 0x16), (0x24, 0x47, 0x15) };
            double size = 2600;
            foreach (var (r, g, b) in colors)
            {
                rings.Children.Add(new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b)) });
                size -= 150;
            }
            rings.Children.Add(new Ellipse { Width = size, Height = size, Fill = new RadialGradientBrush(Color.FromRgb(0x5E, 0x5E, 0x62), Color.FromRgb(0x3A, 0x3A, 0x3E)) });
            canvas.Children.Add(rings);
            canvas.Children.Add(new Rectangle
            {
                Fill = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x20, PageColor.R, PageColor.G, PageColor.B), 0),
                    new GradientStop(Color.FromArgb(0x90, PageColor.R, PageColor.G, PageColor.B), 0.55),
                    new GradientStop(PageColor, 1),
                }, 90),
            });
            return canvas;
        }

        /// A game's own background (artackground.jpg): full width behind the
        /// header, darkened and fading into the page.
        static UIElement Backdrop(string file)
        {
            var grid = new Grid { Height = 1100, ClipToBounds = true };
            grid.Children.Add(new Border { Background = new ImageBrush(Load(file, 2560, false)) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top } });
            grid.Children.Add(new Rectangle
            {
                Fill = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x70, PageColor.R, PageColor.G, PageColor.B), 0),
                    new GradientStop(Color.FromArgb(0xB0, PageColor.R, PageColor.G, PageColor.B), 0.5),
                    new GradientStop(PageColor, 1),
                }, 90),
            });
            return grid;
        }

        static FrameworkElement GIcon(double size, Brush fill)
        {
            var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(new Ellipse { Fill = fill });
            grid.Children.Add(new TextBlock { Text = "G", FontFamily = Bold, FontSize = size * 0.62, Foreground = Rgb(0x2A, 0x2B, 0x2F), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, size * 0.06) });
            return grid;
        }

        static FrameworkElement Glyph(string data, double size, Brush stroke, double thickness = 1.6) => new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data), Stroke = stroke, StrokeThickness = thickness, Stretch = Stretch.Uniform,
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, StrokeLineJoin = PenLineJoin.Round,
        };

        const string TrophyPath = "M7,3 H17 V9 A5,5 0 0 1 7,9 Z M7,5 H4 V7 A3,3 0 0 0 7,10 M17,5 H20 V7 A3,3 0 0 1 17,10 M12,14 V18 M8,21 H16 M9,18 H15 V21";
        const string ClockPath = "M12,2.5 A9.5,9.5 0 1 1 11.99,2.5 Z M12,6.5 V12 L15.5,14";
        const string DiamondPath = "M6,3 H18 L22,8 L12,21 L2,8 Z M2,8 H22 M8.5,3 L12,8 L15.5,3 M7,8 L12,21 M17,8 L12,21";
        const string ChevronPath = "M2,5 L10,13 L18,5";
        const string LockPath = "M12,1 A5,5 0 0 0 7,6 V9 H5 V22 H19 V9 H17 V6 A5,5 0 0 0 12,1 Z M9,6 A3,3 0 0 1 15,6 V9 H9 Z";

        /// A thin rounded thumb on a transparent track, as in the Xbox app.
        static Style DarkScrollBar() => (Style)System.Windows.Markup.XamlReader.Parse(
            "<Style TargetType='ScrollBar' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
            "<Setter Property='Width' Value='6'/><Setter Property='Margin' Value='0,90,6,6'/>" +
            "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'><Grid Background='Transparent'>" +
            "<Track x:Name='PART_Track' IsDirectionReversed='True'><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
            "<Border CornerRadius='3' Background='#80B4B7BE'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track>" +
            "</Grid></ControlTemplate></Setter.Value></Setter></Style>");

        // -------------------------------------------------------------- build

        void Build()
        {
            content.Children.Clear();
            dropdowns.Clear();
            content.Margin = new Thickness(0, 120, 0, 64);
            content.HorizontalAlignment = HorizontalAlignment.Center;
            LayoutCards(); // the page keeps the grid's width whatever it shows
            if (game == null)
            {
                content.Children.Add(new TextBlock { Text = Lang.T("No games yet.", "Ігор ще немає."), FontSize = 30, Margin = new Thickness(0, 0, 0, 24) });
                var add = Clickable(new TextBlock { Text = Lang.T("Settings – Add game…", "Параметри – Додати гру…"), FontSize = 25 }, 10, new Thickness(24, 12, 24, 12), Outline);
                add.HorizontalAlignment = HorizontalAlignment.Left;
                add.Click += (s, e) => openSettings();
                content.Children.Add(add);
                return;
            }
            Title = game.Name + " – OpenBox Achievements";
            backdrop.Children.Clear();
            backdrop.Children.Add(game.Background != null ? Backdrop(game.Background) : Rings());
            content.Children.Add(Header());
            content.Children.Add(Stats());
            if (game.NeedsData)
            {
                content.Children.Add(MissingData());
                return;
            }
            content.Children.Add(Dropdowns());
            cards.Margin = new Thickness(0, 44, -Gap, 0);
            content.Children.Add(cards);
            BuildCards();
        }

        UIElement Header()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 64) };
            var box = new Border { Width = 262, Height = 262, CornerRadius = new CornerRadius(10), Background = Faint };
            if (game.BoxArt != null) box.Background = new ImageBrush(Load(game.BoxArt, 400, false)) { Stretch = Stretch.UniformToFill };
            row.Children.Add(box);
            var chevron = Glyph(ChevronPath, 22, White, 2);
            chevron.Margin = new Thickness(28, 6, 0, 0);
            var switcher = Clickable(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new TextBlock { Text = game.Name, FontSize = 30, VerticalAlignment = VerticalAlignment.Center }, chevron },
            }, 12, new Thickness(14, 8, 14, 8));
            switcher.Margin = new Thickness(26, 0, 0, 0);
            switcher.VerticalAlignment = VerticalAlignment.Center;
            switcher.Click += (s, e) => ShowList(switcher, games.Select(g => g.Name).ToArray(), games.IndexOf(game), i => Select(games[i]));
            row.Children.Add(switcher);
            return row;
        }

        UIElement Stats()
        {
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 48) };
            grid.Children.Add(Stat("Gamerscore", GIcon(32, White), game.Earned + "/" + game.Total));
            grid.Children.Add(Stat(Lang.T("Achievements", "Досягнення"), Glyph(TrophyPath, 32, White), game.UnlockedCount + "/" + game.Catalog.Achievements.Count));
            var played = TimeSpan.FromSeconds(game.State.SecondsPlayed);
            grid.Children.Add(Stat(Lang.T("Time played", "Час у грі"), Glyph(ClockPath, 32, White),
                (int)played.TotalHours + Lang.T("h ", " год ") + played.Minutes + Lang.T("m", " хв")));
            return grid;
        }

        static UIElement Stat(string label, FrameworkElement icon, string value)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = label, FontSize = 25, Foreground = Muted });
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            line.Children.Add(icon);
            line.Children.Add(new TextBlock { Text = value, FontSize = 25, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(line);
            return panel;
        }

        /// A game with only its achievement IDs (as shared): load the texts and art.
        UIElement MissingData()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = game.Info.Achievements.Length > 0
                    ? Lang.T("This game has its " + game.Info.Achievements.Length + " achievement IDs, but no texts or art yet.",
                        "Ця гра має ідентифікатори досягнень (" + game.Info.Achievements.Length + "), але ще не має текстів і зображень.")
                    : Lang.T("This game's achievements are not loaded yet.", "Досягнення цієї гри ще не завантажено."),
                FontSize = 27, TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = game.Info.XboxTitleId != 0
                    ? Lang.T("Load them from Xbox with your own account; they stay on this PC.", "Завантажте їх з Xbox через власний обліковий запис; вони залишаться на цьому ПК.")
                    : Lang.T("Add them in Settings → Add game (Manual).", "Додайте їх у Параметри → Додати гру (Вручну)."),
                FontSize = 23, Foreground = Muted, Margin = new Thickness(0, 12, 0, 28), TextWrapping = TextWrapping.Wrap,
            });
            if (game.Info.XboxTitleId != 0)
            {
                var load = Clickable(new TextBlock { Text = Lang.T("Load from Xbox…", "Завантажити з Xbox…"), FontSize = 25 }, 10, new Thickness(24, 12, 24, 12), Outline);
                load.HorizontalAlignment = HorizontalAlignment.Left;
                load.Click += (s, e) => loadData(game);
                panel.Children.Add(load);
            }
            return panel;
        }

        UIElement Dropdowns()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Dropdown(Filters, () => filter, i => { filter = i; Build(); FocusFirst(); }));
            var second = Dropdown(Sorts, () => sort, i => { sort = i; Build(); FocusFirst(); });
            second.Margin = new Thickness(16, 0, 0, 0);
            row.Children.Add(second);
            return row;
        }

        Button Dropdown(string[] items, Func<int> current, Action<int> choose)
        {
            var grid = new Grid { Width = 400 };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = items[current()], FontSize = 24, VerticalAlignment = VerticalAlignment.Center });
            var chevron = Glyph(ChevronPath, 20, White, 2);
            Grid.SetColumn(chevron, 1);
            grid.Children.Add(chevron);
            var button = Clickable(grid, 10, new Thickness(18, 10, 20, 10), Outline);
            button.Background = new SolidColorBrush(Color.FromArgb(0x50, 0x2A, 0x2C, 0x31));
            button.Click += (s, e) => ShowList(button, items, current(), choose);
            dropdowns.Add(button);
            return button;
        }

        /// The Xbox dropdown list: a dark rounded panel under the button with
        /// centred items, the current one on a lighter rounded highlight.
        void ShowList(FrameworkElement anchor, string[] items, int current, Action<int> choose)
        {
            var list = new StackPanel { Margin = new Thickness(28) };
            Button focus = null;
            for (int i = 0; i < items.Length; ++i)
            {
                int index = i;
                var item = Clickable(new TextBlock { Text = items[i], FontSize = 25, FontFamily = Bold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = i == current ? White : Muted }, 12, new Thickness(28, 22, 28, 22));
                item.Width = Math.Max(300, anchor.ActualWidth - 56); // the list as wide as its button
                item.HorizontalContentAlignment = HorizontalAlignment.Center;
                item.Margin = new Thickness(0, 0, 0, 6);
                if (i == current) item.Background = Selected;
                item.Click += (s, e) => { CloseList(); choose(index); };
                if (i == current || focus == null) focus = item;
                list.Children.Add(item);
            }
            var panel = new Border { Background = PopupBack, CornerRadius = new CornerRadius(10), BorderBrush = Rgb(0x3A, 0x3C, 0x42), BorderThickness = new Thickness(1), Child = list, LayoutTransform = scale };
            CloseList();
            // Under the button, as in the Xbox app; a click outside closes it.
            var layer = new Grid { Background = Brushes.Transparent };
            layer.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == layer) CloseList(); };
            var at = anchor.TranslatePoint(new Point(0, anchor.ActualHeight), root);
            panel.HorizontalAlignment = HorizontalAlignment.Left;
            panel.VerticalAlignment = VerticalAlignment.Top;
            panel.Margin = new Thickness(Math.Max(0, at.X), at.Y + 8 * scale.ScaleY, 0, 0);
            panel.Opacity = 0;
            panel.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            layer.Children.Add(panel);
            KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
            root.Children.Add(layer);
            openList = layer;
            openAnchor = anchor;
            // Keeps a list near the bottom of the screen on screen.
            layer.Loaded += (s, e) =>
            {
                double over = panel.Margin.Top + panel.ActualHeight * scale.ScaleY - root.ActualHeight + 16;
                if (over > 0) panel.Margin = new Thickness(panel.Margin.Left, Math.Max(0, at.Y - anchor.ActualHeight * scale.ScaleY - panel.ActualHeight * scale.ScaleY - 12 * scale.ScaleY), 0, 0);
                focus?.Focus();
            };
        }

        /// Closes the open dropdown list, focus back on its button.
        bool CloseList()
        {
            if (openList == null) return false;
            root.Children.Remove(openList);
            openList = null;
            if (openAnchor != null && openAnchor.IsVisible) openAnchor.Focus();
            openAnchor = null;
            return true;
        }

        /// A focusable button without chrome: a rounded background that lights
        /// up on hover, and the Xbox white focus ring for keyboard and pad.
        static Button Clickable(UIElement child, double radius, Thickness padding, Brush border = null)
        {
            var button = new Button { Content = child, Padding = padding, Background = Brushes.Transparent, BorderBrush = border ?? Brushes.Transparent, Foreground = White, Cursor = Cursors.Hand, FocusVisualStyle = null, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var chrome = new FrameworkElementFactory(typeof(Border), "chrome");
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(border == null ? 0 : 1.5));
            chrome.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            chrome.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            chrome.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new System.Windows.Data.Binding("HorizontalContentAlignment") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            chrome.AppendChild(presenter);
            var ring = new FrameworkElementFactory(typeof(Border), "ring");
            ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius + 5));
            ring.SetValue(Border.BorderThicknessProperty, new Thickness(3));
            ring.SetValue(Border.BorderBrushProperty, White);
            ring.SetValue(MarginProperty, new Thickness(-7));
            ring.SetValue(VisibilityProperty, Visibility.Hidden);
            ring.SetValue(IsHitTestVisibleProperty, false);
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.AppendChild(chrome);
            grid.AppendChild(ring);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = grid };
            var focused = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "ring"));
            template.Triggers.Add(focused);
            button.Template = template;
            return button;
        }

        /// Cards scroll into view with room around them; the header (title,
        /// dropdowns) always shows the page from its top, as the Xbox app does.
        void Scrolling(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!(e.NewFocus is Button button) || !content.IsAncestorOf(button)) return;
            if (button.Tag is string)
                button.BringIntoView(new Rect(-40, -60, Math.Max(0, button.ActualWidth) + 80, Math.Max(0, button.ActualHeight) + 120));
            else scroll.ScrollToTop();
        }

        // -------------------------------------------------------------- cards

        IEnumerable<Achievement> Visible()
        {
            var all = game.Catalog.Achievements.AsEnumerable();
            switch (filter)
            {
                case 1: all = all.Where(game.IsUnlocked); break;
                case 2: all = all.Where(a => !game.IsUnlocked(a)); break;
            }
            switch (sort)
            {
                case 1: return all.OrderByDescending(a => a.Gamerscore).ThenBy(a => a.Name);
            }
            // By progress: newest unlocks first, then those closest to unlocking.
            return all.OrderBy(a => game.IsUnlocked(a) ? 0 : 1)
                .ThenByDescending(a => game.IsUnlocked(a) ? game.State.Unlocked[a.Id].Time : "", StringComparer.Ordinal)
                .ThenByDescending(a => game.Results.Progress(a, out _));
        }

        static bool Rare(Achievement a) => a.RarityPercent > 0 && a.RarityPercent < 10;

        int columns;
        double cardWidth = CardWidth;

        /// Up to three fixed-size columns, the whole column centred.
        void LayoutCards()
        {
            // The window's width, not the scroll area's: the scrollbar coming and
            // going (a short page) must not change the layout.
            double width = (ActualWidth > 0 ? ActualWidth : Width) / scale.ScaleX;
            int wanted = Math.Min(MaxColumns, Math.Max(1, (int)((width - 2 * SideMargin + Gap) / (CardWidth + Gap))));
            cardWidth = wanted == 1 ? Math.Min(CardWidth, width - 2 * SideMargin) : CardWidth;
            columns = wanted;
            content.Width = wanted * cardWidth + (wanted - 1) * Gap;
            foreach (FrameworkElement card in cards.Children) card.Width = cardWidth;
        }

        void BuildCards()
        {
            cards.Children.Clear();
            LayoutCards();
            foreach (var achievement in Visible()) cards.Children.Add(CardFor(achievement));
            if (cards.Children.Count == 0)
                cards.Children.Add(new TextBlock
                {
                    Text = game.Catalog.Achievements.Count == 0 ? Lang.T("This game has no achievements.", "Ця гра не має досягнень.")
                        : Lang.T("No achievements to show here.", "Тут немає досягнень для показу."),
                    FontSize = 27, Foreground = Muted, Margin = new Thickness(0, 24, 0, 0),
                });
        }

        FrameworkElement CardFor(Achievement a)
        {
            bool unlocked = game.IsUnlocked(a), hidden = !unlocked && a.IsSecret;
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition());

            var art = new Border { CornerRadius = new CornerRadius(10, 10, 0, 0), Background = Faint };
            art.SetBinding(HeightProperty, new System.Windows.Data.Binding("ActualWidth") { Source = art, Converter = new SixteenByNine() });
            string file = hidden ? null : game.Art(a);
            if (file != null) art.Background = new ImageBrush(Load(file, 640, !unlocked)) { Stretch = Stretch.UniformToFill, Opacity = unlocked ? 1 : 0.55 };
            if (!unlocked)
                art.Child = new System.Windows.Shapes.Path
                {
                    Fill = White, Opacity = 0.9, Width = 26, Height = 30, Stretch = Stretch.Uniform, Margin = new Thickness(18),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Data = Geometry.Parse(LockPath),
                };
            body.Children.Add(art);

            var info = new StackPanel { Margin = new Thickness(34, 34, 34, 30) };
            Grid.SetRow(info, 1);
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(new TextBlock { Text = hidden ? SecretName : a.Name, FontFamily = Bold, FontSize = 25, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var score = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
            if (Rare(a)) score.Children.Add(Glyph(DiamondPath, 28, Muted, 1.4));
            var g = GIcon(28, Muted);
            g.Margin = new Thickness(12, 0, 8, 0);
            score.Children.Add(g);
            score.Children.Add(new TextBlock { Text = a.Gamerscore.ToString(CultureInfo.CurrentCulture), FontSize = 27, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(score, 1);
            top.Children.Add(score);
            info.Children.Add(top);

            string description = hidden ? SecretDescription
                : !unlocked && !string.IsNullOrEmpty(a.LockedDescription) ? a.LockedDescription : a.Description;
            info.Children.Add(new TextBlock { Text = description, FontSize = 23, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 22, 0, 0) });
            if (a.RarityPercent > 0)
                info.Children.Add(new TextBlock
                {
                    Text = Lang.T((Rare(a) ? "Only " : "") + a.RarityPercent.ToString("0.##", CultureInfo.CurrentCulture) + "% of gamers unlocked this",
                        (Rare(a) ? "Лише " : "") + a.RarityPercent.ToString("0.##", CultureInfo.CurrentCulture) + "% гравців відкрили це досягнення"),
                    FontSize = 23, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                });

            string progressText = null;
            double progress = unlocked ? 1 : game.Results.Progress(a, out progressText);
            var bar = new Grid { Height = 3, Margin = new Thickness(0, 40, 0, 0) };
            bar.Children.Add(new Rectangle { Fill = Faint, RadiusX = 1.5, RadiusY = 1.5 });
            var fill = new Rectangle { Fill = White, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left };
            fill.SetBinding(WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = bar, Converter = new Fraction(Math.Max(0, progress)) });
            bar.Children.Add(fill);
            info.Children.Add(bar);
            string footer = unlocked ? UnlockedOn(game.State.Unlocked[a.Id].Time) : progress >= 0 ? progressText : Locked;
            info.Children.Add(new TextBlock { Text = footer, FontSize = 23, Foreground = Muted, Margin = new Thickness(0, 30, 0, 0) });
            body.Children.Add(info);

            var card = Clickable(body, 10, new Thickness(0));
            card.Tag = a.Id;
            card.Background = Card;
            card.Width = cardWidth;
            card.Margin = new Thickness(0, 0, Gap, Gap);
            card.VerticalAlignment = VerticalAlignment.Top;
            card.GotKeyboardFocus += (s, e) => card.Background = CardFocus;
            card.LostKeyboardFocus += (s, e) => card.Background = Card;
            card.MouseEnter += (s, e) => card.Background = CardFocus;
            card.MouseLeave += (s, e) => { if (!card.IsKeyboardFocused) card.Background = Card; };
            card.Click += (s, e) => ShowDetail(a);
            return card;
        }

        static string When(string time) =>
            DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : "";

        static string UnlockedOn(string time) => Lang.T("Unlocked ", "Відкрито ") + When(time);
        static string Locked => Lang.T("Locked", "Закрито");
        static string SecretName => Lang.T("Secret achievement", "Секретне досягнення");
        static string SecretDescription => Lang.T("Keep playing to unlock this secret achievement.", "Грайте далі, щоб відкрити це секретне досягнення.");

        /// The achievement over the page: large art, text and status. B closes.
        void ShowDetail(Achievement a)
        {
            CloseDetail();
            bool unlocked = game.IsUnlocked(a), hidden = !unlocked && a.IsSecret;
            var shade = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x0C, 0x0D, 0x0F)) };
            var panel = new Border { Background = Card, CornerRadius = new CornerRadius(12), MaxWidth = 980, Margin = new Thickness(40), VerticalAlignment = VerticalAlignment.Center };
            var stack = new StackPanel();
            string file = hidden ? null : game.Art(a);
            if (file != null)
                stack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12, 12, 0, 0), Height = 520,
                    Background = new ImageBrush(Load(file, 1280, !unlocked)) { Stretch = Stretch.UniformToFill },
                });
            var text = new StackPanel { Margin = new Thickness(40, 32, 40, 36) };
            text.Children.Add(new TextBlock { Text = hidden ? SecretName : a.Name, FontFamily = Bold, FontSize = 34, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = hidden ? SecretDescription : a.Description, FontSize = 24, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 24, 0, 0) };
            line.Children.Add(GIcon(30, White));
            string status = unlocked ? UnlockedOn(game.State.Unlocked[a.Id].Time) : game.Results.Progress(a, out string p) >= 0 ? p : Locked;
            line.Children.Add(new TextBlock { Text = a.Gamerscore + "   ·   " + status, FontSize = 24, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            text.Children.Add(line);
            stack.Children.Add(text);
            panel.Child = stack;
            panel.LayoutTransform = scale;
            shade.Children.Add(panel);
            shade.MouseLeftButtonUp += (s, e) => CloseDetail();
            detail = shade;
            root.Children.Add(shade);
        }

        bool CloseDetail()
        {
            if (detail == null) return false;
            root.Children.Remove(detail);
            detail = null;
            return true;
        }

        static readonly Dictionary<string, BitmapSource> images = new Dictionary<string, BitmapSource>();

        static BitmapSource Load(string file, int width, bool gray)
        {
            string key = file + "|" + width + "|" + gray;
            if (images.TryGetValue(key, out var cached)) return cached;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = width;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            bitmap.Freeze();
            BitmapSource result = bitmap;
            if (gray) { var g = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0); g.Freeze(); result = g; }
            images[key] = result;
            return result;
        }

        sealed class SixteenByNine : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type t, object p, CultureInfo c) => (double)value * 9 / 16;
            public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
        }

        sealed class Fraction : System.Windows.Data.IValueConverter
        {
            readonly double part;
            public Fraction(double part) { this.part = Math.Min(1, part); }
            public object Convert(object value, Type t, object p, CultureInfo c) => (double)value * part;
            public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
        }

        // ---------------------------------------------------------- navigation

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Back(); }
        }

        void Back()
        {
            if (CloseDetail()) return;
            if (CloseList()) return;
            Close();
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct GamepadState { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

        [System.Runtime.InteropServices.DllImport("xinput1_4.dll")]
        static extern int XInputGetState(int user, out GamepadState state);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        static ushort HeldButtons()
        {
            ushort buttons = 0;
            for (int user = 0; user < 4; ++user)
                if (XInputGetState(user, out var state) == 0) buttons |= state.Buttons;
            return buttons;
        }

        /// Controller: D-pad/left stick move focus, A selects, B goes back,
        /// LB/RB switch games, X filter, Y sort, Menu settings, right stick scrolls.
        void Pad()
        {
            ushort buttons = 0;
            short lx = 0, ly = 0, ry = 0;
            for (int user = 0; user < 4; ++user)
            {
                if (XInputGetState(user, out var state) != 0) continue;
                buttons |= state.Buttons;
                if (Math.Abs((int)state.LX) > Math.Abs((int)lx)) lx = state.LX;
                if (Math.Abs((int)state.LY) > Math.Abs((int)ly)) ly = state.LY;
                if (Math.Abs((int)state.RY) > Math.Abs((int)ry)) ry = state.RY;
            }
            // Only the page in front reads the pad; presses meant for the game are ignored.
            if (GetForegroundWindow() != new System.Windows.Interop.WindowInteropHelper(this).Handle)
            {
                lastButtons = buttons;
                return;
            }
            ushort pressed = (ushort)(buttons & ~lastButtons);
            lastButtons = buttons;
            if ((pressed & 0x2000) != 0) { Back(); return; }                                  // B
            if ((pressed & 0x0010) != 0 && detail == null) { openSettings(); return; }        // Menu (Start)
            if ((pressed & 0x1000) != 0)                                                      // A
            {
                if (CloseDetail()) return;
                if (Keyboard.FocusedElement is Button b) b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                else FocusFirst();
                return;
            }
            if (detail != null) return;
            if ((pressed & 0x0300) != 0 && games.Count > 1)                                    // LB / RB
            {
                int step = (pressed & 0x0200) != 0 ? 1 : -1;
                Select(games[(games.IndexOf(game) + step + games.Count) % games.Count]);
                return;
            }
            if ((pressed & 0x4000) != 0 && dropdowns.Count > 0) { dropdowns[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return; } // X
            if ((pressed & 0x8000) != 0 && dropdowns.Count > 1) { dropdowns[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return; } // Y
            if (Math.Abs((int)ry) > 9000) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - ry / 1800.0);

            int direction = (buttons & 0x0001) != 0 || ly > 16000 ? 0 : (buttons & 0x0002) != 0 || ly < -16000 ? 1
                : (buttons & 0x0004) != 0 || lx < -16000 ? 2 : (buttons & 0x0008) != 0 || lx > 16000 ? 3 : -1;
            long now = Environment.TickCount;
            if (direction < 0) { heldDirection = -1; return; }
            if (direction == heldDirection && now < nextMove) return;
            nextMove = now + (direction == heldDirection ? 90 : 300);
            heldDirection = direction;
            if (!(Keyboard.FocusedElement is UIElement focused) || focused == this) { FocusFirst(); return; }
            var way = new[] { FocusNavigationDirection.Up, FocusNavigationDirection.Down, FocusNavigationDirection.Left, FocusNavigationDirection.Right }[direction];
            if (!focused.MoveFocus(new TraversalRequest(way)) && way == FocusNavigationDirection.Up) scroll.ScrollToTop();
        }

        void FocusFirst()
        {
            var first = cards.Children.OfType<Button>().FirstOrDefault();
            if (first != null) first.Focus();
        }
    }
}
