// Reference: 2.0.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;
using Oxide.Game.Rust.Cui;

namespace Oxide.Plugins
{
    [Info("KitsPlus", "AliGhasab", "1.1.0")]
    [Description("Modern UI kits with Persian localization, daily/weekly, streaks, team-triggered kits (3/4), and removal >5, time windows, costs, random bundles, and more.")]
    public class KitsPlus : RustPlugin
    {
        #region Optional Integrations
        [PluginReference] private Plugin Economics;        // optional
        [PluginReference] private Plugin ServerRewards;    // optional
        [PluginReference] private Plugin ImageLibrary;     // optional (icons)
        [PluginReference] private Plugin LevelSystem;      // optional (XP/Level)
        #endregion

        #region Permissions
        private const string PERM_USE = "kitsplus.use";
        private const string PERM_ADMIN = "kitsplus.admin";
        private string PermForKit(string kitName) => $"kitsplus.kit.{kitName.ToLower()}";
        #endregion

        #region Config & Data Models

        private PluginConfig _config;

        private class PluginConfig
        {
            public bool UseEconomics = true;
            public bool UseServerRewards = false;
            public string CurrencyFormat = "{0:N0}";
            public bool AllowAuthLevelAdmin = true;

            public AutoKitRules AutoKits = new AutoKitRules();
            public StreakConfig Streaks = new StreakConfig();
            public UIConfig UI = new UIConfig();
            public TeamUnlockConfig TeamUnlock = new TeamUnlockConfig();
            public Dictionary<string, int> StreakRewards = new Dictionary<string, int>(); // e.g. {"streak-3":3,"streak-7":7}

            public class AutoKitRules
            {
                public List<string> OnFirstConnect = new List<string>();
                public List<string> OnRespawn = new List<string>();
                public List<string> OnConnect = new List<string>();
                public Dictionary<string, int> Priority = new Dictionary<string, int>(); // kit -> weight
            }

            public class UIConfig
            {
                // Modern centered window
                public float UIScale = 1.0f;
                public int CardsPerRow = 3;

                // Glassmorphism layers
                public string Backdrop = "0 0 0 0.60";            // background dimmer
                public string Glass = "0.10 0.10 0.14 0.88";     // main window
                public string GlassTop = "0.16 0.16 0.22 0.95";  // header strip
                public string Accent = "0.35 0.20 0.90 1.00";    // purple/blue accent
                public string AccentSoft = "0.35 0.20 0.90 0.25";
                public string Text = "1 1 1 0.96";
                public string TextDim = "1 1 1 0.78";
                public string Card = "0.12 0.12 0.16 0.90";
                public string CardBorder = "0.65 0.45 1.00 0.25";

                public bool EnableChatHints = true;
                public bool ShowCooldownBars = true;
            }

            public class TeamUnlockConfig
            {
                public bool Enable = true;
                public string KitAt3 = "team3"; // name of kit available when team size >=3
                public string KitAt4 = "team4"; // name of kit additionally available when team size >=4
                public int RemoveAbove = 5;      // strictly greater than this => remove both
                public bool Notify = true;       // show chat hints on threshold changes
            }
        }

        private class StreakConfig
        {
            public bool Enable = true;
            public int ResetIfMissDays = 1;
            public string DailyKitName = "daily";
        }

        private class StoredData
        {
            public string WipeId;
            public Dictionary<ulong, PlayerData> Players = new Dictionary<ulong, PlayerData>();
            public Dictionary<ulong, int> TeamLastKnownSize = new Dictionary<ulong, int>(); // teamId -> last size for notifications
        }

        private class PlayerData
        {
            public Dictionary<string, KitUsage> Kits = new Dictionary<string, KitUsage>(); // key: kit name
            public int StreakDays = 0;
            public DateTime? LastDaily; // for streak chains
        }

        private class KitUsage
        {
            public int Uses = 0;
            public DateTime? LastClaim;
            public string LastWipeId; // for onetime-per-wipe
        }

        private class ItemEntry
        {
            public string ShortName;
            public int Amount;
            public int Skin = 0;
            public string Container = "main"; // "main","belt","wear"
        }

        private class TimeWindow
        {
            public string FromISO; // "2025-08-10T00:00:00Z"
            public string ToISO;
            public List<DayOfWeek> Days = new List<DayOfWeek>(); // e.g. Saturday,Sunday

            public bool IsActiveNow(DateTime now)
            {
                if (!string.IsNullOrEmpty(FromISO))
                {
                    if (!DateTime.TryParse(FromISO, null, DateTimeStyles.AdjustToUniversal, out var from)) return true;
                    if (now < from) return false;
                }
                if (!string.IsNullOrEmpty(ToISO))
                {
                    if (!DateTime.TryParse(ToISO, null, DateTimeStyles.AdjustToUniversal, out var to)) return true;
                    if (now > to) return false;
                }
                if (Days != null && Days.Count > 0 && !Days.Contains(now.DayOfWeek)) return false;
                return true;
            }
        }

        private class Cost
        {
            public double Money = 0;  // Economics
            public int RP = 0;        // ServerRewards
        }

        private class KitDef
        {
            public string Name;
            public string DisplayName;
            public string Description;
            public string IconUrl; // if ImageLibrary present
            public string Permission; // optional custom permission gate
            public int AuthLevel = 0; // 1/2 for admin-only if desired
            public string Category = "General";

            public List<ItemEntry> Items = new List<ItemEntry>();

            public string Cooldown = "0"; // e.g., "30m", "2h", "1d"
            public int MaxUses = 0;       // 0 = unlimited
            public bool OneTime = false;  // once ever
            public bool ResetOnWipe = true;

            public bool Daily = false;    // once per 24h
            public bool Weekly = false;   // once per 7d
            public bool Randomize = false;
            public int Rolls = 0;         // number of random rolls if Randomize == true
            public bool TeamShared = false; // give to whole online team on claim

            public int MinLevel = 0;      // requires LevelSystem plugin (optional)
            public TimeWindow Window = new TimeWindow();
            public Cost Cost = new Cost();
        }

        private class KitsData
        {
            public Dictionary<string, KitDef> Kits = new Dictionary<string, KitDef>(StringComparer.OrdinalIgnoreCase);
        }

        private PluginConfig.TeamUnlockConfig TU => _config.TeamUnlock;

        private KitsData _kits;
        private StoredData _db;

        #endregion

        #region Save/Load

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig
            {
                UseEconomics = true,
                UseServerRewards = false,
                CurrencyFormat = "{0:N0}",
                AllowAuthLevelAdmin = true,
                AutoKits = new PluginConfig.AutoKitRules
                {
                    OnFirstConnect = new List<string> { "starter" },
                    OnRespawn = new List<string> { "starter" },
                    OnConnect = new List<string>(),
                    Priority = new Dictionary<string, int> { { "pvp", 10 }, { "starter", 5 } }
                },
                Streaks = new StreakConfig(),
                UI = new PluginConfig.UIConfig(),
                TeamUnlock = new PluginConfig.TeamUnlockConfig
                {
                    Enable = true,
                    KitAt3 = "team3",
                    KitAt4 = "team4",
                    RemoveAbove = 5,
                    Notify = true
                },
                StreakRewards = new Dictionary<string, int> { { "streak-3", 3 }, { "streak-7", 7 } }
            };
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("null config");
            }
            catch
            {
                PrintWarning("Config invalid; loading defaults.");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultMessages()
        {
            // Persian
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "شما اجازه انجام این کار را ندارید.",
                ["NoUsePermission"] = "برای استفاده از کیت‌ها نیاز به مجوز دارید.",
                ["KitNotFound"] = "کیت مورد نظر پیدا نشد.",
                ["KitNoPermission"] = "برای دریافت این کیت دسترسی ندارید.",
                ["KitAuthDenied"] = "این کیت مخصوص ادمین است.",
                ["Cooldown"] = "کول‌داون باقی‌مانده: {0}",
                ["MaxUses"] = "سقف استفاده از این کیت به پایان رسیده است.",
                ["OneTime"] = "این کیت یک‌بار مصرف است و قبلاً دریافت شده.",
                ["NotInWindow"] = "این کیت در حال حاضر فعال نیست.",
                ["NeedLevel"] = "برای دریافت این کیت باید حداقل لول {0} داشته باشید.",
                ["NeedMoney"] = "برای دریافت این کیت باید {0} پول داشته باشید.",
                ["NeedRP"] = "برای دریافت این کیت باید {0} امتیاز داشته باشید.",
                ["ClaimSuccess"] = "کیت «{0}» با موفقیت دریافت شد.",
                ["ClaimTeamAlso"] = "این کیت برای اعضای آنلاین تیم شما نیز ارسال شد.",
                ["InventoryFullDrop"] = "فضای کافی نبود؛ برخی آیتم‌ها کنار شما انداخته شد.",
                ["DailyReady"] = "کیت روزانه آماده دریافت است!",
                ["WeeklyReady"] = "کیت هفتگی آماده دریافت است!",

                // UI texts
                ["UI_Title"] = "لیست کیت‌ها",
                ["UI_Close"] = "بستن",
                ["UI_Preview"] = "پیش‌نمایش",
                ["UI_Claim"] = "دریافت",
                ["UI_Filter_All"] = "همه",
                ["UI_Filter_Starter"] = "استارتر",
                ["UI_Filter_VIP"] = "وی‌آی‌پی",
                ["UI_Filter_Team"] = "تیمی",

                ["StatsHeader"] = "آمار شما:",
                ["StatsLine"] = "کیت {0}: {1} بار | آخرین بار: {2}",
                ["AdminOnly"] = "فقط ادمین.",
                ["AddedFromInventory"] = "کیت «{0}» از روی موجودی فعلی ساخته شد.",
                ["RemovedKit"] = "کیت «{0}» حذف شد.",
                ["SetField"] = "فیلد {0} برای کیت «{1}» تنظیم شد.",
                ["GivenKit"] = "کیت «{0}» به بازیکن {1} داده شد.",
                ["UnknownField"] = "فیلد نامعتبر.",
                ["ReloadToSeeUI"] = "پلاگین ری‌لود شد.",

                // Team unlock notices
                ["Team3KitUnlocked"] = "🎯 تیم شما به ۳ عضو رسید! کیت تیمی جدید فعال شد.",
                ["Team4KitUnlocked"] = "🔥 تیم شما به ۴ عضو رسید! یک کیت ویژه دیگر فعال شد.",
                ["TeamKitsRemoved"]   = "⚠️ تعداد تیم بیش از ۵ عضو شد. کیت‌های تیمی حذف شدند.",

                // Team lock labels
                ["TeamReq3"] = "نیازمند تیم ۳+",
                ["TeamReq4"] = "نیازمند تیم ۴+"
            }, this, "fa");

            // English (fallback)
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission.",
                ["NoUsePermission"] = "You need permission to use kits.",
                ["KitNotFound"] = "Kit not found.",
                ["KitNoPermission"] = "You don't have permission for this kit.",
                ["KitAuthDenied"] = "This kit is admin-only.",
                ["Cooldown"] = "Cooldown remaining: {0}",
                ["MaxUses"] = "Maximum uses reached.",
                ["OneTime"] = "This kit is one-time and already claimed.",
                ["NotInWindow"] = "This kit is not active right now.",
                ["NeedLevel"] = "You need at least level {0}.",
                ["NeedMoney"] = "You need {0} money.",
                ["NeedRP"] = "You need {0} RP.",
                ["ClaimSuccess"] = "Kit '{0}' claimed.",
                ["ClaimTeamAlso"] = "Also sent to your online team.",
                ["InventoryFullDrop"] = "No space; some items were dropped near you.",
                ["DailyReady"] = "Daily kit is ready!",
                ["WeeklyReady"] = "Weekly kit is ready!",

                ["UI_Title"] = "Kits List",
                ["UI_Close"] = "Close",
                ["UI_Preview"] = "Preview",
                ["UI_Claim"] = "Claim",
                ["UI_Filter_All"] = "All",
                ["UI_Filter_Starter"] = "Starter",
                ["UI_Filter_VIP"] = "VIP",
                ["UI_Filter_Team"] = "Team",

                ["StatsHeader"] = "Your stats:",
                ["StatsLine"] = "Kit {0}: {1} uses | Last: {2}",
                ["AdminOnly"] = "Admin only.",
                ["AddedFromInventory"] = "Kit '{0}' created from current inventory.",
                ["RemovedKit"] = "Kit '{0}' removed.",
                ["SetField"] = "Field {0} set for kit '{1}'.",
                ["GivenKit"] = "Kit '{0}' given to {1}.",
                ["UnknownField"] = "Unknown field.",
                ["ReloadToSeeUI"] = "Plugin reloaded.",

                ["Team3KitUnlocked"] = "Team reached 3 members. New team kit unlocked.",
                ["Team4KitUnlocked"] = "Team reached 4 members. Another team kit unlocked.",
                ["TeamKitsRemoved"]   = "Team size > 5. Team kits removed.",
                ["TeamReq3"] = "Requires team 3+",
                ["TeamReq4"] = "Requires team 4+"
            }, this);
        }

        private DynamicConfigFile _dataKits;
        private DynamicConfigFile _dataPlayers;

        private string DataPath(string name) => $"{Name}/{name}.json";

        private void SaveKits() => _dataKits.WriteObject(_kits);
        private void SavePlayers() => _dataPlayers.WriteObject(_db);

        private void LoadData()
        {
            _dataKits = Interface.Oxide.DataFileSystem.GetFile(DataPath("kits"));
            _dataPlayers = Interface.Oxide.DataFileSystem.GetFile(DataPath("players"));

            try { _kits = _dataKits.ReadObject<KitsData>(); }
            catch { _kits = new KitsData(); }

            try { _db = _dataPlayers.ReadObject<StoredData>(); }
            catch { _db = new StoredData(); }

            var newWipeId = GetWipeId();
            if (string.IsNullOrEmpty(_db.WipeId))
                _db.WipeId = newWipeId;
            else if (_db.WipeId != newWipeId)
            {
                foreach (var pd in _db.Players.Values)
                {
                    foreach (var it in pd.Kits.Values)
                    {
                        it.LastWipeId = null;
                        if (_config.Streaks.Enable)
                        {
                            pd.StreakDays = 0;
                            pd.LastDaily = null;
                        }
                    }
                }
                _db.WipeId = newWipeId;
                SavePlayers();
            }
        }

        private string GetWipeId()
        {
            var s = ConVar.Server.seed.ToString();
            var map = ConVar.Server.level;
            var size = ConVar.Server.worldsize.ToString();
            var save = SaveRestore.SaveCreatedTime.ToUniversalTime().ToString("yyyyMMdd");
            return $"{map}-{size}-{s}-{save}";
        }

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);
            permission.RegisterPermission(PERM_ADMIN, this);
            LoadConfig();
            LoadData();

            foreach (var k in _kits.Kits.Keys)
                permission.RegisterPermission(PermForKit(k), this);
        }

        private void OnServerInitialized()
        {
            // Example kits
            EnsureKitExists("starter", new List<ItemEntry> {
                new ItemEntry{ ShortName="stone.pickaxe", Amount=1, Container="belt" },
                new ItemEntry{ ShortName="stonehatchet", Amount=1, Container="belt" },
                new ItemEntry{ ShortName="bandage", Amount=3, Container="main" }
            }, "استارتر", "شروع سریع با ابزار پایه", "Starter");

            // Team threshold kits (empty by default—admin can fill later)
            EnsureKitExists(_config.TeamUnlock.KitAt3, new List<ItemEntry>(), "کیت تیم ۳+", "برای تیم‌های ۳ تا ۵ نفر", "Team");
            EnsureKitExists(_config.TeamUnlock.KitAt4, new List<ItemEntry>(), "کیت تیم ۴+", "برای تیم‌های ۴ تا ۵ نفر", "Team");
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList)
                DestroyUI(p);
            SaveKits();
            SavePlayers();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            EnsurePlayer(player.userID);
            EvaluateTeamThreshold(player); // notify on connect
            if (_config.UI.EnableChatHints)
                HintDailyWeekly(player);
        }

        private void OnPlayerInit(BasePlayer player)
        {
            EnsurePlayer(player.userID);
            TryAutoKits(player, _config.AutoKits.OnFirstConnect);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            EvaluateTeamThreshold(player); // update cache on leave
        }

        // These hooks may or may not exist; harmless if not called by Rust.
        private void OnTeamAcceptInvite(RelationshipManager.PlayerTeam team, BasePlayer player) => EvaluateTeamThreshold(player);
        private void OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player) => EvaluateTeamThreshold(player);
        private void OnTeamKick(RelationshipManager.PlayerTeam team, BasePlayer player, ulong target) => EvaluateTeamThreshold(player);
        private void OnTeamDisbanded(RelationshipManager.PlayerTeam team) { /* nothing */ }

        private void OnPlayerRespawned(BasePlayer player)
        {
            TryAutoKits(player, _config.AutoKits.OnRespawn);
        }

        #endregion

        #region Commands

        [ChatCommand("kit")]
        private void CmdKit(BasePlayer player, string cmd, string[] args)
        {
            if (!HasUsePerm(player))
            {
                Reply(player, "NoUsePermission");
                return;
            }

            if (args != null && args.Length > 0)
            {
                var sub = args[0].ToLower();
                if (sub == "claim" && args.Length >= 2)
                {
                    ClaimKit(player, args[1]);
                    return;
                }
                if (sub == "preview" && args.Length >= 2)
                {
                    OpenUI(player, filter: args[1], previewOnly: true);
                    return;
                }
                if (sub == "stats")
                {
                    ShowStats(player);
                    return;
                }
            }

            OpenUI(player);
        }

        [ChatCommand("kit.admin")]
        private void CmdKitAdmin(BasePlayer player, string cmd, string[] args)
        {
            if (!IsAdmin(player))
            {
                Reply(player, "AdminOnly");
                return;
            }
            if (args == null || args.Length == 0)
            {
                SendReply(player, "/kit.admin add <name> | set <kit> <field> <value> | remove <name> | list | give <player> <kit> | ui");
                return;
            }

            var sub = args[0].ToLower();
            if (sub == "list")
            {
                var list = string.Join(", ", _kits.Kits.Keys.OrderBy(x => x));
                SendReply(player, $"Kits: {list}");
                return;
            }
            if (sub == "ui")
            {
                OpenUI(player);
                Reply(player, "ReloadToSeeUI");
                return;
            }
            if (sub == "remove" && args.Length >= 2)
            {
                var name = args[1];
                if (_kits.Kits.Remove(name))
                {
                    SaveKits();
                    Reply(player, "RemovedKit", name);
                }
                else Reply(player, "KitNotFound");
                return;
            }
            if (sub == "give" && args.Length >= 3)
            {
                var targetName = args[1];
                var kit = args[2];
                var target = FindPlayerByName(targetName);
                if (target == null) { SendReply(player, "Player not found"); return; }
                if (!_kits.Kits.ContainsKey(kit)) { Reply(player, "KitNotFound"); return; }
                GiveKitToPlayer(target, _kits.Kits[kit], byAdmin:true);
                Reply(player, "GivenKit", kit, target.displayName);
                return;
            }
            if (sub == "add" && args.Length >= 2)
            {
                var name = args[1].ToLower();
                var kit = BuildKitFromInventory(player, name);
                _kits.Kits[name] = kit;
                SaveKits();
                permission.RegisterPermission(PermForKit(name), this);
                Reply(player, "AddedFromInventory", name);
                return;
            }
            if (sub == "set" && args.Length >= 4)
            {
                var kitName = args[1];
                if (!_kits.Kits.TryGetValue(kitName, out var kit))
                {
                    Reply(player, "KitNotFound");
                    return;
                }
                var field = args[2].ToLower();
                var value = string.Join(" ", args.Skip(3).ToArray());

                bool ok = SetField(kit, field, value);
                if (!ok) { Reply(player, "UnknownField"); return; }
                _kits.Kits[kitName] = kit;
                SaveKits();
                Reply(player, "SetField", field, kitName);
                return;
            }

            SendReply(player, "Unknown admin usage.");
        }

        #endregion

        #region Admin Utils

        private bool SetField(KitDef kit, string field, string value)
        {
            switch (field)
            {
                case "display":
                case "displayname":
                    kit.DisplayName = value; return true;
                case "description":
                    kit.Description = value; return true;
                case "permission":
                    kit.Permission = string.IsNullOrWhiteSpace(value) ? null : value; return true;
                case "authlevel":
                    if (int.TryParse(value, out var al)) { kit.AuthLevel = Mathf.Clamp(al, 0, 2); return true; }
                    return false;
                case "cooldown":
                    kit.Cooldown = value; return true;
                case "maxuses":
                    if (int.TryParse(value, out var mu)) { kit.MaxUses = Math.Max(0, mu); return true; }
                    return false;
                case "onetime":
                    kit.OneTime = ToBool(value); return true;
                case "resetonwipe":
                    kit.ResetOnWipe = ToBool(value); return true;
                case "daily":
                    kit.Daily = ToBool(value); return true;
                case "weekly":
                    kit.Weekly = ToBool(value); return true;
                case "randomize":
                    kit.Randomize = ToBool(value); return true;
                case "rolls":
                    if (int.TryParse(value, out var r)) { kit.Rolls = Math.Max(0, r); return true; }
                    return false;
                case "teamshared":
                    kit.TeamShared = ToBool(value); return true;
                case "minlevel":
                    if (int.TryParse(value, out var lv)) { kit.MinLevel = Math.Max(0, lv); return true; }
                    return false;
                case "category":
                    kit.Category = value; return true;
                case "cost.money":
                    if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var m))
                    { kit.Cost.Money = Math.Max(0, m); return true; }
                    return false;
                case "cost.rp":
                    if (int.TryParse(value, out var rp)) { kit.Cost.RP = Math.Max(0, rp); return true; }
                    return false;
                case "window.from":
                    kit.Window.FromISO = value; return true;
                case "window.to":
                    kit.Window.ToISO = value; return true;
                case "window.days":
                    kit.Window.Days = ParseDays(value); return true;
                default:
                    return false;
            }
        }

        private List<DayOfWeek> ParseDays(string csv)
        {
            var list = new List<DayOfWeek>();
            foreach (var token in csv.Split(','))
            {
                if (Enum.TryParse(token.Trim(), true, out DayOfWeek d))
                    list.Add(d);
            }
            return list;
        }

        private bool ToBool(string s)
        {
            return s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   s.Equals("1") || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private KitDef BuildKitFromInventory(BasePlayer player, string name)
        {
            var kit = new KitDef
            {
                Name = name.ToLower(),
                DisplayName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name),
                Description = "کیت ساخته شده از موجودی فعلی",
                Cooldown = "0",
                Category = "Custom"
            };

            void addList(ItemContainer cont, string container)
            {
                foreach (var it in cont.itemList)
                {
                    var entry = new ItemEntry
                    {
                        ShortName = it.info.shortname,
                        Amount = it.amount,
                        Skin = (int)it.skin,
                        Container = container
                    };
                    kit.Items.Add(entry);
                }
            }

            addList(player.inventory.containerMain, "main");
            addList(player.inventory.containerBelt, "belt");
            addList(player.inventory.containerWear, "wear");

            return kit;
        }

        private void EnsureKitExists(string name, List<ItemEntry> items, string display, string desc, string cat)
        {
            if (_kits.Kits.ContainsKey(name)) return;
            _kits.Kits[name] = new KitDef
            {
                Name = name.ToLower(),
                DisplayName = display,
                Description = desc,
                Items = items ?? new List<ItemEntry>(),
                Category = cat,
                Cooldown = "0"
            };
            SaveKits();
            permission.RegisterPermission(PermForKit(name), this);
        }

        #endregion

        #region Core Claim Logic

        private bool HasUsePerm(BasePlayer p) => permission.UserHasPermission(p.UserIDString, PERM_USE) || IsAdmin(p);
        private bool IsAdmin(BasePlayer p) => permission.UserHasPermission(p.UserIDString, PERM_ADMIN) || (_config.AllowAuthLevelAdmin && p.IsAdmin);

        private PlayerData EnsurePlayer(ulong id)
        {
            if (!_db.Players.TryGetValue(id, out var pd))
            {
                pd = new PlayerData();
                _db.Players[id] = pd;
                SavePlayers();
            }
            return pd;
        }

        private void ClaimKit(BasePlayer player, string kitName)
        {
            if (!_kits.Kits.TryGetValue(kitName, out var kit))
            {
                Reply(player, "KitNotFound");
                return;
            }

            if (!HasUsePerm(player))
            {
                Reply(player, "NoUsePermission");
                return;
            }

            if (!string.IsNullOrEmpty(kit.Permission) && !permission.UserHasPermission(player.UserIDString, kit.Permission) &&
                !permission.UserHasPermission(player.UserIDString, PermForKit(kit.Name)) && !IsAdmin(player))
            {
                Reply(player, "KitNoPermission");
                return;
            }

            if (kit.AuthLevel > 0 && !IsAdmin(player))
            {
                Reply(player, "KitAuthDenied");
                return;
            }

            // Team threshold gating: team kits available only if size in [3..5] / 4..5
            if (_config.TeamUnlock.Enable)
            {
                int teamSize = GetTeamSize(player);
                if (kit.Name.Equals(TU.KitAt3, StringComparison.OrdinalIgnoreCase))
                {
                    if (teamSize < 3 || teamSize > TU.RemoveAbove) { Reply(player, "TeamReq3"); return; }
                }
                if (kit.Name.Equals(TU.KitAt4, StringComparison.OrdinalIgnoreCase))
                {
                    if (teamSize < 4 || teamSize > TU.RemoveAbove) { Reply(player, "TeamReq4"); return; }
                }
            }

            if (!kit.Window.IsActiveNow(DateTime.UtcNow))
            {
                Reply(player, "NotInWindow");
                return;
            }

            var pd = EnsurePlayer(player.userID);
            if (!pd.Kits.TryGetValue(kit.Name, out var usage))
            {
                usage = new KitUsage();
                pd.Kits[kit.Name] = usage;
            }

            if (kit.OneTime)
            {
                if (!string.IsNullOrEmpty(usage.LastWipeId))
                {
                    Reply(player, "OneTime");
                    return;
                }
            }

            if (kit.Daily && usage.LastClaim.HasValue && (DateTime.UtcNow - usage.LastClaim.Value).TotalHours < 24)
            {
                var remain = TimeSpan.FromHours(24) - (DateTime.UtcNow - usage.LastClaim.Value);
                Reply(player, "Cooldown", FormatTime(remain));
                return;
            }
            if (kit.Weekly && usage.LastClaim.HasValue && (DateTime.UtcNow - usage.LastClaim.Value).TotalDays < 7)
            {
                var remain = TimeSpan.FromDays(7) - (DateTime.UtcNow - usage.LastClaim.Value);
                Reply(player, "Cooldown", FormatTime(remain));
                return;
            }

            var cd = ParseDuration(kit.Cooldown);
            if (cd.TotalSeconds > 0 && usage.LastClaim.HasValue)
            {
                var passed = DateTime.UtcNow - usage.LastClaim.Value;
                if (passed < cd)
                {
                    Reply(player, "Cooldown", FormatTime(cd - passed));
                    return;
                }
            }

            if (kit.MaxUses > 0 && usage.Uses >= kit.MaxUses)
            {
                Reply(player, "MaxUses");
                return;
            }

            if (kit.MinLevel > 0 && LevelSystem != null)
            {
                var lvl = GetPlayerLevel(player.userID);
                if (lvl < kit.MinLevel)
                {
                    Reply(player, "NeedLevel", kit.MinLevel.ToString());
                    return;
                }
            }

            if (!_config.UseEconomics) kit.Cost.Money = 0;
            if (!_config.UseServerRewards) kit.Cost.RP = 0;

            if (kit.Cost.Money > 0 && Economics != null)
            {
                var balObj = Economics.Call("Balance", player.userID);
                var bal = 0.0;
                if (balObj is double d) bal = d;
                else if (balObj is long l) bal = l;
                else if (balObj is int i) bal = i;
                if (bal < kit.Cost.Money)
                {
                    Reply(player, "NeedMoney", string.Format(_config.CurrencyFormat, kit.Cost.Money));
                    return;
                }
            }
            if (kit.Cost.RP > 0 && ServerRewards != null)
            {
                var rpObj = ServerRewards.Call("CheckPoints", player.userID);
                var rp = 0;
                if (rpObj is int irp) rp = irp;
                else if (rpObj is long lrp) rp = (int)lrp;
                if (rp < kit.Cost.RP)
                {
                    Reply(player, "NeedRP", kit.Cost.RP.ToString());
                    return;
                }
            }

            if (kit.Cost.Money > 0 && Economics != null)
                Economics.Call("Withdraw", player.userID, kit.Cost.Money);
            if (kit.Cost.RP > 0 && ServerRewards != null)
                ServerRewards.Call("TakePoints", player.userID, kit.Cost.RP);

            GiveKitToPlayer(player, kit);

            usage.Uses++;
            usage.LastClaim = DateTime.UtcNow;
            if (kit.ResetOnWipe) usage.LastWipeId = _db.WipeId;

            if (_config.Streaks.Enable && kit.Daily && string.Equals(kit.Name, _config.Streaks.DailyKitName, StringComparison.OrdinalIgnoreCase))
            {
                if (pd.LastDaily.HasValue)
                {
                    var deltaDays = (DateTime.UtcNow.Date - pd.LastDaily.Value.Date).TotalDays;
                    if (deltaDays <= 1) pd.StreakDays++;
                    else pd.StreakDays = 1;
                }
                else pd.StreakDays = 1;

                pd.LastDaily = DateTime.UtcNow;

                foreach (var kv in _config.StreakRewards.OrderBy(k => k.Value))
                {
                    if (pd.StreakDays == kv.Value && _kits.Kits.TryGetValue(kv.Key, out var rewardKit))
                    {
                        GiveKitToPlayer(player, rewardKit, byAdmin: true);
                    }
                }
            }

            SavePlayers();

            Reply(player, "ClaimSuccess", kit.DisplayName ?? kit.Name);

            if (kit.TeamShared && player.currentTeam != 0)
            {
                var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                if (team != null)
                {
                    foreach (var memberId in team.members)
                    {
                        if (memberId == player.userID) continue;
                        var teammate = BasePlayer.FindByID(memberId);
                        if (teammate != null && teammate.IsConnected)
                        {
                            GiveKitToPlayer(teammate, kit, byAdmin: true);
                        }
                    }
                    Reply(player, "ClaimTeamAlso");
                }
            }

            OpenUI(player); // refresh
        }

        private void GiveKitToPlayer(BasePlayer player, KitDef kit, bool byAdmin = false)
        {
            var giveItems = new List<ItemEntry>();
            if (kit.Randomize && kit.Rolls > 0)
            {
                var rnd = Pool.Get<System.Random>();
                try
                {
                    var poolList = kit.Items.ToList();
                    for (int i = 0; i < kit.Rolls && poolList.Count > 0; i++)
                    {
                        var pick = poolList[rnd.Next(poolList.Count)];
                        giveItems.Add(pick);
                    }
                }
                finally { Pool.Free(ref rnd); }
            }
            else giveItems = kit.Items.ToList();

            var dropped = false;

            foreach (var e in giveItems)
            {
                var def = ItemManager.FindItemDefinition(e.ShortName);
                if (def == null) continue;
                var amountLeft = e.Amount;
                while (amountLeft > 0)
                {
                    var stack = Math.Min(amountLeft, def.stackable);
                    var item = ItemManager.Create(def, stack, (ulong)e.Skin);
                    var container = ChooseContainer(player, e.Container);
                    var ok = item.MoveToContainer(container, item.position);
                    if (!ok)
                    {
                        dropped = true;
                        item.Drop(player.eyes.position, player.GetDropVelocity());
                    }
                    amountLeft -= stack;
                }
            }

            if (dropped) Reply(player, "InventoryFullDrop");
        }

        private ItemContainer ChooseContainer(BasePlayer player, string container)
        {
            switch ((container ?? "main").ToLower())
            {
                case "belt": return player.inventory.containerBelt;
                case "wear": return player.inventory.containerWear;
                default: return player.inventory.containerMain;
            }
        }

        private int GetPlayerLevel(ulong id)
        {
            if (LevelSystem == null) return 0;
            try
            {
                var obj = LevelSystem.Call("GetPlayerLevel", id);
                if (obj is int i) return i;
                if (obj is long l) return (int)l;
                if (obj is double d) return (int)d;
            }
            catch { }
            return 0;
        }

        private void TryAutoKits(BasePlayer player, List<string> which)
        {
            if (which == null || which.Count == 0) return;

            var sorted = which
                .Where(k => _kits.Kits.ContainsKey(k))
                .OrderByDescending(k => _config.AutoKits.Priority.ContainsKey(k) ? _config.AutoKits.Priority[k] : 0);

            foreach (var k in sorted)
            {
                var kit = _kits.Kits[k];
                if (CanClaimSilently(player, kit, out _))
                {
                    ClaimKit(player, k);
                    break; // one best kit
                }
            }
        }

        private bool CanClaimSilently(BasePlayer player, KitDef kit, out string reasonKey)
        {
            reasonKey = null;

            if (_config.TeamUnlock.Enable)
            {
                int teamSize = GetTeamSize(player);
                if (kit.Name.Equals(TU.KitAt3, StringComparison.OrdinalIgnoreCase))
                {
                    if (teamSize < 3 || teamSize > TU.RemoveAbove) { reasonKey = "TeamReq3"; return false; }
                }
                if (kit.Name.Equals(TU.KitAt4, StringComparison.OrdinalIgnoreCase))
                {
                    if (teamSize < 4 || teamSize > TU.RemoveAbove) { reasonKey = "TeamReq4"; return false; }
                }
            }

            if (!string.IsNullOrEmpty(kit.Permission) && !permission.UserHasPermission(player.UserIDString, kit.Permission) &&
                !permission.UserHasPermission(player.UserIDString, PermForKit(kit.Name)) && !IsAdmin(player))
            { reasonKey = "KitNoPermission"; return false; }

            if (!kit.Window.IsActiveNow(DateTime.UtcNow)) { reasonKey = "NotInWindow"; return false; }

            var pd = EnsurePlayer(player.userID);
            pd.Kits.TryGetValue(kit.Name, out var usage);

            if (kit.OneTime && usage != null && !string.IsNullOrEmpty(usage.LastWipeId)) { reasonKey = "OneTime"; return false; }

            if (usage != null)
            {
                if (kit.Daily && usage.LastClaim.HasValue && (DateTime.UtcNow - usage.LastClaim.Value).TotalHours < 24) { reasonKey = "Cooldown"; return false; }
                if (kit.Weekly && usage.LastClaim.HasValue && (DateTime.UtcNow - usage.LastClaim.Value).TotalDays < 7) { reasonKey = "Cooldown"; return false; }
                var cd = ParseDuration(kit.Cooldown);
                if (cd.TotalSeconds > 0 && usage.LastClaim.HasValue && (DateTime.UtcNow - usage.LastClaim.Value) < cd) { reasonKey = "Cooldown"; return false; }
                if (kit.MaxUses > 0 && usage.Uses >= kit.MaxUses) { reasonKey = "MaxUses"; return false; }
            }

            return true;
        }

        #endregion

        #region Team Threshold Logic

        private int GetTeamSize(BasePlayer player)
        {
            if (player == null || player.currentTeam == 0) return 1;
            var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
            if (team == null || team.members == null) return 1;
            return team.members.Count;
        }

        private void EvaluateTeamThreshold(BasePlayer player)
        {
            if (!_config.TeamUnlock.Enable || player == null) return;
            if (player.currentTeam == 0) return;

            var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
            if (team == null) return;

            int size = team.members?.Count ?? 1;
            _db.TeamLastKnownSize.TryGetValue(team.teamID, out int last);

            if (size != last)
            {
                // Notify members about changes
                if (_config.TeamUnlock.Notify)
                {
                    if (size == 3) BroadcastTeam(team, "Team3KitUnlocked");
                    else if (size == 4) BroadcastTeam(team, "Team4KitUnlocked");
                    else if (size > _config.TeamUnlock.RemoveAbove) BroadcastTeam(team, "TeamKitsRemoved");
                }
                _db.TeamLastKnownSize[team.teamID] = size;
                SavePlayers();
            }
        }

        private void BroadcastTeam(RelationshipManager.PlayerTeam team, string key)
        {
            foreach (var id in team.members)
            {
                var p = BasePlayer.FindByID(id);
                if (p != null && p.IsConnected)
                    Reply(p, key);
            }
        }

        private bool IsTeamKitVisible(KitDef k, int teamSize)
        {
            if (!_config.TeamUnlock.Enable) return true;
            if (k.Name.Equals(TU.KitAt3, StringComparison.OrdinalIgnoreCase))
                return teamSize >= 3 && teamSize <= TU.RemoveAbove;
            if (k.Name.Equals(TU.KitAt4, StringComparison.OrdinalIgnoreCase))
                return teamSize >= 4 && teamSize <= TU.RemoveAbove;
            return true;
        }

        private bool IsTeamKitClaimableNow(KitDef k, int teamSize, out string reasonLabelKey)
        {
            reasonLabelKey = null;
            if (!_config.TeamUnlock.Enable) return true;

            if (k.Name.Equals(TU.KitAt3, StringComparison.OrdinalIgnoreCase))
            {
                if (teamSize < 3) { reasonLabelKey = "TeamReq3"; return false; }
                if (teamSize > TU.RemoveAbove) { reasonLabelKey = "TeamKitsRemoved"; return false; }
            }
            if (k.Name.Equals(TU.KitAt4, StringComparison.OrdinalIgnoreCase))
            {
                if (teamSize < 4) { reasonLabelKey = "TeamReq4"; return false; }
                if (teamSize > TU.RemoveAbove) { reasonLabelKey = "TeamKitsRemoved"; return false; }
            }
            return true;
        }

        #endregion

        #region UI (Modern Centered / Glass)

        private const string UI_BACK = "KitsPlus.Backdrop";
        private const string UI_MAIN = "KitsPlus.Main";
        private const string UI_CONTENT = "KitsPlus.Content";

        private void OpenUI(BasePlayer player, string filter = null, bool previewOnly = false)
        {
            DestroyUI(player);

            var uiScale = Mathf.Clamp(_config.UI.UIScale, 0.8f, 1.5f);
            var w = 0.70f * uiScale; // slightly smaller window
            var h = 0.72f * uiScale;

            var bg = new CuiElementContainer();

            // Dim backdrop
            bg.Add(new CuiPanel
            {
                Image = { Color = _config.UI.Backdrop },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", UI_BACK);

            // Glass window
            bg.Add(new CuiPanel
            {
                Image = { Color = _config.UI.Glass },
                RectTransform =
                {
                    AnchorMin = $"{0.5f - w / 2f} {0.5f - h / 2f}",
                    AnchorMax = $"{0.5f + w / 2f} {0.5f + h / 2f}"
                },
                CursorEnabled = true
            }, UI_BACK, UI_MAIN);

            // Header strip
            bg.Add(new CuiPanel
            {
                Image = { Color = _config.UI.GlassTop },
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" }
            }, UI_MAIN);

            // Title
            bg.Add(new CuiLabel
            {
                Text = { Text = Lang(player, "UI_Title"), Align = TextAnchor.MiddleLeft, FontSize = 18, Color = _config.UI.Text },
                RectTransform = { AnchorMin = "0.02 0.93", AnchorMax = "0.6 0.995" }
            }, UI_MAIN);

            // Close button
            bg.Add(new CuiButton
            {
                Button = { Color = _config.UI.Accent, Command = $"kitui.close" },
                RectTransform = { AnchorMin = "0.94 0.935", AnchorMax = "0.985 0.985" },
                Text = { Text = Lang(player, "UI_Close"), Align = TextAnchor.MiddleCenter, FontSize = 14, Color = "1 1 1 1" }
            }, UI_MAIN);

            // Category Tabs (All / Starter / VIP / Team)
            var tabs = new[] {
                new { key="all", label=Lang(player,"UI_Filter_All") },
                new { key="Starter", label=Lang(player,"UI_Filter_Starter") },
                new { key="VIP", label=Lang(player,"UI_Filter_VIP") },
                new { key="Team", label=Lang(player,"UI_Filter_Team") },
            };
            float tabW = 0.18f;
            for (int t = 0; t < tabs.Length; t++)
            {
                float xMin = 0.02f + t * (tabW + 0.01f);
                float xMax = xMin + tabW;
                var tabKey = tabs[t].key;
                var cmd = $"kitui.tab {tabKey}";
                bg.Add(new CuiButton
                {
                    Button = { Color = _config.UI.AccentSoft, Command = cmd },
                    RectTransform = { AnchorMin = $"{xMin} 0.90", AnchorMax = $"{xMax} 0.93" },
                    Text = { Text = tabs[t].label, Align = TextAnchor.MiddleCenter, FontSize = 12, Color = _config.UI.Text }
                }, UI_MAIN);
            }

            // Content holder
            var content = new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = "0.98 0.885" }
            };
            bg.Add(content, UI_MAIN, UI_CONTENT);

            // Build kit cards
            var kitsList = _kits.Kits.Values.AsEnumerable();
            if (!string.IsNullOrEmpty(filter))
            {
                // if used as tab key
                if (filter.Equals("all", StringComparison.OrdinalIgnoreCase)) { /* all */ }
                else
                {
                    kitsList = kitsList.Where(k => string.Equals(k.Category, filter, StringComparison.OrdinalIgnoreCase)
                                                || k.Name.Equals(filter, StringComparison.OrdinalIgnoreCase)
                                                || (k.DisplayName ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }

            // Sort & render
            var perRow = Mathf.Clamp(_config.UI.CardsPerRow, 2, 5);
            float rowH = 0.24f;
            float cardW = 1f / perRow - 0.01f;

            int teamSize = GetTeamSize(player);
            int i = 0;
            foreach (var k in kitsList.OrderBy(k => k.Category).ThenBy(k => k.DisplayName ?? k.Name))
            {
                // Team kits shown only when visible by size
                if (!IsTeamKitVisible(k, teamSize)) continue;

                var row = i / perRow;
                var col = i % perRow;
                var yMax = 0.98f - row * (rowH + 0.01f);
                var yMin = yMax - rowH;

                if (yMin < 0) break;

                var xMin = 0.01f + col * (cardW + 0.01f);
                var xMax = xMin + cardW;

                var cardName = $"KitsPlus.Card.{k.Name}";

                // Card base
                bg.Add(new CuiPanel
                {
                    Image = { Color = _config.UI.Card },
                    RectTransform = { AnchorMin = $"{xMin} {yMin}", AnchorMax = $"{xMax} {yMax}" }
                }, UI_CONTENT, cardName);

                // Accent border (top)
                bg.Add(new CuiPanel
                {
                    Image = { Color = _config.UI.CardBorder },
                    RectTransform = { AnchorMin = "0 0.97", AnchorMax = "1 1" }
                }, cardName);

                // Title
                bg.Add(new CuiLabel
                {
                    Text = { Text = (k.DisplayName ?? k.Name), Align = TextAnchor.MiddleLeft, FontSize = 16, Color = _config.UI.Text },
                    RectTransform = { AnchorMin = "0.02 0.77", AnchorMax = "0.98 0.97" }
                }, cardName);

                // Description
                var desc = string.IsNullOrEmpty(k.Description) ? "" : Shorten(k.Description, 120);
                bg.Add(new CuiLabel
                {
                    Text = { Text = desc, Align = TextAnchor.UpperLeft, FontSize = 12, Color = _config.UI.TextDim },
                    RectTransform = { AnchorMin = "0.02 0.44", AnchorMax = "0.98 0.76" }
                }, cardName);

                // Info line
                var infoParts = new List<string>();
                var cdTs = ParseDuration(k.Cooldown);
                if (!string.IsNullOrEmpty(k.Cooldown) && cdTs.TotalSeconds > 0) infoParts.Add($"CD: {k.Cooldown}");
                if (k.Cost.Money > 0) infoParts.Add($"$ {k.Cost.Money}");
                if (k.Cost.RP > 0) infoParts.Add($"RP {k.Cost.RP}");
                if (k.Daily) infoParts.Add("روزانه");
                if (k.Weekly) infoParts.Add("هفتگی");
                if (k.TeamShared) infoParts.Add("تیمی");
                // Team lock labels
                if (k.Name.Equals(TU.KitAt3, StringComparison.OrdinalIgnoreCase)) infoParts.Add(Lang(player, "TeamReq3"));
                if (k.Name.Equals(TU.KitAt4, StringComparison.OrdinalIgnoreCase)) infoParts.Add(Lang(player, "TeamReq4"));

                var info = string.Join(" | ", infoParts);
                bg.Add(new CuiLabel
                {
                    Text = { Text = info, Align = TextAnchor.LowerLeft, FontSize = 12, Color = _config.UI.TextDim },
                    RectTransform = { AnchorMin = "0.02 0.32", AnchorMax = "0.98 0.44" }
                }, cardName);

                // Buttons
                var previewCmd = $"kitui.preview {k.Name}";

                // determine claimable state
                bool claimable = IsTeamKitClaimableNow(k, teamSize, out var teamReasonKey);

                var claimCmd = previewOnly ? "" : (claimable ? $"kitui.claim {k.Name}" : "");

                // Preview button
                bg.Add(new CuiButton
                {
                    Button = { Color = _config.UI.AccentSoft, Command = previewCmd },
                    RectTransform = { AnchorMin = "0.02 0.06", AnchorMax = "0.30 0.28" },
                    Text = { Text = Lang(player, "UI_Preview"), Align = TextAnchor.MiddleCenter, FontSize = 14, Color = "1 1 1 1" }
                }, cardName);

                // Claim button
                var claimColor = claimable ? _config.UI.Accent : "0.35 0.35 0.35 0.9";
                var claimText = Lang(player, "UI_Claim");
                if (!claimable && !string.IsNullOrEmpty(teamReasonKey))
                    claimText = Lang(player, teamReasonKey);

                if (!previewOnly)
                {
                    bg.Add(new CuiButton
                    {
                        Button = { Color = claimColor, Command = claimCmd },
                        RectTransform = { AnchorMin = "0.32 0.06", AnchorMax = "0.98 0.28" },
                        Text = { Text = claimText, Align = TextAnchor.MiddleCenter, FontSize = 13, Color = "1 1 1 1" }
                    }, cardName);
                }

                // Cooldown progress bar (if enabled)
                if (_config.UI.ShowCooldownBars)
                {
                    var pd = EnsurePlayer(player.userID);
                    pd.Kits.TryGetValue(k.Name, out var usage);
                    double frac = 0.0;
                    if (usage != null && usage.LastClaim.HasValue && cdTs.TotalSeconds > 0)
                    {
                        var elapsed = (DateTime.UtcNow - usage.LastClaim.Value).TotalSeconds;
                        frac = Math.Min(1.0, Math.Max(0.0, elapsed / cdTs.TotalSeconds));
                    }
                    else frac = 1.0; // ready

                    // background bar
                    bg.Add(new CuiPanel
                    {
                        Image = { Color = "0 0 0 0.35" },
                        RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = "0.98 0.055" }
                    }, cardName);

                    // filled bar
                    bg.Add(new CuiPanel
                    {
                        Image = { Color = _config.UI.Accent },
                        RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = $"{0.02 + 0.96f * (float)frac} 0.055" }
                    }, cardName);
                }

                i++;
            }

            CuiHelper.AddUi(player, bg);
        }

        private void DestroyUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_BACK);
            CuiHelper.DestroyUi(player, UI_MAIN);
        }

        [ConsoleCommand("kitui.close")]
        private void CCUIClose(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            DestroyUI(p);
        }

        [ConsoleCommand("kitui.claim")]
        private void CCUIClaim(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null || arg.Args == null || arg.Args.Length < 1) return;
            ClaimKit(p, arg.Args[0]);
        }

        [ConsoleCommand("kitui.preview")]
        private void CCUIPreview(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null || arg.Args == null || arg.Args.Length < 1) return;
            OpenUI(p, filter: arg.Args[0], previewOnly: true);
        }

        [ConsoleCommand("kitui.tab")]
        private void CCUITab(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null || arg.Args == null || arg.Args.Length < 1) return;
            var key = arg.Args[0];
            OpenUI(p, filter: key);
        }

        private string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        #endregion

        #region Utilities

        private BasePlayer FindPlayerByName(string name)
        {
            name = name.ToLower();
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p.displayName != null && p.displayName.ToLower().Contains(name))
                    return p;
            }
            return null;
        }

        private void ShowStats(BasePlayer player)
        {
            var pd = EnsurePlayer(player.userID);
            var lines = new List<string> { Lang(player, "StatsHeader") };
            foreach (var kv in pd.Kits)
            {
                var last = kv.Value.LastClaim.HasValue ? kv.Value.LastClaim.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "-";
                lines.Add(string.Format(Lang(player, "StatsLine"), kv.Key, kv.Value.Uses, last));
            }
            SendReply(player, string.Join("\n", lines));
        }

        private void HintDailyWeekly(BasePlayer player)
        {
            if (!_config.UI.EnableChatHints) return;

            foreach (var k in _kits.Kits.Values)
            {
                if (k.Daily) { SendReply(player, Lang(player, "DailyReady")); break; }
            }
            foreach (var k in _kits.Kits.Values)
            {
                if (k.Weekly) { SendReply(player, Lang(player, "WeeklyReady")); break; }
            }
        }

        private void Reply(BasePlayer player, string key, params object[] args)
        {
            SendReply(player, Lang(player, key, args));
        }

        private string Lang(BasePlayer p, string key, params object[] args)
        {
            var userId = p?.UserIDString ?? "0";
            var msg = lang.GetMessage(key, this, userId);
            if (args != null && args.Length > 0) msg = string.Format(msg, args);
            return msg;
        }

        private TimeSpan ParseDuration(string s)
        {
            if (string.IsNullOrEmpty(s)) return TimeSpan.Zero;
            s = s.Trim().ToLowerInvariant();

            try
            {
                double num;
                if (s.EndsWith("ms") && double.TryParse(s.Substring(0, s.Length - 2), NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                    return TimeSpan.FromMilliseconds(num);
                if (s.EndsWith("s") && double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                    return TimeSpan.FromSeconds(num);
                if (s.EndsWith("m") && double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                    return TimeSpan.FromMinutes(num);
                if (s.EndsWith("h") && double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                    return TimeSpan.FromHours(num);
                if (s.EndsWith("d") && double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                    return TimeSpan.FromDays(num);

                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                    return TimeSpan.FromSeconds(num);
            }
            catch { }
            return TimeSpan.Zero;
        }

        private string FormatTime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
            return $"{(int)t.TotalSeconds}s";
        }

        #endregion
    }
}
