using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenBoxAchievements
{
    /// Adds a game: its achievements from Xbox Live (signed in, picked from
    /// the account's games, the store, or by title ID) or entered by hand.
    /// Writes games\<id>\achievement_manifest.json beside the app; texts and
    /// art loaded from Xbox go to app data (a manual game's own stay beside
    /// the manifest).
    sealed class Wizard : Window
    {
        static readonly Brush Back = new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1B)), Panel = new SolidColorBrush(Color.FromRgb(0x27, 0x29, 0x2E)),
            Field = new SolidColorBrush(Color.FromRgb(0x1F, 0x21, 0x25)), Line = new SolidColorBrush(Color.FromRgb(0x55, 0x58, 0x5F)),
            Muted = new SolidColorBrush(Color.FromRgb(0xB4, 0xB7, 0xBE)), Green = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));

        readonly Settings settings;
        readonly Action<string> finished;
        readonly ContentControl page = new ContentControl();
        readonly TextBlock heading = new TextBlock { FontSize = 26, FontFamily = new FontFamily("Segoe UI Semibold"), Margin = new Thickness(0, 0, 0, 18) };
        Xbox xbox;

        // What is being made.
        string name = "", source = "xbox";
        uint titleId;
        Catalog catalog = new Catalog();
        readonly List<(Achievement Achievement, string Image)> manual = new List<(Achievement, string)>();
        string folder;

        /// restore: a game with a manifest but no texts yet, loaded from Xbox.
        readonly Game restore;

        public Wizard(Settings settings, Action<string> finished, Game restore = null)
        {
            this.restore = restore;
            this.settings = settings; this.finished = finished;
            Title = Lang.T("Add a game", "Додавання гри") + " – OpenBox Achievements";
            Width = 760; Height = 720; MinWidth = 600; MinHeight = 560;
            Background = Back; Foreground = Brushes.White; FontFamily = new FontFamily("Segoe UI"); FontSize = 15;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Dark.Apply(this);
            Pad.Attach(this);
            var shell = new DockPanel { Margin = new Thickness(36, 30, 36, 30) };
            DockPanel.SetDock(heading, Dock.Top);
            shell.Children.Add(heading);
            shell.Children.Add(page);
            Content = shell;
            if (restore != null)
            {
                name = restore.Name;
                source = "xbox";
                titleId = restore.Info.XboxTitleId;
                folder = restore.Folder;
                SignInPage();
            }
            else GamePage();
        }

        // ---------------------------------------------------------------- UI

        static Button Action(string text, RoutedEventHandler click, bool primary = false)
        {
            var button = new Button
            {
                Content = text, Padding = new Thickness(22, 9, 22, 9), Margin = new Thickness(10, 0, 0, 0), FontSize = 15,
                Foreground = Brushes.White, Background = primary ? Green : Panel, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            };
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            chrome.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            chrome.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            chrome.AppendChild(presenter);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
            button.Click += click;
            return button;
        }

        static TextBox Input(string text = "") => new TextBox
        {
            Text = text, Background = Field, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = Line,
            BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 15, Margin = new Thickness(0, 6, 0, 14),
        };

        static TextBlock Label(string text) => new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap };

        /// A page: content, then the buttons at the bottom right.
        void Show(string title, UIElement body, params Button[] buttons)
        {
            heading.Text = title;
            var dock = new DockPanel();
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            foreach (var b in buttons) row.Children.Add(b);
            DockPanel.SetDock(row, Dock.Bottom);
            dock.Children.Add(row);
            dock.Children.Add(body);
            page.Content = dock;
        }

        static string Slug(string text)
        {
            var slug = new StringBuilder();
            foreach (char c in text.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) slug.Append(c);
                else if (slug.Length > 0 && slug[slug.Length - 1] != '-') slug.Append('-');
            return slug.ToString().Trim('-') is string s && s.Length > 0 ? s : "game";
        }

        // ------------------------------------------------------------- pages

        void GamePage()
        {
            var body = new StackPanel();
            body.Children.Add(Label(Lang.T("Game name", "Назва гри")));
            var nameBox = Input(name);
            body.Children.Add(nameBox);
            body.Children.Add(Label(Lang.T("Where do the achievements come from?", "Звідки взяти досягнення?")));
            var xboxChoice = new RadioButton { Content = Lang.T("Xbox game – load the list, texts and art from Xbox Live", "Гра Xbox – завантажити список, тексти й зображення з Xbox Live"), Foreground = Brushes.White, IsChecked = source == "xbox", Margin = new Thickness(0, 10, 0, 6) };
            var manualChoice = new RadioButton { Content = Lang.T("Manual – enter the achievements yourself", "Вручну – ввести досягнення самостійно"), Foreground = Brushes.White, IsChecked = source == "manual", Margin = new Thickness(0, 0, 0, 6) };
            var allChoice = new RadioButton { Content = Lang.T("All my Xbox games – every game with achievements on my account (name not needed)", "Усі мої ігри Xbox – кожна гра з досягненнями в моєму обліковому записі (назва не потрібна)"), Foreground = Brushes.White, IsChecked = source == "all" };
            body.Children.Add(xboxChoice);
            body.Children.Add(manualChoice);
            body.Children.Add(allChoice);
            var error = Label("");
            error.Foreground = Brushes.OrangeRed;
            error.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(error);
            Show(Lang.T("Add a game", "Додавання гри"), body, Action(Lang.T("Cancel", "Скасувати"), (s, e) => Close()), Action(NextText, (s, e) =>
            {
                name = nameBox.Text.Trim();
                source = allChoice.IsChecked == true ? "all" : xboxChoice.IsChecked == true ? "xbox" : "manual";
                if (name.Length == 0 && source == "manual") { error.Text = Lang.T("Enter the game's name.", "Введіть назву гри."); return; }
                if (source == "manual") ManualPage(); else SignInPage();
            }, true));
            nameBox.Focus();
        }

        void SignInPage()
        {
            if (xbox == null)
            {
                string id = settings.SignInClientId;
                if (id == null) { AppIdPage(); return; }
                xbox = new Xbox(Paths.Data, id);
            }
            if (xbox.HasAccount) { Next(); return; }
            var body = new StackPanel();
            var info = Label(Lang.T("Getting a sign-in code…", "Отримання коду входу…"));
            var code = new TextBlock { FontSize = 42, FontFamily = new FontFamily("Segoe UI Semibold"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 22) };
            var status = Label("");
            body.Children.Add(info); body.Children.Add(code); body.Children.Add(status);
            bool left = false;
            Show(Lang.T("Sign in with Xbox", "Вхід через Xbox"), body, Action(BackText, (s, e) => { left = true; GamePage(); }));
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            Task.Run(() => xbox.StartSignIn()).ContinueWith(start =>
            {
                if (left) return;
                if (start.IsFaulted) { status.Text = start.Exception.GetBaseException().Message; return; }
                var device = start.Result;
                info.Text = Lang.T("Open " + device.VerificationUri + " on any device, enter this code and sign in with your Xbox account. This page continues by itself.",
                    "Відкрийте " + device.VerificationUri + " на будь-якому пристрої, введіть цей код і увійдіть в обліковий запис Xbox. Ця сторінка продовжить сама.");
                code.Text = device.UserCode;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(device.VerificationUri) { UseShellExecute = true });
                status.Text = Lang.T("Waiting for sign-in…", "Очікування входу…");
                Task.Run(() => xbox.CompleteSignIn(device, () => left || !IsVisible)).ContinueWith(done =>
                {
                    if (left) return;
                    if (done.IsFaulted) status.Text = done.Exception.GetBaseException().Message;
                    else Next();
                }, ui);
            }, ui);
        }

        void Next()
        {
            if (restore != null) DownloadPage();
            else if (source == "all") ImportAllPage();
            else PickPage();
        }

        /// Before the first sign-in: which Microsoft account app ID to sign in
        /// with. Saved to openbox.ini, so this is asked once.
        void AppIdPage()
        {
            var body = new StackPanel();
            body.Children.Add(Label(Lang.T("Xbox sign-in needs a Microsoft account app ID (used only to load the achievement lists).",
                "Для входу в Xbox потрібен ID застосунку облікового запису Microsoft (лише для завантаження списків досягнень).")));
            body.Children.Add(new TextBlock { Text = Lang.T("Your own app ID (recommended)", "Власний ID застосунку (рекомендовано)"), Margin = new Thickness(0, 18, 0, 0) });
            body.Children.Add(Label(Lang.T("Azure portal → App registrations → New registration, \"Personal Microsoft accounts only\". " +
                "Then Authentication → Allow public client flows: Yes. Copy the Application (client) ID here:",
                "Портал Azure → App registrations → New registration, \"Personal Microsoft accounts only\". " +
                "Потім Authentication → Allow public client flows: Yes. Скопіюйте сюди Application (client) ID:")));
            var client = Input(settings.ClientId);
            body.Children.Add(client);
            var community = new RadioButton { Content = Lang.T("Use the app ID community Xbox tools reuse instead (it belongs to another Microsoft app; your choice)",
                "Натомість використати ID, який повторно використовують спільнотні інструменти Xbox (він належить іншому застосунку Microsoft; на ваш розсуд)"),
                Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0) };
            var own = new RadioButton { Content = Lang.T("Use my own app ID above", "Використати мій ID застосунку, вказаний вище"), Foreground = Brushes.White, IsChecked = true, Margin = new Thickness(0, 10, 0, 6) };
            client.TextChanged += (s, e) => own.IsChecked = true;
            body.Children.Add(own);
            body.Children.Add(community);
            var error = Label("");
            error.Foreground = Brushes.OrangeRed;
            error.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(error);
            Show(Lang.T("Xbox sign-in", "Вхід у Xbox"), body, Action(BackText, (s, e) => GamePage()), Action(Lang.T("Continue", "Продовжити"), (s, e) =>
            {
                string id = client.Text.Trim();
                if (own.IsChecked == true && !Guid.TryParse(id, out _))
                {
                    error.Text = Lang.T("Paste the Application (client) ID, like 12345678-1234-1234-1234-123456789abc.",
                        "Вставте Application (client) ID, наприклад 12345678-1234-1234-1234-123456789abc.");
                    return;
                }
                settings.ClientId = own.IsChecked == true ? id : "";
                settings.UseCommunityClientId = community.IsChecked == true;
                settings.Save();
                SignInPage();
            }, true));
        }

        void PickPage()
        {
            var body = new DockPanel();
            var top = new StackPanel();
            DockPanel.SetDock(top, Dock.Top);
            top.Children.Add(Label(Lang.T("Search your Xbox games", "Пошук серед ваших ігор Xbox")));
            var search = Input();
            top.Children.Add(search);
            body.Children.Add(top);
            var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Children.Add(Label(Lang.T("…or enter a title ID (hex like 1A96545B, or decimal)", "…або введіть Title ID (шістнадцятковий, як 1A96545B, або десятковий)")));
            var idBox = Input(titleId == 0 ? "" : titleId.ToString("X8"));
            bottom.Children.Add(idBox);
            var status = Label(Lang.T("Loading your games…", "Завантаження ваших ігор…"));
            bottom.Children.Add(status);
            var progressBox = ProgressBox();
            bottom.Children.Add(progressBox);
            body.Children.Add(bottom);
            var list = new ListBox { Background = Field, BorderBrush = Line, Foreground = Brushes.White };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            body.Children.Add(list);
            var titles = new List<Dictionary<string, object>>();
            // Every Xbox game (Microsoft Store), below the account's own.
            var store = new List<Dictionary<string, object>>();
            var storeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            string storeQuery = "";

            void Fill()
            {
                list.Items.Clear();
                string filter = search.Text.Trim();
                var mine = titles.Where(t => filter.Length == 0 || ((string)t["name"]).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                var rows = mine.Concat(filter.Length == 0 ? Enumerable.Empty<Dictionary<string, object>>() : store.Where(s => !mine.Any(m => string.Equals((string)m["name"], (string)s["name"], StringComparison.OrdinalIgnoreCase))));
                foreach (var t in rows)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4), Tag = t };
                    var image = new Image { Width = 48, Height = 48, Margin = new Thickness(0, 0, 12, 0) };
                    if (t.TryGetValue("displayImage", out var url) && url is string u)
                        try { image.Source = new BitmapImage(new Uri(u)) { DecodePixelWidth = 96 }; } catch (Exception) { }
                    row.Children.Add(image);
                    var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    text.Children.Add(new TextBlock { Text = (string)t["name"], FontFamily = new FontFamily("Segoe UI Semibold") });
                    string totals = "";
                    if (t.TryGetValue("achievement", out var a) && a is Dictionary<string, object> ach)
                        totals = Convert.ToString(ach["totalAchievements"]) + Lang.T(" achievements · ", " досягн. · ") + Convert.ToString(ach["totalGamerscore"]) + " G";
                    else if (t.ContainsKey("productId")) totals = Lang.T("Not played on this account · Microsoft Store", "Не грали з цього облікового запису · Microsoft Store");
                    text.Children.Add(new TextBlock { Text = totals, Foreground = Muted, FontSize = 13 });
                    row.Children.Add(text);
                    list.Items.Add(row);
                }
            }
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            storeTimer.Tick += (s, e) =>
            {
                storeTimer.Stop();
                string query = search.Text.Trim();
                if (query.Length < 2 || query == storeQuery) return;
                storeQuery = query;
                Task.Run(() => Xbox.StoreSearch(query)).ContinueWith(task =>
                {
                    if (query != storeQuery) return;
                    store = task.IsFaulted ? new List<Dictionary<string, object>>() : task.Result;
                    Fill();
                }, ui);
            };
            search.TextChanged += (s, e) => { Fill(); storeTimer.Stop(); storeTimer.Start(); };
            list.SelectionChanged += (s, e) =>
            {
                if (list.SelectedItem is FrameworkElement row && row.Tag is Dictionary<string, object> t)
                {
                    if (name.Length == 0 || source == "xbox") name = (string)t["name"];
                    if (t.TryGetValue("titleId", out var known))
                    {
                        idBox.Text = uint.Parse(Convert.ToString(known, CultureInfo.InvariantCulture)).ToString("X8");
                        return;
                    }
                    // A store game: its title ID from the catalog.
                    idBox.Text = "";
                    status.Text = Lang.T("Looking up ", "Пошук ") + t["name"] + "…";
                    string product = (string)t["productId"];
                    Task.Run(() => Xbox.StoreTitleId(product)).ContinueWith(task =>
                    {
                        if (list.SelectedItem != row) return;
                        if (task.IsFaulted || task.Result == 0)
                        {
                            status.Text = t["name"] + Lang.T(" has no Xbox title ID (no achievements).", " не має Xbox Title ID (немає досягнень).");
                            return;
                        }
                        t["titleId"] = task.Result;
                        idBox.Text = task.Result.ToString("X8");
                        status.Text = t["name"] + ": Title ID " + task.Result.ToString("X8") + ".";
                    }, ui);
                }
            };
            list.MouseDoubleClick += (s, e) => { if (list.SelectedItem != null) Start(); };

            void Start()
            {
                settings.ImportProgress = progressBox.IsChecked == true;
                settings.Save();
                string text = idBox.Text.Trim();
                if (!(uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out titleId) && text.Length == 8) && !uint.TryParse(text, out titleId))
                { status.Text = Lang.T("Pick a game or enter its title ID.", "Виберіть гру або введіть її Title ID."); return; }
                if (name.Length == 0) name = Lang.T("Game ", "Гра ") + titleId.ToString("X8");
                DownloadPage();
            }

            Show(Lang.T("Choose the game", "Вибір гри"), body, Action(BackText, (s, e) => GamePage()),
                Action(Lang.T("Sign out", "Вийти з облікового запису"), (s, e) => { xbox.SignOut(); SignInPage(); }), Action(NextText, (s, e) => Start(), true));
            Task.Run(() => xbox.TitleHistory()).ContinueWith(task =>
            {
                if (task.IsFaulted) { status.Text = task.Exception.GetBaseException().Message; return; }
                titles = task.Result.OfType<Dictionary<string, object>>().Where(t => t.ContainsKey("name") && t.ContainsKey("titleId")).ToList();
                status.Text = Lang.T(titles.Count + " games on this account.", "Ігор у цьому обліковому записі: " + titles.Count + ".");
                Fill();
            }, ui);
        }

        void DownloadPage()
        {
            var body = new StackPanel();
            var status = Label(Lang.T("Loading the achievements of ", "Завантаження досягнень гри ") + name + "…");
            var bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 18, 0, 0), Foreground = Green, Background = Field, BorderThickness = new Thickness(0) };
            body.Children.Add(status); body.Children.Add(bar);
            Show(Lang.T("Loading from Xbox", "Завантаження з Xbox"), body);
            if (restore == null) folder = UniqueFolder(Slug(name));
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            var progress = new Progress<(int Done, int Total)>(p => { bar.Maximum = Math.Max(1, p.Total); bar.Value = p.Done; status.Text = Lang.T("Art ", "Зображення ") + p.Done + Lang.T(" of ", " з ") + p.Total + "…"; });
            Task.Run(() => DownloadXbox(progress)).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    status.Text = task.Exception.GetBaseException().Message;
                    Show(Lang.T("Loading from Xbox", "Завантаження з Xbox"), body, restore != null ? Action(CloseText, (s, e) => Close()) : Action(BackText, (s, e) => PickPage()));
                    return;
                }
                if (restore != null) { finished(restore.Info.Id); Close(); }
                else Finish();
            }, ui);
        }

        string UniqueFolder(string slug)
        {
            string path = Path.Combine(Paths.Games, slug);
            for (int i = 2; File.Exists(Path.Combine(path, Game.ManifestFile)); ++i) path = Path.Combine(Paths.Games, slug + "-" + i);
            Directory.CreateDirectory(path);
            return path;
        }

        void DownloadXbox(IProgress<(int, int)> progress) => catalog = Import(xbox, titleId, name, folder, true, settings.ImportProgress, out _, progress);

        /// One title from achievements.xboxlive.com into a
        /// game: achievement_data.json in app data, the ids in the manifest and, with art,
        /// the icons and boxart.jpg.
        static Catalog Import(Xbox xbox, uint titleId, string name, string folder, bool art, bool progressToo, out int unlockedCount, IProgress<(int, int)> progress)
        {
            string local = Paths.LocalGame(Path.GetFileName(folder));
            Directory.CreateDirectory(local);
            var items = xbox.Achievements(titleId);
            var unlocks = new Dictionary<string, Unlock>();
            // Xbox 360 titles: the older list, without art; this account's unlocks separately.
            if (items.Length == 0)
            {
                items = xbox.Achievements360(titleId);
                if (progressToo)
                    foreach (Dictionary<string, object> item in xbox.Unlocked360(titleId))
                        if (item.TryGetValue("unlocked", out var done) && done is bool yes && yes)
                            unlocks[Convert.ToString(item["id"], CultureInfo.InvariantCulture)] = new Unlock { Time = Text(item, "timeUnlocked"), Source = "xbox" };
            }
            var title = xbox.Title(titleId);
            var catalog = new Catalog { TitleName = title.TryGetValue("name", out var n) ? (string)n : name };
            var icons = new List<(Achievement, string)>();
            foreach (Dictionary<string, object> item in items)
            {
                var a = new Achievement
                {
                    Id = Convert.ToString(item["id"], CultureInfo.InvariantCulture), Name = (string)item["name"],
                    Description = Text(item, "description"), LockedDescription = Text(item, "lockedDescription"),
                    IsSecret = item.TryGetValue("isSecret", out var secret) && secret is bool b && b,
                };
                if (item.TryGetValue("gamerscore", out var score)) a.Gamerscore = Convert.ToInt32(score);
                if (item.TryGetValue("rewards", out var rewards))
                    foreach (Dictionary<string, object> reward in Json.List(rewards))
                        if ((string)reward["type"] == "Gamerscore") a.Gamerscore = int.Parse((string)reward["value"]);
                if (item.TryGetValue("rarity", out var rarity) && rarity is Dictionary<string, object> r && r.TryGetValue("currentPercentage", out var percent))
                    a.RarityPercent = Convert.ToDouble(percent);
                if (item.TryGetValue("mediaAssets", out var assets))
                    foreach (Dictionary<string, object> asset in Json.List(assets))
                        if ((string)asset["type"] == "Icon") icons.Add((a, (string)asset["url"]));
                // Xbox 360: the legacy image server, by title and image ID (hex);
                // some games' images are gone from it, and stay without art.
                if (item.TryGetValue("imageId", out var image) && !item.ContainsKey("mediaAssets"))
                    icons.Add((a, "https://image-ssl.xboxlive.com/global/t." + titleId.ToString("x8") + "/ach/0/" + Convert.ToInt32(image).ToString("x")));
                string existing = Path.Combine(local, a.Id + ".jpg");
                if (File.Exists(existing)) a.Icon = a.Id + ".jpg";
                if (progressToo && Text(item, "progressState") == "Achieved")
                    unlocks[a.Id] = new Unlock
                    {
                        Time = item.TryGetValue("progression", out var p) && p is Dictionary<string, object> pd ? Text(pd, "timeUnlocked") : "",
                        Source = "xbox",
                    };
                catalog.Achievements.Add(a);
            }
            catalog.Achievements = catalog.Achievements.OrderBy(a => int.TryParse(a.Id, out var number) ? number : int.MaxValue).ToList();
            if (art)
            {
                for (int i = 0; i < icons.Count; ++i)
                {
                    progress?.Report((i, icons.Count));
                    var (a, url) = icons[i];
                    if (a.Icon != null) continue;
                    try { Art.Save(xbox.Download(url), Path.Combine(local, a.Id + ".jpg"), 1280); a.Icon = a.Id + ".jpg"; } catch (Exception) { }
                }
                progress?.Report((icons.Count, icons.Count));
                if (title.TryGetValue("displayImage", out var box) && !File.Exists(Path.Combine(local, "boxart.jpg")))
                    try { Art.Save(xbox.Download((string)box), Path.Combine(local, "boxart.jpg"), 600); } catch (Exception) { }
            }
            Game.SaveCatalog(folder, catalog, false);
            Game.UpdateManifest(folder, info => { if (string.IsNullOrEmpty(info.Name)) info.Name = name; info.XboxTitleId = titleId; });
            unlockedCount = unlocks.Count;
            if (unlocks.Count > 0) Game.Save(Path.Combine(local, Game.ImportedUnlocksFile), unlocks);
            return catalog;
        }

        /// Every game with achievements in the account's title history. A game
        /// already here (same title ID) is updated; its rules, progress,
        /// process and events stay as they are.
        void ImportAllPage()
        {
            var body = new StackPanel();
            var art = new CheckBox { Content = Lang.T("Download art (icons, box art) – slower, larger", "Завантажити зображення (значки, обкладинки) – повільніше й більше"), IsChecked = true, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 14) };
            body.Children.Add(Label(Lang.T("Imports every game with achievements from your Xbox title history into the games folder.",
                "Імпортує кожну гру з досягненнями з історії ігор Xbox у папку ігор.")));
            body.Children.Add(art);
            var progressBox = ProgressBox();
            body.Children.Add(progressBox);
            var status = Label("");
            var bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 14, 0, 0), Foreground = Green, Background = Field, BorderThickness = new Thickness(0) };
            var log = new TextBox { IsReadOnly = true, Background = Field, Foreground = Muted, BorderBrush = Line, Height = 300, Margin = new Thickness(0, 14, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
            body.Children.Add(status); body.Children.Add(bar); body.Children.Add(log);
            bool stop = false, running = false;
            Button start = null, cancel = null;
            cancel = Action(CloseText, (s, e) => { if (running) { stop = true; cancel.Content = Lang.T("Stopping…", "Зупинка…"); } else Close(); });
            start = Action(Lang.T("Import", "Імпортувати"), (s, e) =>
            {
                if (running) return;
                running = true;
                start.IsEnabled = false;
                art.IsEnabled = false;
                cancel.Content = Lang.T("Stop", "Зупинити");
                bool withArt = art.IsChecked == true;
                bool withProgress = settings.ImportProgress = progressBox.IsChecked == true;
                settings.Save();
                progressBox.IsEnabled = false;
                var ui = TaskScheduler.FromCurrentSynchronizationContext();
                var report = new Progress<string>(line => { log.AppendText(line + Environment.NewLine); log.ScrollToEnd(); });
                var count = new Progress<(int Done, int Total, string Name)>(c => { bar.Maximum = Math.Max(1, c.Total); bar.Value = c.Done; status.Text = c.Done + Lang.T(" of ", " з ") + c.Total + (c.Name != null ? " – " + c.Name : ""); });
                Task.Run(() =>
                {
                    var titles = xbox.TitleHistory().OfType<Dictionary<string, object>>()
                        .Where(t => t.ContainsKey("titleId") && t.TryGetValue("achievement", out var a) && a is Dictionary<string, object> d && Convert.ToInt32(d["totalAchievements"]) > 0)
                        .ToList();
                    var existing = Game.LoadAll();
                    int done = 0, added = 0, updated = 0, failed = 0;
                    foreach (var t in titles)
                    {
                        if (stop) break;
                        uint id = uint.Parse(Convert.ToString(t["titleId"], CultureInfo.InvariantCulture));
                        string title = (string)t["name"];
                        ((IProgress<(int, int, string)>)count).Report((done, titles.Count, title));
                        try
                        {
                            var game = existing.FirstOrDefault(g => g.Info.XboxTitleId == id);
                            string target = game?.Folder ?? UniqueFolder(Slug(title));
                            Import(xbox, id, title, target, withArt, withProgress, out int unlockedHere, null);
                            Game.UpdateManifest(target, info => info.Name = title);
                            if (game == null) added++; else updated++;
                            ((IProgress<string>)report).Report((game == null ? Lang.T("Added   ", "Додано    ") : Lang.T("Updated ", "Оновлено  ")) + title
                                + (withProgress ? "  (" + Lang.T("unlocked: ", "відкрито: ") + unlockedHere + ")" : ""));
                        }
                        catch (Exception error) { failed++; ((IProgress<string>)report).Report(Lang.T("Failed  ", "Помилка   ") + title + ": " + error.Message); }
                        done++;
                        System.Threading.Thread.Sleep(400); // gentle on the service
                    }
                    ((IProgress<(int, int, string)>)count).Report((done, titles.Count, null));
                    return (added, updated, failed);
                }).ContinueWith(task =>
                {
                    running = false;
                    cancel.Content = CloseText;
                    if (task.IsFaulted) { status.Text = task.Exception.GetBaseException().Message; return; }
                    var (added, updated, failed) = task.Result;
                    status.Text = Lang.T(added + " added, " + updated + " updated" + (failed > 0 ? ", " + failed + " failed" : "") + ".",
                        "Додано: " + added + ", оновлено: " + updated + (failed > 0 ? ", з помилкою: " + failed : "") + ".");
                    finished(null);
                }, ui);
            }, true);
            Show(Lang.T("All my Xbox games", "Усі мої ігри Xbox"), body, cancel, start);
        }

        static string BackText => Lang.T("Back", "Назад");
        static string NextText => Lang.T("Next", "Далі");
        static string CloseText => Lang.T("Close", "Закрити");

        /// "Obtain progress from the account": remembered in openbox.ini.
        CheckBox ProgressBox() => new CheckBox
        {
            Content = Lang.T("Obtain progress from the account (achievements unlocked on Xbox are unlocked here too)",
                "Отримати прогрес з облікового запису (досягнення, відкриті на Xbox, буде відкрито й тут)"),
            IsChecked = settings.ImportProgress, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 4),
        };

        static string Text(Dictionary<string, object> item, string key) =>
            item.TryGetValue(key, out var value) ? value as string ?? "" : "";

        void ManualPage()
        {
            var body = new DockPanel();
            var form = new StackPanel();
            DockPanel.SetDock(form, Dock.Top);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            var nameBox = Input();
            var scoreBox = Input("10");
            scoreBox.Margin = new Thickness(12, 6, 0, 14);
            var left = new StackPanel(); left.Children.Add(Label(Lang.T("Achievement name", "Назва досягнення"))); left.Children.Add(nameBox);
            var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) }; right.Children.Add(Label("Gamerscore")); right.Children.Add(scoreBox);
            scoreBox.Margin = new Thickness(0, 6, 0, 14);
            Grid.SetColumn(right, 1);
            grid.Children.Add(left); grid.Children.Add(right);
            form.Children.Add(grid);
            form.Children.Add(Label(Lang.T("Description", "Опис")));
            var descriptionBox = Input();
            form.Children.Add(descriptionBox);
            var options = new StackPanel { Orientation = Orientation.Horizontal };
            var secret = new CheckBox { Content = Lang.T("Secret", "Секретне"), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            string image = null;
            var imageLabel = Label(NoImage);
            imageLabel.Margin = new Thickness(12, 0, 0, 0);
            imageLabel.VerticalAlignment = VerticalAlignment.Center;
            options.Children.Add(secret);
            options.Children.Add(Action(Lang.T("Image…", "Зображення…"), (s, e) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Lang.T("Images", "Зображення") + "|*.jpg;*.jpeg;*.png;*.bmp" };
                if (dialog.ShowDialog(this) == true) { image = dialog.FileName; imageLabel.Text = Path.GetFileName(image); }
            }));
            options.Children.Add(imageLabel);
            form.Children.Add(options);
            var list = new ListBox { Background = Field, BorderBrush = Line, Foreground = Brushes.White, Margin = new Thickness(0, 16, 0, 0) };
            void Fill()
            {
                list.Items.Clear();
                foreach (var (a, file) in manual)
                    list.Items.Add(new TextBlock { Text = a.Name + "   ·   " + a.Gamerscore + " G" + (a.IsSecret ? "   ·   " + Lang.T("secret", "секретне") : "") + (file != null ? "   ·   " + Path.GetFileName(file) : ""), Tag = a });
            }
            var add = Action(Lang.T("Add achievement", "Додати досягнення"), (s, e) =>
            {
                if (nameBox.Text.Trim().Length == 0) return;
                int.TryParse(scoreBox.Text.Trim(), out int score);
                manual.Add((new Achievement { Id = (manual.Count + 1).ToString(CultureInfo.InvariantCulture), Name = nameBox.Text.Trim(), Description = descriptionBox.Text.Trim(), Gamerscore = Math.Max(0, score), IsSecret = secret.IsChecked == true }, image));
                nameBox.Text = descriptionBox.Text = ""; secret.IsChecked = false; image = null; imageLabel.Text = NoImage;
                Fill();
                nameBox.Focus();
            }, true);
            add.Margin = new Thickness(0, 12, 0, 0);
            add.HorizontalAlignment = HorizontalAlignment.Left;
            form.Children.Add(add);
            body.Children.Add(form);
            list.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Delete && list.SelectedIndex >= 0) { manual.RemoveAt(list.SelectedIndex); for (int i = 0; i < manual.Count; ++i) manual[i].Achievement.Id = (i + 1).ToString(CultureInfo.InvariantCulture); Fill(); }
            };
            body.Children.Add(list);
            Fill();
            Show(Lang.T("Achievements of ", "Досягнення гри ") + name, body, Action(BackText, (s, e) => GamePage()), Action(NextText, (s, e) =>
            {
                if (manual.Count == 0) return;
                folder = UniqueFolder(Slug(name));
                catalog = new Catalog { TitleName = name };
                foreach (var (a, file) in manual)
                {
                    if (file != null) { Art.Save(File.ReadAllBytes(file), Path.Combine(folder, a.Id + ".jpg"), 1280); a.Icon = a.Id + ".jpg"; }
                    catalog.Achievements.Add(a);
                }
                Game.SaveCatalog(folder, catalog, true); // the user's own texts: shareable
                Finish();
            }, true));
            nameBox.Focus();
        }

        static string NoImage => Lang.T("No image", "Без зображення");

        /// Names the game in its manifest and ends the wizard. Tracking (process,
        /// events file, rules) is set in the manifest by whoever supports the
        /// game, e.g. a mod; see docs\MANIFEST.md.
        void Finish()
        {
            Game.UpdateManifest(folder, info => { info.Name = name; info.XboxTitleId = source == "xbox" ? titleId : 0; });
            finished(Path.GetFileName(folder));
            Close();
        }
    }

    /// Image files for a game folder: JPEG, at most the given width.
    static class Art
    {
        public static void Save(byte[] data, string path, int maxWidth)
        {
            using (var input = new MemoryStream(data))
            using (var source = System.Drawing.Image.FromStream(input))
            {
                int width = Math.Min(maxWidth, source.Width), height = (int)Math.Round(source.Height * (double)width / source.Width);
                using (var bitmap = new System.Drawing.Bitmap(width, height))
                using (var g = System.Drawing.Graphics.FromImage(bitmap))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(source, 0, 0, width, height);
                    var encoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                    var parameters = new System.Drawing.Imaging.EncoderParameters(1);
                    parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                    bitmap.Save(path, encoder, parameters);
                }
            }
        }
    }
}
