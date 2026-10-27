using System.Globalization;

namespace OpenBoxAchievements
{
    /// The UI language: English or Ukrainian. Each user-visible string is
    /// written at its place in both languages, T("English", "Українська").
    /// Achievement names and descriptions (Xbox data), logs and developer
    /// messages are not translated.
    static class Lang
    {
        public static bool Ukrainian { get; private set; }

        /// "en" or "uk".
        public static string Code => Ukrainian ? "uk" : "en";

        /// setting: openbox.ini's Language ("en", "uk", or empty for the Windows UI language).
        public static void Apply(string setting) =>
            Ukrainian = setting == "uk" || setting != "en" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "uk";

        public static string T(string english, string ukrainian) => Ukrainian ? ukrainian : english;
    }
}
