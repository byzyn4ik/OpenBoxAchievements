using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace OpenBoxAchievements
{
    /// Xbox Live sign-in and the achievements service, read-only, used to
    /// create catalogs. Device code sign-in (a short code at microsoft.com/link)
    /// with either the user's own Azure app ID (Microsoft identity platform,
    /// scope XboxLive.signin) or, if opted in, the legacy community ID
    /// (login.live.com). The refresh token is kept in account.bin beside the
    /// app, encrypted for the current Windows user (DPAPI).
    sealed class Xbox
    {
        const string LegacyScope = "service::user.auth.xboxlive.com::MBI_SSL";
        const string Scope = "XboxLive.signin offline_access";
        readonly string clientId;
        readonly bool legacy;

        readonly string accountFile;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        string refreshToken, userHash, xsts;
        public string Xuid { get; private set; }
        public string Gamertag { get; private set; }

        public Xbox(string dataFolder, string clientId)
        {
            this.clientId = clientId ?? throw new InvalidOperationException(Lang.T("Set your Azure app ID (or opt in to the community ID) in Settings to sign in to Xbox.",
                "Щоб увійти в Xbox, вкажіть ID застосунку Azure (або виберіть ID спільноти) у Параметрах."));
            legacy = clientId == Settings.CommunityClientId;
            accountFile = Path.Combine(dataFolder, "account.bin");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (File.Exists(accountFile))
            {
                try
                {
                    refreshToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                        File.ReadAllBytes(accountFile), null, DataProtectionScope.CurrentUser));
                }
                catch (CryptographicException) { refreshToken = null; }
            }
        }

        public bool HasAccount => !string.IsNullOrEmpty(refreshToken);

        public sealed class DeviceCode
        {
            public string UserCode, VerificationUri, Code;
            public int Interval, ExpiresIn;
        }

        /// Starts a device code sign-in: the user enters UserCode at VerificationUri.
        public DeviceCode StartSignIn()
        {
            var reply = legacy
                ? Post("https://login.live.com/oauth20_connect.srf",
                    "client_id=" + clientId + "&scope=" + Uri.EscapeDataString(LegacyScope) + "&response_type=device_code",
                    "application/x-www-form-urlencoded", null)
                : Post("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
                    "client_id=" + Uri.EscapeDataString(clientId) + "&scope=" + Uri.EscapeDataString(Scope),
                    "application/x-www-form-urlencoded", null);
            return new DeviceCode
            {
                UserCode = (string)reply["user_code"], VerificationUri = (string)reply["verification_uri"],
                Code = (string)reply["device_code"], Interval = Convert.ToInt32(reply["interval"]), ExpiresIn = Convert.ToInt32(reply["expires_in"]),
            };
        }

        /// Waits until the user has signed in (or the code expires).
        public void CompleteSignIn(DeviceCode code, Func<bool> cancelled)
        {
            var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
            while (DateTime.UtcNow < deadline && !cancelled())
            {
                System.Threading.Thread.Sleep(Math.Max(1, code.Interval) * 1000);
                try
                {
                    MicrosoftToken("grant_type=urn:ietf:params:oauth:grant-type:device_code&device_code=" + Uri.EscapeDataString(code.Code));
                    return;
                }
                catch (InvalidOperationException error) when (error.Message.Contains("authorization_pending")) { }
                catch (InvalidOperationException error) when (error.Message.Contains("slow_down")) { code.Interval += 5; }
            }
            throw new InvalidOperationException(Lang.T("The sign-in code expired. Try again.", "Термін дії коду входу минув. Спробуйте ще раз."));
        }

        public void SignOut()
        {
            refreshToken = xsts = null;
            if (File.Exists(accountFile)) File.Delete(accountFile);
        }

        void MicrosoftToken(string grant)
        {
            var reply = legacy
                ? Post("https://login.live.com/oauth20_token.srf",
                    grant + "&client_id=" + clientId + "&scope=" + Uri.EscapeDataString(LegacyScope), "application/x-www-form-urlencoded", null)
                : Post("https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                    grant + "&client_id=" + Uri.EscapeDataString(clientId) + "&scope=" + Uri.EscapeDataString(Scope), "application/x-www-form-urlencoded", null);
            refreshToken = (string)reply["refresh_token"];
            File.WriteAllBytes(accountFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken),
                null, DataProtectionScope.CurrentUser));
            // The identity platform's tokens go to Xbox Live with a "d=" prefix.
            XboxTokens(legacy ? (string)reply["access_token"] : "d=" + (string)reply["access_token"]);
        }

        void XboxTokens(string accessToken)
        {
            var user = Post("https://user.auth.xboxlive.com/user/authenticate", json.Serialize(new Dictionary<string, object>
            {
                ["Properties"] = new Dictionary<string, object> { ["AuthMethod"] = "RPS", ["SiteName"] = "user.auth.xboxlive.com", ["RpsTicket"] = accessToken },
                ["RelyingParty"] = "http://auth.xboxlive.com",
                ["TokenType"] = "JWT",
            }), "application/json", null);
            var authorized = Post("https://xsts.auth.xboxlive.com/xsts/authorize", json.Serialize(new Dictionary<string, object>
            {
                ["Properties"] = new Dictionary<string, object> { ["SandboxId"] = "RETAIL", ["UserTokens"] = new[] { (string)user["Token"] } },
                ["RelyingParty"] = "http://xboxlive.com",
                ["TokenType"] = "JWT",
            }), "application/json", null);
            xsts = (string)authorized["Token"];
            var claims = (Dictionary<string, object>)Json.List(((Dictionary<string, object>)authorized["DisplayClaims"])["xui"])[0];
            userHash = (string)claims["uhs"];
            Xuid = claims.ContainsKey("xid") ? (string)claims["xid"] : null;
            Gamertag = claims.ContainsKey("gtg") ? (string)claims["gtg"] : null;
        }

        void EnsureSignedIn()
        {
            if (xsts != null) return;
            if (!HasAccount) throw new InvalidOperationException(Lang.T("Not signed in.", "Вхід не виконано."));
            MicrosoftToken("grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refreshToken));
        }

        /// A title's achievements with this account's progress (contract 2).
        public object[] Achievements(uint titleId)
        {
            EnsureSignedIn();
            var reply = Get("https://achievements.xboxlive.com/users/xuid(" + Xuid + ")/achievements?titleId=" + titleId + "&maxItems=1000", "2");
            return Json.List(reply["achievements"]);
        }

        /// An Xbox 360 title's achievements (contract 2 lists none for these):
        /// every achievement of the title, with rarity, but no image URLs.
        public object[] Achievements360(uint titleId)
        {
            EnsureSignedIn();
            var reply = Get("https://achievements.xboxlive.com/users/xuid(" + Xuid + ")/titleachievements?titleId=" + titleId + "&maxItems=1000", "3");
            return Json.List(reply["achievements"]);
        }

        /// The achievements this account unlocked in an Xbox 360 title.
        public object[] Unlocked360(uint titleId)
        {
            EnsureSignedIn();
            var reply = Get("https://achievements.xboxlive.com/users/xuid(" + Xuid + ")/achievements?titleId=" + titleId + "&maxItems=1000", "3");
            return Json.List(reply["achievements"]);
        }

        /// Title name and artwork for the page header.
        public Dictionary<string, object> Title(uint titleId)
        {
            EnsureSignedIn();
            var reply = Get("https://titlehub.xboxlive.com/users/xuid(" + Xuid + ")/titles/titleid(" + titleId + ")/decoration/image,achievement", "2");
            var titles = Json.List(reply["titles"]);
            return titles.Length > 0 ? (Dictionary<string, object>)titles[0] : new Dictionary<string, object>();
        }

        /// The account's played titles (name, titleId, displayImage, achievement totals).
        public object[] TitleHistory()
        {
            EnsureSignedIn();
            var reply = Get("https://titlehub.xboxlive.com/users/xuid(" + Xuid + ")/titles/titlehistory/decoration/image,achievement?maxItems=1000", "2");
            return Json.List(reply["titles"]);
        }

        /// Any game by name, from the public Microsoft Store catalog (no sign-in):
        /// name, store product ID and box art. Games sold only on Xbox 360 are
        /// not in it; those are added by title ID.
        public static List<Dictionary<string, object>> StoreSearch(string query)
        {
            var found = new List<Dictionary<string, object>>();
            var reply = StoreGet("https://displaycatalog.mp.microsoft.com/v7.0/productFamilies/autosuggest?market=US&languages=en-US&productFamilyNames=Games&query=" + Uri.EscapeDataString(query));
            if (!reply.TryGetValue("Results", out var results)) return found;
            foreach (Dictionary<string, object> family in Json.List(results))
                foreach (Dictionary<string, object> product in Json.List(family["Products"]))
                {
                    if ((string)product["Type"] != "Game") continue;
                    string icon = product.TryGetValue("Icon", out var i) ? i as string : null;
                    found.Add(new Dictionary<string, object>
                    {
                        ["name"] = (string)product["Title"], ["productId"] = (string)product["ProductId"],
                        ["displayImage"] = icon != null && icon.StartsWith("//") ? "https:" + icon : icon,
                    });
                }
            return found;
        }

        /// A store product's Xbox title ID, or 0 when it has none.
        public static uint StoreTitleId(string productId)
        {
            var reply = StoreGet("https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds=" + Uri.EscapeDataString(productId) + "&market=US&languages=en-US");
            foreach (Dictionary<string, object> product in Json.List(reply["Products"]))
                if (product.TryGetValue("AlternateIds", out var ids))
                    foreach (Dictionary<string, object> id in Json.List(ids))
                        if ((string)id["IdType"] == "XboxTitleId" && uint.TryParse((string)id["Value"], out uint titleId)) return titleId;
            return 0;
        }

        static Dictionary<string, object> StoreGet(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var client = new WebClient { Encoding = Encoding.UTF8 })
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(client.DownloadString(url));
        }

        public byte[] Download(string url)
        {
            using (var client = new WebClient()) return client.DownloadData(url);
        }

        Dictionary<string, object> Get(string url, string contract) => GetPolitely(() =>
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Headers["Authorization"] = "XBL3.0 x=" + userHash + ";" + xsts;
            request.Headers["x-xbl-contract-version"] = contract;
            request.Headers["Accept-Language"] = System.Globalization.CultureInfo.CurrentUICulture.Name;
            request.Accept = "application/json";
            return Read(request);
        });

        Dictionary<string, object> Post(string url, string body, string type, string contract)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = type;
            request.Accept = "application/json";
            if (contract != null) request.Headers["x-xbl-contract-version"] = contract;
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            return Read(request);
        }

        Dictionary<string, object> Read(HttpWebRequest request)
        {
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
            }
            catch (WebException error) when (error.Response != null)
            {
                var status = ((HttpWebResponse)error.Response).StatusCode;
                using (var reader = new StreamReader(error.Response.GetResponseStream()))
                    throw new XboxError(status, "Xbox Live: " + status + " " + reader.ReadToEnd(), error.Response.Headers["Retry-After"]);
            }
        }

        /// GETs are retried when the service asks to slow down (HTTP 429).
        Dictionary<string, object> GetPolitely(Func<Dictionary<string, object>> get)
        {
            for (int attempt = 0; ; ++attempt)
            {
                try { return get(); }
                catch (XboxError error) when ((int)error.Status == 429 && attempt < 6)
                {
                    int seconds = int.TryParse(error.RetryAfter, out int s) ? s : 15;
                    System.Threading.Thread.Sleep(Math.Min(120, Math.Max(2, seconds)) * 1000);
                }
            }
        }
    }

    sealed class XboxError : InvalidOperationException
    {
        public readonly HttpStatusCode Status;
        public readonly string RetryAfter;
        public XboxError(HttpStatusCode status, string message, string retryAfter) : base(message) { Status = status; RetryAfter = retryAfter; }
    }
}
