using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenBoxAchievements
{
    /// openbox.ini in %LOCALAPPDATA%\OpenBoxAchievements: this user's settings.
    sealed class Settings
    {
        // Microsoft account app ID of the Minecraft launcher, which many community
        // Xbox tools reuse for device code sign-in. Used only when opted in.
        public const string CommunityClientId = "00000000402b5328";

        public string ClientId = "";
        public bool UseCommunityClientId;
        public bool Toasts = true, ToastSound = true;
        public bool AchievementArtBackground;
        public bool ImportProgress;
        public string Language = ""; // "en" or "uk"; empty: the Windows display language

        readonly string file;

        Settings(string file) { this.file = file; }

        public static Settings Load(string folder)
        {
            Directory.CreateDirectory(folder);
            var settings = new Settings(Path.Combine(folder, "openbox.ini"));
            if (!File.Exists(settings.file)) { settings.Save(); return settings; }
            foreach (var raw in File.ReadAllLines(settings.file))
            {
                string line = raw.Trim();
                int equals = line.IndexOf('=');
                if (line.StartsWith(";") || line.StartsWith("[") || equals < 0) continue;
                string key = line.Substring(0, equals).Trim(), value = line.Substring(equals + 1).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "clientid": settings.ClientId = value; break;
                    case "usecommunityclientid": settings.UseCommunityClientId = value == "1"; break;
                    case "toasts": settings.Toasts = value != "0"; break;
                    case "toastsound": settings.ToastSound = value != "0"; break;
                    case "achievementartbackground": settings.AchievementArtBackground = value == "1"; break;
                    case "importprogress": settings.ImportProgress = value == "1"; break;
                    case "language": settings.Language = value.ToLowerInvariant(); break;
                }
            }
            return settings;
        }

        public void Save()
        {
            var text = new StringBuilder();
            text.AppendLine("; OpenBox Achievements settings.");
            text.AppendLine("[OpenBox]");
            text.AppendLine("; Your own Microsoft account (Azure) app ID for Xbox sign-in, used only to load achievement lists.");
            text.AppendLine("ClientId=" + ClientId);
            text.AppendLine("; 1 = without your own ID, use the app ID community Xbox tools reuse (not ours; your choice).");
            text.AppendLine("UseCommunityClientId=" + (UseCommunityClientId ? "1" : "0"));
            text.AppendLine("; Unlock pop-ups and their chime.");
            text.AppendLine("Toasts=" + (Toasts ? "1" : "0"));
            text.AppendLine("ToastSound=" + (ToastSound ? "1" : "0"));
            text.AppendLine("; 1 = a game loaded from Xbox without its own background.jpg gets its first achievement's art as one.");
            text.AppendLine("AchievementArtBackground=" + (AchievementArtBackground ? "1" : "0"));
            text.AppendLine("; 1 = loading a game from Xbox also marks the achievements this account unlocked as unlocked here.");
            text.AppendLine("ImportProgress=" + (ImportProgress ? "1" : "0"));
            text.AppendLine("; The app's language: en (English) or uk (Ukrainian); empty = the Windows display language.");
            text.AppendLine("Language=" + Language);
            File.WriteAllText(file, text.ToString(), new UTF8Encoding(false));
        }

        /// The client ID to sign in with, or null when none is configured.
        public string SignInClientId => ClientId.Length > 0 ? ClientId : UseCommunityClientId ? CommunityClientId : null;
    }

    sealed class SettingsWindow : Window
    {
        public SettingsWindow(Settings settings, IEnumerable<Game> games, Action addGame, Action saved, Action<Game> remove)
        {
            Title = Lang.T("Settings", "Параметри") + " – OpenBox Achievements";
            Width = 720; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1B)); Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 15; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Dark.Apply(this);
            Pad.Attach(this);
            var muted = new SolidColorBrush(Color.FromRgb(0xB4, 0xB7, 0xBE));
            var body = new StackPanel { Margin = new Thickness(32, 26, 32, 26) };
            TextBlock Note(string text) => new TextBlock { Text = text, Foreground = muted, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) };
            CheckBox Check(string text, bool value) => new CheckBox { Content = text, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 12) };

            body.Children.Add(new TextBlock { Text = Lang.T("Settings", "Параметри"), FontSize = 26, FontFamily = new FontFamily("Segoe UI Semibold"), Margin = new Thickness(0, 0, 0, 18) });
            body.Children.Add(new TextBlock { Text = Lang.T("Games", "Ігри") });
            var list = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
            foreach (var game in games)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var delete = new Button { Content = Lang.T("Remove", "Видалити"), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(12, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0x27, 0x29, 0x2E)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
                DockPanel.SetDock(delete, Dock.Right);
                delete.Click += (s, e) =>
                {
                    string question = Lang.T("Remove " + game.Name + "?\n\nIts manifest, texts, art and your progress go to the Recycle Bin.",
                        "Видалити " + game.Name + "?\n\nЇї маніфест, тексти, зображення та ваш прогрес буде переміщено до Кошика.");
                    if (MessageBox.Show(this, question, Lang.T("Remove game", "Видалення гри"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
                    Close();
                    remove(game);
                };
                row.Children.Add(delete);
                row.Children.Add(new TextBlock
                {
                    Text = game.Name + "   ·   " + game.Catalog.Achievements.Count + Lang.T(" achievements", " досягн.") + "   ·   " + (game.Info.Source == "manual" ? Lang.T("manual", "вручну") : "Xbox"),
                    Foreground = muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                });
                list.Children.Add(row);
            }
            if (list.Children.Count == 0) list.Children.Add(Note(Lang.T("No games yet.", "Ігор ще немає.")));
            body.Children.Add(list);
            var add = new Button { Content = Lang.T("Add game…", "Додати гру…"), Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(0, 0, 0, 18), HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromRgb(0x27, 0x29, 0x2E)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            add.Click += (s, e) => { Close(); addGame(); };
            body.Children.Add(add);

            body.Children.Add(new TextBlock { Text = Lang.T("Notifications", "Сповіщення"), Margin = new Thickness(0, 8, 0, 0) });
            var toasts = Check(Lang.T("Show unlock pop-ups", "Показувати сповіщення про відкриті досягнення"), settings.Toasts);
            var sound = Check(Lang.T("Play the unlock chime", "Відтворювати звук відкриття"), settings.ToastSound);
            body.Children.Add(toasts);
            body.Children.Add(sound);

            body.Children.Add(new TextBlock { Text = Lang.T("Loading from Xbox", "Завантаження з Xbox"), Margin = new Thickness(0, 8, 0, 0) });
            var artBackground = Check(Lang.T("Use the first achievement's art as the background of a game without its own",
                "Використовувати зображення першого досягнення як фон гри, яка не має власного"), settings.AchievementArtBackground);
            body.Children.Add(artBackground);

            // Applied on Save: open windows and the tray menu are shown again in it.
            body.Children.Add(new TextBlock { Text = Lang.T("Language", "Мова"), Margin = new Thickness(0, 8, 0, 0) });
            var languages = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
            var english = new RadioButton { Content = "English", IsChecked = !Lang.Ukrainian, Foreground = Brushes.White, Margin = new Thickness(0, 0, 24, 0) };
            var ukrainian = new RadioButton { Content = "Українська", IsChecked = Lang.Ukrainian, Foreground = Brushes.White };
            languages.Children.Add(english);
            languages.Children.Add(ukrainian);
            body.Children.Add(languages);

            body.Children.Add(Note(Lang.T("OpenBox Achievements is not affiliated with or endorsed by Microsoft or Xbox. Texts and art loaded from " +
                "Xbox Live, your progress and the sign-in token (encrypted for your Windows account) stay in " + Paths.Data + ".",
                "OpenBox Achievements не пов'язаний із Microsoft чи Xbox і не схвалений ними. Тексти й зображення, завантажені з " +
                "Xbox Live, ваш прогрес і токен входу (зашифрований для вашого облікового запису Windows) зберігаються в " + Paths.Data + ".")));

            var save = new Button { Content = Lang.T("Save", "Зберегти"), Padding = new Thickness(26, 8, 26, 8), Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            save.Click += (s, e) =>
            {
                settings.Toasts = toasts.IsChecked == true;
                settings.ToastSound = sound.IsChecked == true;
                settings.AchievementArtBackground = artBackground.IsChecked == true;
                if (ukrainian.IsChecked != Lang.Ukrainian) settings.Language = ukrainian.IsChecked == true ? "uk" : "en";
                settings.Save();
                saved();
                Close();
            };
            body.Children.Add(save);
            Content = body;
        }
    }
}
