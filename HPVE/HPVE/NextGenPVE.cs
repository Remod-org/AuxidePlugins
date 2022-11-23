using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Mono.Data.Sqlite;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using System.Timers;
using Auxide;

public class NextGenPVE : RustScript
{
    #region vars
    // Ruleset to multiple zones
    private Dictionary<string, NextGenPVEZoneMap> ngpvezonemaps = new Dictionary<string, NextGenPVEZoneMap>();
    private Dictionary<int, NGPVE_Schedule> ngpveschedule = new Dictionary<int, NGPVE_Schedule>();
    private Dictionary<string, long> lastConnected = new Dictionary<string, long>();

    private const string permNextGenPVEUse = "nextgenpve.use";
    private const string permNextGenPVEAdmin = "nextgenpve.admin";
    private const string permNextGenPVEGod = "nextgenpve.god";
    private ConfigData configData;
    private SqliteConnection sqlConnection;
    private string connStr;

    private List<ulong> isopen = new List<ulong>();

    private readonly string BannerColor = "Grey";
    private string TextColor = "Red";

    private static NextGenPVE Instance;
    private readonly string logfilename = "log";
    private bool dolog;
    private bool enabled = true;
    private bool inPurge;
    private bool purgeLast;
    private Timer scheduleTimer;
    private const string NGPVERULELIST = "nextgenpve.rulelist";
    private const string NGPVEEDITRULESET = "nextgenpve.ruleseteditor";
    private const string NGPVERULEEDIT = "nextgenpve.ruleeditor";
    private const string NGPVEENTEDIT = "nextgenpve.enteditor";
    private const string NGPVEVALUEEDIT = "nextgenpve.value";
    private const string NGPVESCHEDULEEDIT = "nextgenpve.schedule";
    private const string NGPVEENTSELECT = "nextgenpve.selectent";
    private const string NGPVENPCSELECT = "nextgenpve.selectnpc";
    private const string NGPVERULESELECT = "nextgenpve.selectrule";
    private const string NGPVERULEEXCLUSIONS = "nextgenpve.exclusions";
    private const string NGPVECUSTOMSELECT = "nextgenpve.customsel";
    #endregion

    #region Message
    public string Lang(string input, params object[] args)
    {
        return string.Format(lang.Get(input), args);
    }

    public void Message(BasePlayer player, string input, params object[] args)
    {
        Utils.SendReply(player, string.Format(lang.Get(input), args));
    }

    private void LMessage(BasePlayer player, string key, params object[] args) => Utils.SendReply(player, Lang(key, args));

    #endregion

    #region init
    private void OnScriptLoaded()
    {
        Instance = this;
        //Utils.DoLog("Creating database connection for main thread.");

        connStr = $"Data Source={data.Directory}{Path.DirectorySeparatorChar}{Name}{Path.DirectorySeparatorChar}nextgenpve.db;";
        //AddCovalenceCommand("pveupdate", "CmdUpdateEnts");
        //AddCovalenceCommand("pvebackup", "CmdNextGenPVEbackup");
        //AddCovalenceCommand("pverule", "CmdNextGenPVEGUI");
        //AddCovalenceCommand("pveenable", "CmdNextGenPVEenable");
        //AddCovalenceCommand("pvedrop", "CmdNextGenPVEDrop");
        //AddCovalenceCommand("pvelog", "CmdNextGenPVElog");
        //AddCovalenceCommand("pvedebug", "CmdNextGenPVEDebug");

        Permissions.RegisterPermission(Name, permNextGenPVEUse);
        Permissions.RegisterPermission(Name, permNextGenPVEAdmin);
        Permissions.RegisterPermission(Name, permNextGenPVEGod);
        enabled = true;
    }

    private void OnServerInitialized()
    {
        ConsoleSystem.Run(ConsoleSystem.Option.Server.FromServer(), "server.pve 0");
        //Utils.DoLog("Opening...");
        sqlConnection = new SqliteConnection(connStr);
        sqlConnection.Open();

        LoadConfigVariables();
        LoadData();

        //if (configData.Options.useSchedule) RunSchedule(true);
    }

    private void OnUserConnected(BasePlayer player) => OnUserDisconnected(player);

    private void OnUserDisconnected(BasePlayer player)
    {
        //if (configData.Options.usePlayerDatabase) return;
        long lc;
        lastConnected.TryGetValue(player.UserIDString, out lc);
        if (lc > 0)
        {
            lastConnected[player.UserIDString] = ToEpochTime(DateTime.UtcNow);
        }
        else
        {
            lastConnected.Add(player.UserIDString, ToEpochTime(DateTime.UtcNow));
        }
        SaveData();
    }

    private object OnUserCommand(BasePlayer player, string command, string[] args)
    {
        if (command != "pverule" && isopen.Contains(player.userID))
        {
            if (configData.Options.debug) Utils.DoLog($"OnPlayerCommand: {command} BLOCKED");
            return true;
        }
        return null;
    }

    private object OnPlayerCommand(BasePlayer player, string command, string[] args)
    {
        if (command != "pverule" && isopen.Contains(player.userID))
        {
            if (configData.Options.debug) Utils.DoLog($"OnPlayerCommand: {command} BLOCKED");
            return true;
        }
        return null;
    }

    private void OnNewSave()
    {
        Utils.DoLog("Wipe detected.  Clearing zone maps...");
        ngpvezonemaps = new Dictionary<string, NextGenPVEZoneMap>();
        NewPurgeSchedule();
        SaveData();
        //UpdateEnts();
    }

    private void UpdateEnts()
    {
        // Populate the entities table with any new entities (Typically only at wipe but can be run manually via pveupdate.)
        // All new ents are added as unknown for manual recategorization.
        Utils.DoLog("Finding new entity types...");
        List<string> names = new List<string>();
        foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(new BaseCombatEntity().GetType()))
        {
            string objname = obj.GetType().ToString();
            if (objname.Contains("Entity")) continue;
            if (names.Contains(objname)) continue; // Saves 20-30 seconds of processing time.
            names.Add(objname);
            //Utils.DoLog($"{objname}");

            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                string query = $"INSERT INTO ngpve_entities (name, type) SELECT 'unknown', '{objname}' WHERE NOT EXISTS (SELECT * FROM ngpve_entities WHERE type='{objname}')";
                using (SqliteCommand us = new SqliteCommand(query, c))
                {
                    us.ExecuteNonQuery();
                }
            }
        }
        Utils.DoLog("Done!");
    }

    public override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            ["notauthorized"] = "You don't have permission to use this command.",
            ["zonemanagerreq"] = "ZoneManager required for handling multiple active rulesets.",
            ["nextgenpverulesets"] = "NextGenPVE Rulesets",
            ["nextgenpverulesetsf"] = "NextGenPVE Rulesets and Flags",
            ["rulesets"] = "Rulesets",
            ["pveenabled"] = "PVE Enabled for {0} ruleset.",
            ["pvedisabled"] = "PVE Disabled for {0} ruleset.",
            ["nextgenpveruleset"] = "NextGenPVE Ruleset",
            ["nextgenpverule"] = "NextGenPVE Rule",
            ["nextgenpveruleselect"] = "NextGenPVE Rule Select",
            ["nextgenpveentselect"] = "NextGenPVE Entity Select",
            ["nextgenpvenpcselect"] = "NextGenPVE NPC Turret Target Exclusions",
            ["npcselectnotes"] = "Selected (Disabled) items in orange will be excluded from targeting by turrets, etc.  Items in green will be targeted (Enabled).  Use the Global Flags as a reference.",
            ["nextgenpveentedit"] = "NextGenPVE Entity Edit",
            ["nextgenpvevalue"] = "NextGenPVE Ruleset Value",
            ["nextgenpveexclusions"] = "NextGenPVE Ruleset Exclusions",
            ["nextgenpveschedule"] = "NextGenPVE Ruleset Schedule",
            ["scheduling"] = "Schedules should be in the format of 'day(s);starttime;endtime'.  Use * for all days.  Enter 0 to clear.",
            ["current2chedule"] = "Currently scheduled for day: {0} from {1} until {2}",
            ["noschedule"] = "Not currently scheduled",
            ["flags"] = "Global Flags",
            ["defload"] = "Set Defaults",
            ["deflag"] = "Reset Flags",
            ["default"] = "default",
            ["drop"] = "RESET DATABASE",
            ["none"] = "none",
            ["close"] = "Close",
            ["clone"] = "Clone",
            ["allow"] = "Allow",
            ["block"] = "Block",
            ["save"] = "Save",
            ["edit"] = "Edit",
            ["delete"] = "Delete",
            ["new"] = "new",
            ["newcat"] = "New Collection",
            ["setcat"] = "Set Collection",
            ["clicktoedit"] = "^Click to Edit^",
            ["editname"] = "Edit Name",
            ["editnpc"] = "Edit NPC Targeting",
            ["standard"] = "Standard",
            ["automated"] = "Automated",
            ["add"] = "Add",
            ["all"] = "All",
            ["true"] = "True",
            ["false"] = "False",
            ["editing"] = "Editing",
            ["source"] = "Source",
            ["target"] = "Target",
            ["exclude"] = "Exclude from",
            ["enableset"] = "Enable set to {0}",
            ["enabled"] = "Enabled",
            ["disabled"] = "Disabled",
            ["genabled"] = "Globally Enabled",
            ["gdisabled"] = "Globally Disabled",
            ["zone"] = "Zone",
            ["purgeactive"] = "PURGE ACTIVE",
            ["purgeinactive"] = "PURGE IS OVER",
            ["sinvert"] = "Invert Schedule",
            ["sinverted"] = "Schedule Inverted",
            ["schedule"] = "Schedule",
            ["schedulep"] = "Schedule (Active period)",
            ["schedulei"] = "Schedule (Inactive period)",
            ["scheduled"] = "Schedule Disabled",
            ["editing"] = "Editing",
            ["select"] = "Select",
            ["damage"] = "Damage",
            ["stock"] = "Stock",
            ["custom"] = "Custom",
            ["customedit"] = "Rule/Ent Editor",
            ["lookup"] = "lookup",
            ["unknown"] = "Unknown",
            ["search"] = "Search",
            ["defaultdamage"] = "Default Damage",
            ["damageexceptions"] = "Damage Exceptions",
            ["sourceexcl"] = "Source Exclusions",
            ["targetexcl"] = "Target Exclusions",
            ["clicktosetcoll"] = "Click to set collection",
            ["rule"] = "Rule",
            ["rules"] = "Rules",
            ["entities"] = "Entities",
            ["uentities"] = "Unknown Entities",
            ["logging"] = "Logging set to {0}",
            ["debug"] = "Debug set to {0}",
            ["day"] = "Day",
            ["starthour"] = "Start Hour",
            ["endhour"] = "End Hour",
            ["startminute"] = "Start Minute",
            ["endminute"] = "End Minute",
            ["NPCAutoTurretTargetsPlayers"] = "NPC AutoTurret Targets Players",
            ["NPCAutoTurretTargetsNPCs"] = "NPC AutoTurret Targets NPCs",
            ["AutoTurretTargetsPlayers"] = "AutoTurret Targets Players",
            ["HeliTurretTargetsPlayers"] = "Heli Turret Targets Players",
            ["AutoTurretTargetsNPCs"] = "AutoTurret Targets NPCs",
            ["NPCSamSitesIgnorePlayers"] = "NPC SamSites Ignore Players",
            ["SamSitesIgnorePlayers"] = "SamSites Ignore Players",
            ["NPCSamSitesTargetPlayers"] = "(ext) NPC SamSites Target Players",
            ["SamSitesTargetPlayers"] = "(ext) SamSites Target Players",
            ["NPCSamSitesTargetNPCs"] = "(ext) NPC SamSites Target NPCs",
            ["SamSitesTargetNPCs"] = "(ext) SamSites Target NPCs",
            ["NPCSamSitesTargetAnimals"] = "(ext) NPC SamSites Target Animals",
            ["SamSitesTargetAnimals"] = "(ext) SamSites Target Animals",
            ["AllowSuicide"] = "Allow Player Suicide",
            ["AllowFriendlyFire"] = "Allow Friendly Fire",
            ["TrapsIgnorePlayers"] = "Traps Ignore Players",
            ["HonorBuildingPrivilege"] = "Honor Building Privilege",
            ["UnprotectedBuildingDamage"] = "Unprotected Building Damage",
            ["TwigDamage"] = "Twig Damage",
            ["HonorRelationships"] = "Honor Relationships",
            ["backup"] = "Backup Database",
            ["BackupDone"] = "NextGenPVE database has been backed up to {0}",
            ["RestoreDone"] = "NextGenPVE database has been restored from {0}",
            ["RestoreFailed"] = "Unable to restore from {0}!  Verify presence of file.",
            ["RestoreFilename"] = "Restore requires source filename ending in '.db'!",
            ["RestoreAvailable"] = "Available files:\n\t{0}"
        }, Name);
    }

    private void Unload()
    {
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            CuiHelper.DestroyUi(player, NGPVERULELIST);
            CuiHelper.DestroyUi(player, NGPVERULEEDIT);
            CuiHelper.DestroyUi(player, NGPVEVALUEEDIT);
            CuiHelper.DestroyUi(player, NGPVESCHEDULEEDIT);
            CuiHelper.DestroyUi(player, NGPVEEDITRULESET);
            CuiHelper.DestroyUi(player, NGPVERULESELECT);
            CuiHelper.DestroyUi(player, NGPVEENTSELECT);
            CuiHelper.DestroyUi(player, NGPVENPCSELECT);
            CuiHelper.DestroyUi(player, NGPVEENTEDIT);
            CuiHelper.DestroyUi(player, NGPVERULEEXCLUSIONS);
            CuiHelper.DestroyUi(player, NGPVECUSTOMSELECT);
        }

        //scheduleTimer?.Destroy();
        sqlConnection.Close();
    }

    private void LoadData()
    {
        bool found = false;
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand r = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='ngpve_entities'", c))
            {
                using (SqliteDataReader rentry = r.ExecuteReader())
                {
                    while (rentry.Read()) { found = true; }
                }
            }
        }
        if (!found) LoadDefaultEntities();

        found = false;
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand r = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='ngpve_rules'", c))
            {
                using (SqliteDataReader rentry = r.ExecuteReader())
                {
                    while (rentry.Read()) { found = true; }
                }
            }
        }
        if (!found) LoadDefaultRules();

        found = false;
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand r = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='ngpve_rulesets'", c))
            {
                using (SqliteDataReader rentry = r.ExecuteReader())
                {
                    while (rentry.Read()) { found = true; }
                }
            }
        }
        if (!found) LoadDefaultRuleset();

        found = false;
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand r = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='ngpve_targetexclusion'", c))
            {
                using (SqliteDataReader rentry = r.ExecuteReader())
                {
                    while (rentry.Read()) { found = true; }
                }
            }
        }
        if (!found) LoadNPCTgtExclDb();

        ngpvezonemaps = data.ReadObject<Dictionary<string, NextGenPVEZoneMap>>("ngpve_zonemaps");
        lastConnected = data.ReadObject<Dictionary<string, long>>("ngpve_lastconnected");
    }

    private void SaveData()
    {
        data.WriteObject("ngpve_zonemaps", ngpvezonemaps);
        data.WriteObject("ngpve_lastconnected", lastConnected);
    }
    #endregion

    #region Oxide_hooks
    private object OnTrapTrigger(BaseTrap trap, GameObject go)
    {
        if (!enabled) return null;
        BasePlayer player = go.GetComponent<BasePlayer>();
        if (trap == null || player == null) return null;

        object cantraptrigger = BroadcastReturn("CanEntityTrapTrigger", new object[] { trap, player });
        if (cantraptrigger != null && cantraptrigger is bool && (bool)cantraptrigger) return null;

        if (configData.Options.TrapsIgnorePlayers) return false;

        return null;
    }

    private BasePlayer GetMountedPlayer(BaseMountable mount)
    {
        if (mount.GetMounted())
        {
            return mount.GetMounted();
        }

        if (mount as BaseVehicle)
        {
            BaseVehicle vehicle = mount as BaseVehicle;

            foreach (BaseVehicle.MountPointInfo point in vehicle.mountPoints)
            {
                if (point.mountable.IsValid() && point.mountable.GetMounted())
                {
                    return point.mountable.GetMounted();
                }
            }
        }

        return null;
    }

    private object OnSamSiteTarget(SamSite sam, BaseMountable mountable)
    {
        if (sam == null) return null;
        if (mountable == null) return null;
        BasePlayer player = GetMountedPlayer(mountable);

        if (player.IsValid())
        {
            if (sam.OwnerID == 0 && configData.Options.NPCSamSitesIgnorePlayers)
            {
                DoLog($"Skipping targeting by NPC SamSite of player {player.displayName}");
                return true; // block
            }
            else if (sam.OwnerID > 0 && configData.Options.SamSitesIgnorePlayers)
            {
                DoLog($"Skipping targeting by SamSite of player {player.displayName}");
                return true; // block
            }

            try
            {
                object extCanEntityBeTargeted = BroadcastReturn("CanEntityBeTargeted", new object[] { player, sam });
                if (extCanEntityBeTargeted != null && extCanEntityBeTargeted is bool)
                {
                    if ((bool)extCanEntityBeTargeted)
                    {
                        return null; // allow
                    }
                    else
                    {
                        return true; // block
                    }
                }
            }
            catch { }
        }
        return null; // allow
    }

    private object CanBeTargeted(BasePlayer target, GunTrap turret)
    {
        if (target == null || turret == null) return null;
        if (!enabled) return null;

        try
        {
            object extCanEntityBeTargeted = BroadcastReturn("CanEntityBeTargeted", new object[] { target, turret });
            if (extCanEntityBeTargeted != null && extCanEntityBeTargeted is bool && (bool)extCanEntityBeTargeted)
            {
                return null;
            }
        }
        catch { }

        if (!configData.Options.AutoTurretTargetsPlayers)
        {
            return false;
        }
        return null;
    }

    private object CanBeTargeted(BasePlayer target, FlameTurret turret)
    {
        if (target == null || turret == null) return null;
        if (!enabled) return null;

        try
        {
            object extCanEntityBeTargeted = BroadcastReturn("CanEntityBeTargeted", new object[] { target, turret });
            if (extCanEntityBeTargeted != null && extCanEntityBeTargeted is bool && (bool)extCanEntityBeTargeted)
            {
                return null;
            }
        }
        catch { }

        if (!configData.Options.AutoTurretTargetsPlayers)
        {
            return false;
        }
        return null;
    }

    private object CanBeTargeted(BasePlayer target, HelicopterTurret turret)
    {
        if (target == null || turret == null) return null;
        if (!enabled) return null;

        if (!configData.Options.HeliTurretTargetsPlayers) return false;
        return null;
    }

    private object CanBeTargeted(BasePlayer target, AutoTurret turret)
    {
        if (target == null || turret == null) return null;
        if (!enabled) return null;

        try
        {
            object extCanEntityBeTargeted = BroadcastReturn("CanEntityBeTargeted", new object[] { target, turret });
            if (extCanEntityBeTargeted != null && extCanEntityBeTargeted is bool && (bool)extCanEntityBeTargeted)
            {
                return null;
            }
        }
        catch { }

        if (!configData.Options.AutoTurretTargetsPlayers)
        {
            return false;
        }
        return null;
    }

    private object CanBeTargeted(BasePlayer target, NPCAutoTurret turret)
    {
        if (target == null || turret == null) return null;
        if (!enabled) return null;

        try
        {
            object extCanEntityBeTargeted = BroadcastReturn("CanEntityBeTargeted", new object[] { target, turret });
            if (extCanEntityBeTargeted != null && extCanEntityBeTargeted is bool && (bool)extCanEntityBeTargeted)
            {
                return null;
            }
        }
        catch { }

        if (!configData.Options.NPCAutoTurretTargetsPlayers)
        {
            return false;
        }
        return null;
    }

    private bool BlockFallDamage(BaseCombatEntity entity)
    {
        // Special case where attack by scrapheli initiates fall damage on a player.  This was often used to kill players and bypass the rules.
        List<BaseEntity> ents = new List<BaseEntity>();
        Vis.Entities(entity.transform.position, 5, ents);
        foreach (BaseEntity ent in ents)
        {
            if (ent.ShortPrefabName == "scraptransporthelicopter" && configData.Options.BlockScrapHeliFallDamage)
            {
                DoLog("Fall caused by scrapheli.  Blocking...");
                return true;
            }
        }
        return false;
    }
    #endregion

    #region inbound_hooks
    private bool TogglePVE(bool on = true) => enabled = on;

    private bool TogglePVERuleset(string rulesetname, bool on = true)
    {
        string enable = "0";
        if (on) enable = "1";
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand cmd = new SqliteCommand($"UPDATE ngpve_rulesets SET enabled='{enable}", c))
            {
                cmd.ExecuteNonQuery();
            }
        }
        return true;
    }

    private bool AddOrUpdateMapping(string key, string rulesetname)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (rulesetname == null) return false;

        DoLog($"AddOrUpdateMapping called for ruleset: {rulesetname}, zone: {key}", 0);
        bool foundrs = false;
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand au = new SqliteCommand($"SELECT DISTINCT enabled FROM ngpve_rulesets WHERE name='{rulesetname}'", c))
            {
                using (SqliteDataReader ar = au.ExecuteReader())
                {
                    while (ar.Read())
                    {
                        foundrs = true;
                    }
                }
            }
        }
        if (foundrs)
        {
            if (ngpvezonemaps.ContainsKey(rulesetname) && !ngpvezonemaps[rulesetname].map.Contains(key))
            {
                ngpvezonemaps[rulesetname].map.Add(key);
            }
            else
            {
                ngpvezonemaps.Add(rulesetname, new NextGenPVEZoneMap() { map = new List<string>() { key } });
            }
            SaveData();
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                DoLog($"UPDATE ngpve_rulesets SET zone='lookup' WHERE rulesetname='{rulesetname}'");
                using (SqliteCommand cmd = new SqliteCommand($"UPDATE ngpve_rulesets SET zone='lookup' WHERE name='{rulesetname}'", c))
                {
                    cmd.ExecuteNonQuery();
                }
            }
            return true;
        }
        else
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                DoLog($"INSERT INTO ngpve_rulesets VALUES('{rulesetname}', '1', '1', '1', 'lookup', '', '', '', '', 0)");

                using (SqliteCommand cmd = new SqliteCommand($"INSERT INTO ngpve_rulesets VALUES('{rulesetname}', '1', '1', '1', 'lookup', '', '', '', '', 0)", c))
                {
                    cmd.ExecuteNonQuery();
                }
            }
            if (ngpvezonemaps.ContainsKey(rulesetname)) ngpvezonemaps.Remove(rulesetname);
            ngpvezonemaps.Add(rulesetname, new NextGenPVEZoneMap() { map = new List<string>() { key } });
            SaveData();
            return true;
        }
    }

    private bool RemoveMapping(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        List<string> foundrs = new List<string>();

        DoLog($"RemoveMapping called for zone: {key}", 0);

        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            //Utils.DoLog($"SELECT name FROM ngpve_rulesets WHERE zone='{key}'");
            using (SqliteCommand rm = new SqliteCommand($"SELECT name FROM ngpve_rulesets WHERE zone='{key}'", c))
            {
                using (SqliteDataReader rd = rm.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        string rn = rd.GetString(0);
                        foundrs.Add(rn);
                        ngpvezonemaps.Remove(rn);
                        SaveData();
                    }
                }
            }
        }

        if (foundrs.Count > 0)
        {
            foreach (string found in foundrs)
            {
                using (SqliteConnection c = new SqliteConnection(connStr))
                {
                    c.Open();
                    using (SqliteCommand cmd = new SqliteCommand($"UPDATE ngpve_rulesets SET zone='0' WHERE name='{found}'", c))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        return true;
    }

    private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitinfo)
    {
        //if (ConVar.Server.pve) ConsoleSystem.Run(ConsoleSystem.Option.Server.FromServer(), "server.pve 0");
        if (!enabled) return null;
        if (entity == null) return null;
        if (hitinfo == null) return null;
        string majority = hitinfo.damageTypes.GetMajorityDamageType().ToString();
        if (majority == "Decay") return null;

        if (configData.Options.debug) Utils.DoLog("ENTRY:");
        if (configData.Options.debug) Utils.DoLog("Checking for fall damage");
        if (majority == "Fall" && hitinfo.Initiator == null)
        {
            DoLog($"Null initiator for attack on {entity.ShortPrefabName} by Fall");
            if (BlockFallDamage(entity))
            {
                if (configData.Options.debug) Utils.DoLog(":EXIT");
                return true;
            }
        }

        if (configData.Options.debug) Utils.DoLog("Calling external damage hook");
        try
        {
            object CanTakeDamage = BroadcastReturn("CanEntityTakeDamage", new object[] { entity, hitinfo });
            if (CanTakeDamage != null && CanTakeDamage is bool && (bool)CanTakeDamage)
            {
                if (configData.Options.debug) Utils.DoLog("Calling external damage hook PASSED: ALLOW\n:EXIT");
                return null;
            }
        }
        catch
        {
            if (configData.Options.debug) Utils.DoLog("Calling external damage hook FAILED");
        }

        string stype; string ttype;
        bool canhurt;
        if (IsMLRS(hitinfo))
        {
            if (configData.Options.debug) Utils.DoLog($"attacker prefab: {hitinfo.WeaponPrefab?.ShortPrefabName}, victim prefab: {entity.ShortPrefabName}");
            if (configData.Options.debug) Utils.DoLog("Calling EvaluateRulesets: MLRS");
            canhurt = EvaluateRulesets(hitinfo.WeaponPrefab, entity, out stype, out ttype);
        }
        else
        {
            if (configData.Options.debug) Utils.DoLog($"attacker prefab: {hitinfo.Initiator?.ShortPrefabName}, victim prefab: {entity.ShortPrefabName}");
            if (configData.Options.debug) Utils.DoLog("Calling EvaluateRulesets: Standard");
            canhurt = EvaluateRulesets(hitinfo.Initiator, entity, out stype, out ttype);
        }
        if (stype.Length == 0 && ttype.Length == 0)
        {
            if (configData.Options.debug) Utils.DoLog("Calling EvaluateRulesets DETECTION FAILED");
            return null;
        }
        if (configData.Options.debug) Utils.DoLog("Calling EvaluateRulesets PASSED");

        if (stype == "BasePlayer")
        {
            BasePlayer hibp = hitinfo.Initiator as BasePlayer;
            if (configData.Options.debug) Utils.DoLog("Checking for god perms");
            if (hibp != null && Permissions.UserHasPermission(hibp.UserIDString, permNextGenPVEGod))
            {
                Utils.DoLog("Admin almighty!");
                return null;
            }
            if (configData.Options.debug) Utils.DoLog("Checking for suicide flag or friendly fire");
            if (ttype == "BasePlayer")
            {
                ulong sid = hibp.userID;
                ulong tid = (entity as BasePlayer)?.userID ?? 0;
                if (sid > 0 && tid > 0)
                {
                    if (sid == tid && configData.Options.AllowSuicide)
                    {
                        DoLog("AllowSuicide TRUE");
                        canhurt = true;
                    }
                    else if (Utils.IsFriend(sid, tid) && configData.Options.AllowFriendlyFire)
                    {
                        DoLog("AllowFriendlyFire TRUE");
                        canhurt = true;
                    }
                }
            }
        }

        if (canhurt)
        {
            DoLog($"DAMAGE ALLOWED for {stype} attacking {ttype}, majority damage type {majority}");
            if (configData.Options.debug) Utils.DoLog(":EXIT");
            return null;
        }

        DoLog($"DAMAGE BLOCKED for {stype} attacking {ttype}, majority damage type {majority}");
        if (configData.Options.debug) Utils.DoLog(":EXIT");
        return true;
    }
    #endregion

    #region Main
    private bool EvaluateRulesets(BaseEntity source, BaseEntity target, out string stype, out string ttype)
    {
        if (source == null || target == null)
        {
            if (configData.Options.debug) Utils.DoLog("Null source or target object");
            stype = "";
            ttype = "";
            return false;
        }

        if (configData.Options.debug) Utils.DoLog("Getting source type");
        if (source.ShortPrefabName == "rocket_mlrs")
        {
            stype = "MLRS";
        }
        else if (IsBaseHelicopter(source))
        {
            stype = "BaseHelicopter";
        }
        else
        {
            stype = source?.GetType().Name;
        }

        if (configData.Options.debug) Utils.DoLog("Getting target type");
        ttype = target?.GetType().Name;
        if (string.IsNullOrEmpty(stype) || string.IsNullOrEmpty(ttype))
        {
            if (configData.Options.debug) Utils.DoLog("Type detection failure.  Bailing out.");
            stype = "";
            ttype = "";
            return false;
        }
        if (configData.Options.debug) Utils.DoLog($"attacker type: {stype}, victim type: {ttype}");

        // Special case for preventing codelock hacking
        if (stype == "CodeLock" && ttype == "BasePlayer")
        {
            DoLog("Allowing codelock damage");
            return true;
        }

        string zone = "default";
        bool hasBP = true;
        bool isBuilding = false;
        //bool isHeli = false;

        // Special cases for building damage requiring owner or auth access
        if (stype == "BasePlayer" && (ttype == "BuildingBlock" || ttype == "Door" || ttype == "wall.window"))
        {
            isBuilding = true;
            if (!PlayerOwnsItem(source as BasePlayer, target))
            {
                DoLog("No building block access.");
                hasBP = false;
            }
            else
            {
                DoLog("Player has privilege to block or is not blocked by TC.");
            }
        }
        else if (stype == "BasePlayer" && ttype == "BuildingPrivlidge")
        {
            if (!PlayerOwnsTC(source as BasePlayer, target as BuildingPrivlidge))
            {
                DoLog("No building privilege.");
                hasBP = false;
            }
            else if (configData.Options.protectedDays > 0 && target.OwnerID > 0)
            {
                // Check days since last owner connection
                long lc;
                //if (PlayerDatabase != null && configData.Options.usePlayerDatabase)
                //{
                //    lc = (long)PlayerDatabase?.CallHook("GetPlayerData", target.OwnerID.ToString(), "lc");
                //}
                //else
                lastConnected.TryGetValue(target.OwnerID.ToString(), out lc);
                if (lc > 0)
                {
                    long now = ToEpochTime(DateTime.UtcNow);
                    float days = Math.Abs((now - lc) / 86400);
                    if (days > configData.Options.protectedDays)
                    {
                        DoLog($"Allowing TC damage for offline owner beyond {configData.Options.protectedDays} days");
                        return true;
                    }
                    else
                    {
                        DoLog($"Owner was last connected {days} days ago and is still protected...");
                    }
                }
            }
            else
            {
                DoLog("Player has building privilege or is not blocked.");
            }
        }
        else if (stype == "BaseHelicopter" && (ttype == "BuildingBlock" || ttype == "Door" || ttype == "wall.window" || ttype == "BuildingPrivlidge"))
        {
            isBuilding = true;
            hasBP = false;
            //foreach (PatrolHelicopterAI.targetinfo x in (source as BaseHelicopter)?.myAI._targetList.ToArray())
            //{
            //    BasePlayer y = x.ent as BasePlayer;
            //    if (y == null) continue;
            //    DoLog($"Heli targeting player {y.displayName}.  Checking building permission for {target.ShortPrefabName}");
            //    if (PlayerOwnsItem(y, target))
            //    {
            //        DoLog("Yes they own that building!");
            //        hasBP = true;
            //    }
            //}
        }

        bool foundmatch = false;
        bool foundexception = false;
        bool foundexclusion = false;
        bool damage = true;
        string rulesetname = null;

        string src = null; string tgt = null;
        //Utils.DoLog($"SELECT DISTINCT name FROM ngpve_entities WHERE type='{stype}'");
        using (SqliteCommand findIt = new SqliteCommand($"SELECT DISTINCT name FROM ngpve_entities WHERE type='{stype}'", sqlConnection))
        {
            using (SqliteDataReader readMe = findIt.ExecuteReader())
            {
                while (readMe.Read())
                {
                    src = readMe.GetString(0);
                    break;
                }
            }
        }

        //Utils.DoLog($"SELECT DISTINCT name FROM ngpve_entities WHERE type='{ttype}'");
        using (SqliteCommand findIt = new SqliteCommand($"SELECT DISTINCT name FROM ngpve_entities WHERE type='{ttype}'", sqlConnection))
        {
            using (SqliteDataReader readMe = findIt.ExecuteReader())
            {
                while (readMe.Read())
                {
                    tgt = readMe.GetString(0);
                    break;
                }
            }
        }

        string mquery;
        switch (zone)
        {
            case "default":
                DoLog("Default ruleset query");
                mquery = "SELECT DISTINCT name, zone, damage, enabled FROM ngpve_rulesets WHERE zone='0' OR zone='default'";
                break;
            default:
                //mquery = $"SELECT DISTINCT name, zone, damage, enabled FROM ngpve_rulesets WHERE zone='{zone}' OR zone='default' OR zone='0' OR zone='lookup'";
                mquery = $"SELECT DISTINCT name, zone, damage, enabled FROM ngpve_rulesets WHERE zone='{zone}' OR zone='lookup'";
                bool zonefound = false;
                using (SqliteCommand findIt = new SqliteCommand(mquery, sqlConnection))
                {
                    using (SqliteDataReader readMe = findIt.ExecuteReader())
                    {
                        while (readMe.Read())
                        {
                            zonefound = true;
                        }
                    }
                }
                if (!zonefound)
                {
                    // No rules found matching this zone, revert to default...
                    mquery = "SELECT DISTINCT name, zone, damage, enabled FROM ngpve_rulesets WHERE zone='0' OR zone='default'";
                }
                break;
        }
        //using (SqliteCommand findIt = new SqliteCommand("SELECT DISTINCT name, zone, damage, enabled FROM ngpve_rulesets", sqlConnection))
        using (SqliteCommand findIt = new SqliteCommand(mquery, sqlConnection))
        {
            using (SqliteDataReader readMe = findIt.ExecuteReader())
            {
                DoLog("QUERY START");
                while (readMe.Read())
                {
                    DoLog("READING...");
                    if (foundmatch) break; // Breaking due to match found in previously checked ruleset
                    bool enabled = readMe.GetBoolean(3);
                    rulesetname = readMe.GetString(0);
                    string rulesetzone = readMe.GetString(1);

                    if (!enabled)
                    {
                        DoLog($"Skipping ruleset {rulesetname}, which is disabled");
                        continue;
                    }

                    foundexception = false;
                    foundexclusion = false;

                    damage = readMe.GetBoolean(2);

                    // ZONE CHECKS
                    // First: If we are in a matching zone, use it
                    if (zone == rulesetzone)
                    {
                        DoLog($"Zone match for ruleset {rulesetname}, zone {rulesetzone}");
                    }
                    // Second: If we are in the default zone, and this rulesetzone is not, skip it
                    if (zone == "default" && rulesetzone != "default" && rulesetzone != "" && rulesetzone != "0")
                    {
                        DoLog($"Skipping ruleset {rulesetname} due to zone mismatch with current zone, {zone}");
                        continue;
                    }
                    // Third: rulesetzone == "lookup" but zonemaps does not contain this zone, skip it
                    else if (rulesetzone == "lookup" && ngpvezonemaps.ContainsKey(rulesetname))
                    {
                        if (!ngpvezonemaps[rulesetname].map.Contains(zone))
                        {
                            DoLog($"Skipping ruleset {rulesetname} due to zone lookup mismatch with current zone, {zone}");
                            continue;
                        }
                        DoLog($"Lookup zone {zone}");
                    }

                    DoLog($"Checking ruleset {rulesetname} for {src} attacking {tgt}.");
                    //Utils.DoLog($"SELECT enabled, src_exclude, tgt_exclude FROM ngpve_rulesets WHERE name='{rulesetname}' AND enabled='1' AND exception='{src}_{tgt}'");

                    if (src != null && tgt != null)
                    {
                        DoLog($"Found {stype} attacking {ttype}.  Checking ruleset {rulesetname}, zone {rulesetzone}");
                        using (SqliteCommand rq = new SqliteCommand($"SELECT src_exclude, tgt_exclude FROM ngpve_rulesets WHERE name='{rulesetname}' AND exception='{src}_{tgt}'", sqlConnection))
                        {
                            using (SqliteDataReader entry = rq.ExecuteReader())
                            {
                                while (entry.Read())
                                {
                                    // source and target exist - verify that they are not excluded
                                    DoLog($"Found exception match for {stype} attacking {ttype}");

                                    string foundsrc = entry.GetValue(0).ToString();
                                    string foundtgt = entry.GetValue(1).ToString();
                                    // Following check to prevent exception for heli fireball damage
                                    //foundexception = foundsrc != "fireball" || !isHeli;
                                    foundexception = true;

                                    if (foundsrc.Contains(stype))
                                    {
                                        DoLog($"Exclusion for {stype}");
                                        foundexclusion = true;
                                        break;
                                    }
                                    else if (foundtgt.Contains(ttype))
                                    {
                                        DoLog($"Exclusion for {ttype}");
                                        foundexclusion = true;
                                        break;
                                    }
                                    else
                                    {
                                        DoLog($"No exclusions for {stype} to {ttype}");
                                        foundexclusion = false;
                                    }
                                }
                            }
                        }

                        // Special override to HonorBuildingPrivilege since we are NOT seeing a XXX_building exception and damage is true.
                        if (!foundexception && damage && isBuilding)
                        {
                            DoLog($"Setting damage allow for building based on ruleset {rulesetname} damage value {damage}");
                            foundmatch = true;
                            hasBP = true;
                        }

                        if (foundexception && !foundexclusion)
                        {
                            // allow break on current ruleset and zone match with no exclusions
                            foundmatch = true;
                        }
                        else if (!foundexception)
                        {
                            // allow break on current ruleset and zone match with no exceptions
                            foundmatch = true;
                        }
                    }
                }
            }
        }
        if (hasBP && isBuilding)
        {
            DoLog("Player has building privilege and is attacking a BuildingBlock/door/window, heli is attacking a building owned by a targeted player, or damage is enabled for buildings with no exceptions.");
            return true;
        }
        else if (!hasBP && isBuilding)
        {
            DoLog("Player does NOT have building privilege and is attacking a BuildingBlock.  Or, player owner is not being targeted by the heli.");
            return false;
        }

        if (foundmatch)
        {
            DoLog($"Sanity check foundexception: {foundexception}, foundexclusion: {foundexclusion}");
            if (foundexception && !foundexclusion)
            {
                DoLog($"Ruleset '{rulesetname}' exception: Setting damage to {!damage}");
                return !damage;
            }
        }
        else
        {
            DoLog($"No Ruleset match or exclusions: Setting damage to {damage}");
            return damage;
        }
        DoLog("NO RULESET MATCH!");
        return damage;
    }

    private void MessageToAll(string key, string ruleset)
    {
        if (!configData.Options.useMessageBroadcast && !configData.Options.useGUIAnnouncements) return;
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (configData.Options.useMessageBroadcast)
            {
                Message(player, key, ruleset);
            }
        }
    }

    public static IEnumerable<DateTime> AllDatesInMonth(int year, int month)
    {
        int days = DateTime.DaysInMonth(year, month);
        for (int day = 1; day <= days; day++)
        {
            yield return new DateTime(year, month, day);
        }
    }

    private void NewPurgeSchedule()
    {
        if (!configData.Options.autoCalcPurge) return;

        int month = DateTime.Now.Month + 1;
        int year = DateTime.Now.Year;

        if (month > 12 || month < 1)
        {
            month = 1;
            year++;
        }
        if (month == 12) year++;

        IEnumerable<DateTime> thursdays = AllDatesInMonth(year, month).Where(i => i.DayOfWeek == DayOfWeek.Thursday);
        DoLog($"Next force wipe day will be {thursdays.First().ToShortDateString()}");

        configData.Options.purgeStart = thursdays.First().Subtract(TimeSpan.FromDays(configData.Options.autoCalcPurgeDays)).ToShortDateString() + " 0:00";
        configData.Options.purgeEnd = thursdays.First().ToShortDateString() + " 0:00";
        SaveConfig(configData);
    }

    private bool CheckPurgeSchedule()
    {
        if (!configData.Options.purgeEnabled) return false;
        bool foundPurge = false;
        if (configData.Options.purgeStart != null && configData.Options.purgeEnd != null)
        {
            foundPurge = true;
            // This is done here to ensure updates from config changes
            DateTime today = DateTime.Now;
            DateTime pstart = Convert.ToDateTime(configData.Options.purgeStart, new CultureInfo("en-US", true));
            DateTime pend = Convert.ToDateTime(configData.Options.purgeEnd, new CultureInfo("en-US", true));
            if (pstart < today && today < pend)
            {
                return true;
            }
        }
        if (!foundPurge) NewPurgeSchedule();
        return false;
    }

    private void RunSchedule(bool refresh = false)
    {
        return;
        Dictionary<string, bool> enables = new Dictionary<string, bool>();
        TimeSpan ts = new TimeSpan();
        bool invert = false;
        Dictionary<string, bool> ngpveinvert = new Dictionary<string, bool>();
        inPurge = CheckPurgeSchedule();
        if (inPurge != purgeLast)
        {
            switch (purgeLast)
            {
                case true:
                    enabled = true;
                    string end = string.IsNullOrEmpty(configData.Options.purgeEndMessage) ? Lang("purgeinactive") : configData.Options.purgeEndMessage;
                    MessageToAll(end, "");
                    DoLog(Lang("purgeinactive"));
                    BroadcastReturn("EnableMe");
                    break;
                default:
                    enabled = false;
                    string start = string.IsNullOrEmpty(configData.Options.purgeStartMessage) ? Lang("purgeactive") : configData.Options.purgeStartMessage;
                    MessageToAll(start, "");
                    DoLog(Lang("purgeactive"));
                    BroadcastReturn("DisableMe");
                    break;
            }
            purgeLast = inPurge;
        }

        switch (configData.Options.useRealTime)
        {
            case true:
                ts = new TimeSpan((int)DateTime.Now.DayOfWeek, 0, 0, 0).Add(DateTime.Now.TimeOfDay);
                break;
            default:
                try
                {
                    ts = TOD_Sky.Instance.Cycle.DateTime.TimeOfDay;
                }
                catch
                {
                    Utils.DoLog("TOD_Sky failure...");
                    refresh = true;
                }
                break;
        }
        if (refresh)// && configData.Options.useSchedule)
        {
            ngpveschedule = new Dictionary<int, NGPVE_Schedule>();
            int i = 0;
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand use = new SqliteCommand("SELECT DISTINCT name, schedule, invschedule FROM ngpve_rulesets WHERE schedule != '0'", c))
                {
                    using (SqliteDataReader schedule = use.ExecuteReader())
                    {
                        while (schedule.Read())
                        {
                            string nm = schedule.GetString(0);
                            string sc = schedule.GetValue(1).ToString();
                            invert = Convert.ToBoolean(schedule.GetBoolean(2));
                            if (nm != "" && sc != "")
                            {
                                ngpveschedule.Add(i, new NGPVE_Schedule()
                                {
                                    name = nm,
                                    schedule = sc,
                                    invert = invert
                                });
                                ngpveinvert.Add(nm, invert);
                                i++;
                            }
                        }
                    }
                }
            }
        }

        // Actual schedule processing here...
        foreach (KeyValuePair<int, NGPVE_Schedule> scheduleInfo in ngpveschedule)
        {
            NextGenPVESchedule parsed;
            if (ParseSchedule(scheduleInfo.Value.name, scheduleInfo.Value.schedule, out parsed))
            {
                //invert = inPurge || scheduleInfo.Value.invert;
                invert = scheduleInfo.Value.invert;
                DoLog("Schedule string was parsed correctly...");
                int i = 0;
                foreach (string x in parsed.day)
                {
                    DoLog($"Schedule day == {x} {parsed.dayName[i]} {parsed.starthour} to {parsed.endhour}");
                    i++;
                    if (ts.Days.ToString() == x)
                    {
                        DoLog($"Day matched.  Comparing {ts.Hours}:{ts.Minutes.ToString().PadLeft(2, '0')} to start time {parsed.starthour}:{parsed.startminute} and end time {parsed.endhour}:{parsed.endminute}");
                        if (ts.Hours >= Convert.ToInt32(parsed.starthour) && ts.Hours <= Convert.ToInt32(parsed.endhour))
                        {
                            // Hours matched for activating ruleset, check minutes
                            DoLog($"Hours matched for ruleset {scheduleInfo.Key}", 1);
                            if (ts.Hours == Convert.ToInt32(parsed.starthour) && ts.Minutes >= Convert.ToInt32(parsed.startminute))
                            {
                                DoLog("Matched START hour and minute.", 2);
                                enables[scheduleInfo.Value.name] = !invert;
                            }
                            else if (ts.Hours == Convert.ToInt32(parsed.endhour) && ts.Minutes <= Convert.ToInt32(parsed.endminute))
                            {
                                DoLog("Matched END hour and minute.", 2);
                                enables[scheduleInfo.Value.name] = !invert;
                            }
                            else if (ts.Hours > Convert.ToInt32(parsed.starthour) && ts.Hours < Convert.ToInt32(parsed.endhour))
                            {
                                DoLog("Between start and end hours.", 2);
                                enables[scheduleInfo.Value.name] = !invert;
                            }
                            else
                            {
                                DoLog("Minute mismatch for START OR END.", 2);
                                enables[scheduleInfo.Value.name] = invert;
                            }
                        }
                        else
                        {
                            DoLog($"Hours NOT matched for ruleset {scheduleInfo.Key}", 1);
                            enables[scheduleInfo.Value.name] = invert;
                        }
                    }
                    //                        else
                    //                        {
                    //                            DoLog($"Day NOT matched for ruleset {scheduleInfo.Key}", 1);
                    //                            enables[scheduleInfo.Key] = false;
                    //                        }
                }
            }
        }

        foreach (KeyValuePair<string, bool> doenable in enables)
        {
            DoLog($"Enable = {doenable.Key}: {doenable.Value}, invert = {ngpveinvert[doenable.Key]}");
            switch (doenable.Value)// & !invert)
            {
                case false:
                    DoLog($"Disabling ruleset {doenable.Key} (inverted={ngpveinvert[doenable.Key]})", 3);
                    using (SqliteConnection c = new SqliteConnection(connStr))
                    {
                        c.Open();
                        using (SqliteCommand info = new SqliteCommand($"SELECT DISTINCT enabled FROM ngpve_rulesets WHERE name='{doenable.Key}'", c))
                        {
                            using (SqliteDataReader crs = info.ExecuteReader())
                            {
                                while (crs.Read())
                                {
                                    string was = crs.GetValue(0).ToString();
                                    //if (was != "0") MessageToAll("pvedisabled", doenable.Key);
                                }
                            }
                        }
                        using (SqliteCommand cmd = new SqliteCommand($"UPDATE ngpve_rulesets SET enabled='0' WHERE name='{doenable.Key}'", c))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }
                    break;
                case true:
                    DoLog($"Enabling ruleset {doenable.Key} (inverted={ngpveinvert[doenable.Key]})", 3);
                    using (SqliteConnection c = new SqliteConnection(connStr))
                    {
                        c.Open();
                        using (SqliteCommand info = new SqliteCommand($"SELECT DISTINCT enabled FROM ngpve_rulesets WHERE name='{doenable.Key}'", c))
                        {
                            using (SqliteDataReader crs = info.ExecuteReader())
                            {
                                while (crs.Read())
                                {
                                    string was = crs.GetValue(0).ToString();
                                    //if (was != "1") MessageToAll("pveenabled", doenable.Key);
                                }
                            }
                        }
                        using (SqliteCommand cmd = new SqliteCommand($"UPDATE ngpve_rulesets SET enabled='1' WHERE name='{doenable.Key}'", c))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }
                    break;
            }
        }

        //scheduleTimer = timer.Once(configData.Options.useRealTime ? 30f : 5f, () => RunSchedule(refresh));
    }

    public string EditScheduleHM(string rs, string newval, string tomod)
    {
        string sc = "";
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            string query = $"SELECT DISTINCT schedule FROM ngpve_rulesets WHERE name='{rs}' AND schedule != '0'";
            Utils.DoLog(query);
            using (SqliteCommand use = new SqliteCommand(query, c))
            {
                using (SqliteDataReader schedule = use.ExecuteReader())
                {
                    while (schedule.Read())
                    {
                        sc = schedule.GetValue(0).ToString();
                    }
                }
            }
        }

        string[] scparts = Regex.Split(sc, @"(.*)\;(.*)\:(.*)\;(.*)\:(.*)");

        if (scparts.Length < 5)
        {
            return ";0:00;23:59";
        }

        switch (tomod)
        {
            case "sh":
                scparts[2] = newval.PadLeft(2, '0');
                break;
            case "sm":
                scparts[3] = newval.PadLeft(2, '0');
                break;
            case "eh":
                scparts[4] = newval.PadLeft(2, '0');
                break;
            case "em":
                scparts[5] = newval.PadLeft(2, '0');
                break;
        }
        return $"{scparts[1]};{scparts[2]}:{scparts[3]};{scparts[4]}:{scparts[5]}";
    }

    public string EditScheduleDay(string rs, string newval)
    {
        string sc = "";
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand use = new SqliteCommand($"SELECT DISTINCT schedule FROM ngpve_rulesets WHERE name='{rs}' AND schedule != '0'", c))
            {
                using (SqliteDataReader schedule = use.ExecuteReader())
                {
                    while (schedule.Read())
                    {
                        sc = schedule.GetValue(0).ToString();
                    }
                }
            }
        }
        string[] scparts = Regex.Split(sc, @"(.*)\;(.*)\:(.*)\;(.*)\:(.*)");

        if (scparts.Length < 5)
        {
            return ";0:00;23:59";
        }

        string daypart = scparts[1];

        if (newval == "all")
        {
            daypart = scparts[1] != "*" ? "*" : "";
        }
        else if (newval == "none")
        {
            daypart = "";
        }
        else
        {
            string[] days = daypart.Split(',');
            foreach (string nd in newval.Split(','))
            {
                if (days.Contains(nd))
                {
                    daypart = scparts[1].Replace($"{nd},", "").Replace($",{nd}", "");
                }
                else
                {
                    daypart += $",{nd}";
                }
            }
        }
        //return string.Join(";", scparts);
        return $"{daypart};{scparts[2]}:{scparts[3]};{scparts[4]}:{scparts[5]}".TrimStart(',');
    }

    private string ScheduleToString(NextGenPVESchedule schedule)
    {
        return $"{schedule.day};{schedule.starthour}:{schedule.startminute};{schedule.endhour}:{schedule.endminute}";
    }

    private bool ParseSchedule(string rs, string dbschedule, out NextGenPVESchedule parsed)
    {
        DoLog($"ParseSchedule called on string {dbschedule}");
        parsed = new NextGenPVESchedule();

        if (string.IsNullOrEmpty(dbschedule) || dbschedule == "0")
        {
            // Set default values to enable or disable (inverted) always
            for (int i = 0; i < 7; i++)
            {
                parsed.day.Add(i.ToString());
                parsed.dayName.Add(Enum.GetName(typeof(DayOfWeek), i));
            }
            parsed.starthour = "0";
            parsed.startminute = "0";
            parsed.endhour = "23";
            parsed.endminute = "59";
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand us = new SqliteCommand($"UPDATE ngpve_rulesets SET schedule='*;0:00;23:59' WHERE name='{rs}'", c))
                {
                    us.ExecuteNonQuery();
                }
            }
            return true;
        }

        string[] nextgenschedule = Regex.Split(dbschedule, @"(.*)\;(.*)\:(.*)\;(.*)\:(.*)");
        if (nextgenschedule.Length < 6) return false;

        parsed.starthour = nextgenschedule[2];
        parsed.startminute = nextgenschedule[3];
        parsed.endhour = nextgenschedule[4];
        parsed.endminute = nextgenschedule[5];

        parsed.day = new List<string>();
        parsed.dayName = new List<string>();

        string tmp = nextgenschedule[1];
        string[] days = tmp.Split(',');

        int day;
        if (tmp == "*")
        {
            for (int i = 0; i < 7; i++)
            {
                parsed.day.Add(i.ToString());
                parsed.dayName.Add(Enum.GetName(typeof(DayOfWeek), i));
            }
        }
        else if (tmp?.Length == 0)
        {
            parsed.day.Add("none");
            parsed.dayName.Add("none");
        }
        else if (days.Length > 0)
        {
            foreach (string d in days)
            {
                int.TryParse(d, out day);
                parsed.day.Add(day.ToString());
                parsed.dayName.Add(Enum.GetName(typeof(DayOfWeek), day));
            }
        }
        else
        {
            int.TryParse(tmp, out day);
            parsed.day.Add(day.ToString());
            parsed.dayName.Add(Enum.GetName(typeof(DayOfWeek), day));
        }
        return true;
    }

    private void DoLog(string message, int indent = 0)
    {
        if (!enabled) return;
        if (message.Contains("Turret")) return; // Log volume FIXME
        //if (dolog) LogToFile(logfilename, "".PadLeft(indent, ' ') + message, this);
    }
    #endregion

    #region Commands
    [Command("pveupdate")]
    private void CmdUpdateEnts(BasePlayer player, string command, string[] args)
    {
        if (!Permissions.UserHasPermission(player.displayName, permNextGenPVEAdmin)) { Message(player, "notauthorized"); return; }
        UpdateEnts();
    }

    //[Command("pvebackup")]
    //private void CmdNextGenPVEbackup(BasePlayer player, string command, string[] args)
    //{
    //    if (!Auxide.Permissions.UserHasPermission(player.displayName, permNextGenPVEAdmin)) { Message(player, "notauthorized"); return; }

    //    string backupfile = "nextgenpve_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".db";
    //    if (args.Length > 1)
    //    {
    //        backupfile = args[1] + ".db";
    //    }
    //    using (SqliteConnection c = new SqliteConnection(connStr))
    //    {
    //        c.Open();
    //        string bkup = $"Data Source={data.Directory}{Path.DirectorySeparatorChar}{Name}{Path.DirectorySeparatorChar}{backupfile};";
    //        //Utils.DoLog($"Using new db connection '{bkup}'");
    //        using (SqliteConnection d = new SqliteConnection(bkup))
    //        {
    //            d.Open();
    //            c.BackupDatabase(d, "main", "main", -1, null, -1);
    //        }
    //    }
    //    LMessage(player, "BackupDone", backupfile);
    //}

    [Command("pvedrop")]
    private void CmdNextGenPVEDrop(BasePlayer player, string command, string[] args)
    {
        if (configData.Options.AllowDropDatabase) return;
        if (!Permissions.UserHasPermission(player.displayName, permNextGenPVEAdmin)) { Message(player, "notauthorized"); return; }

        LoadDefaultEntities();
        LoadDefaultRules();
        LoadDefaultRuleset();

        if (args.Length > 0)
        {
            if (args[0] == "gui") GUIRuleSets(player);
        }
    }

    [Command("pveenable")]
    private void CmdNextGenPVEenable(BasePlayer player, string command, string[] args)
    {
        if (!Permissions.UserHasPermission(player.displayName, permNextGenPVEAdmin)) { Message(player, "notauthorized"); return; }

        enabled = !enabled;
        if (args.Length > 0)
        {
            if (args[0] == "gui") GUIRuleSets(player);
        }
        else
        {
            Message(player, "enableset", enabled.ToString());
        }
    }

    [Command("pvelog")]
    private void CmdNextGenPVElog(BasePlayer player, string command, string[] args)
    {
        if (!Permissions.UserHasPermission(player.displayName, permNextGenPVEAdmin)) { Message(player, "notauthorized"); return; }

        dolog = !dolog;
        Message(player, "logging", dolog.ToString());
    }

    [Command("pvedebug")]
    private void CmdNextGenPVEDebug(BasePlayer player, string command, string[] args)
    {
        if (!Permissions.UserHasPermission(player.displayName, permNextGenPVEAdmin)) { Message(player, "notauthorized"); return; }

        configData.Options.debug = !configData.Options.debug;
        SaveConfig(configData);
        Message(player, "debug", configData.Options.debug.ToString());
    }

    [Command("pverule")]
    private void CmdNextGenPVEGUI(BasePlayer player, string command, string[] args)
    {
        if (!Permissions.UserHasPermission(player.displayName, permNextGenPVEAdmin)) { Message(player, "notauthorized"); return; }

        if (args.Length > 0)
        {
            if (configData.Options.debug)
            {
                string debug = string.Join(",", args); Utils.DoLog($"{debug}");
            }

            switch (args[0])
            {
                case "list":
                    LMessage(player, "nextgenpverulesets");
                    using (SqliteConnection c = new SqliteConnection(connStr))
                    {
                        c.Open();
                        using (SqliteCommand checkrs = new SqliteCommand($"SELECT DISTINCT name, enabled, zone FROM ngpve_rulesets", c))
                        {
                            using (SqliteDataReader crs = checkrs.ExecuteReader())
                            {
                                while (crs.Read())
                                {
                                    string rs = crs.GetValue(0).ToString();
                                    string en = crs.GetBoolean(1).ToString();
                                    string zone = crs.GetValue(2).ToString();
                                    if (zone == "0" || zone?.Length == 0 || zone == "default") zone = Lang("default");
                                    if (zone != "") zone = Lang("zone") + ": " + zone;
                                    if (rs != "")
                                    {
                                        LMessage(player, "\n" + rs + ", " + Lang("enabled") + ": " + en + ", " + zone + "\n");
                                    }
                                }
                            }
                        }
                    }
                    break;
                case "dump":
                    if (args.Length > 1)
                    {
                        string output = "";
                        string zone = "";
                        string rules = "";
                        List<string> src = new List<string>();
                        List<string> tgt = new List<string>();
                        using (SqliteConnection c = new SqliteConnection(connStr))
                        {
                            c.Open();
                            using (SqliteCommand d = new SqliteCommand($"SELECT DISTINCT * FROM ngpve_rulesets WHERE name='{args[1]}'", c))
                            {
                                using (SqliteDataReader re = d.ExecuteReader())
                                {
                                    while (re.Read())
                                    {
                                        //(name VARCHAR(255), damage INTEGER(1) DEFAULT 0, enabled INTEGER(1) DEFAULT 1,
                                        //automated INTEGER(1) DEFAULT 0, zone VARCHAR(255), exception VARCHAR(255),
                                        //src_exclude VARCHAR(255), tgt_exclude VARCHAR(255), schedule VARCHAR(255))
                                        output = Lang("nextgenpveruleset") + ": " + re.GetValue(0).ToString() + ", " + Lang("defaultdamage") + ": " + re.GetBoolean(1).ToString() + ", " + Lang("enabled") + ": " + re.GetBoolean(2).ToString();
                                        zone = re.GetValue(4).ToString();
                                        if (zone == "0" || zone?.Length == 0 || zone == "default") zone = Lang("default");
                                        if (zone != "") zone = ", " + Lang("zone") + ": " + zone;
                                        rules += re.GetValue(5).ToString() + "\n\t";
                                        string s = re.GetValue(6).ToString();
                                        string t = re.GetValue(7).ToString();
                                        if (s != "" && !src.Contains(s))
                                        {
                                            src.Add(s);
                                        }
                                        if (t != "" && !tgt.Contains(t))
                                        {
                                            tgt.Add(t);
                                        }
                                    }
                                }
                            }
                        }
                        output += zone + "\n" + Lang("damageexceptions") + ":\n\t" + rules;
                        if (src.Count > 0)
                        {
                            output += "\n" + Lang("sourceexcl") + ":\n\t" + string.Join("\n\t", src);
                        }
                        if (tgt.Count > 0)
                        {
                            output += "\n" + Lang("targetexcl") + ":\n\t" + string.Join("\n\t", tgt);
                        }
                        LMessage(player, output);
                    }
                    break;
                //case "backup":
                //    string backupfile = "nextgenpve_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".db";
                //    if (args.Length > 1)
                //    {
                //        backupfile = args[1] + ".db";
                //    }
                //    using (SqliteConnection c = new SqliteConnection(connStr))
                //    {
                //        c.Open();
                //        string bkup = $"Data Source={data.Directory}{Path.DirectorySeparatorChar}{Name}{Path.DirectorySeparatorChar}{backupfile};";
                //        using (SqliteConnection d = new SqliteConnection(bkup))
                //        {
                //            d.Open();
                //            c.BackupDatabase(d, "main", "main", -1, null, -1);
                //        }
                //        Message(player, "BackupDone", backupfile);
                //    }
                //    break;
                //case "restore":
                //    string[] files = data.GetFiles($"{data.Directory}/{Name}");
                //    files = Array.FindAll(files, x => x.EndsWith(".db"));
                //    files = Array.FindAll(files, x => !x.EndsWith("nextgenpve.db"));

                //    if (args.Length > 1)
                //    {
                //        string restorefile = $"{Interface.Oxide.DataDirectory}{Path.DirectorySeparatorChar}{Name}{Path.DirectorySeparatorChar}{args[1]}";
                //        if (files.Contains(restorefile) && restorefile.EndsWith(".db"))
                //        {
                //            using (SqliteConnection c = new SqliteConnection(connStr))
                //            {
                //                c.Open();
                //                string target = $"{Interface.Oxide.DataDirectory}/{Name}/nexgenpve.db";
                //                string restore = $"Data Source={restorefile}";
                //                using (SqliteConnection d = new SqliteConnection(restore))
                //                {
                //                    try
                //                    {
                //                        d.Open();
                //                        d.BackupDatabase(c, "main", "main", -1, null, -1);
                //                        sqlConnection = new SqliteConnection(connStr);
                //                        sqlConnection.Open();
                //                        Message(player, "RestoreDone", restorefile);
                //                    }
                //                    catch
                //                    {
                //                        Message(player, "RestoreFailed", restorefile);
                //                    }
                //                }
                //            }
                //        }
                //        else
                //        {
                //            Message(player, "RestoreFilename", restorefile);
                //        }
                //    }
                //    else
                //    {
                //        Message(player, "RestoreFilename");
                //        Message(player, "RestoreAvailable", string.Join("\n\t", files.Select(x => x.Replace($"{Interface.Oxide.DataDirectory}/{Name}/", ""))));
                //    }
                //    break;
                case "editnpcset":
                    bool bval = GetBoolValue(args[2]);
                    using (SqliteConnection c = new SqliteConnection(connStr))
                    {
                        c.Open();
                        switch (bval)
                        {
                            case true:
                                using (SqliteCommand en = new SqliteCommand($"INSERT OR REPLACE INTO ngpve_targetexclusion VALUES('{args[1]}', 1)", c))
                                {
                                    en.ExecuteNonQuery();
                                }
                                break;
                            case false:
                                using (SqliteCommand en = new SqliteCommand($"DELETE FROM ngpve_targetexclusion WHERE name='{args[1]}'", c))
                                {
                                    en.ExecuteNonQuery();
                                }
                                break;
                        }
                    }
                    GUISelectNPCTypes(player);
                    break;
                case "editnpc":
                    GUISelectNPCTypes(player);
                    break;
                case "customgui":
                    GUICustomSelect(player);
                    break;
                case "customrules":
                    GUISelectRule(player, null, true);
                    break;
                case "allentities":
                    {
                        string search = "";
                        string coll = "all";
                        if (args.Length > 2 && args[1] == "coll")
                        {
                            coll = args[2];
                        }
                        else if (args.Length > 1)
                        {
                            search = args[1];
                        }
                        GUISelectEntity(player, coll, search);
                    }
                    break;
                case "unknownentities":
                    {
                        string search = "";
                        if (args.Length > 1)
                        {
                            search = args[1];
                        }
                        GUISelectEntity(player, "unknown", search);
                    }
                    break;
                case "addcustomrule":
                    GUIRuleEditor(player, null);
                    break;
                case "editruleset":
                    //e.g.: pverule editruleset {rulesetname} damage 0
                    //      pverule editruleset {rulesetname} name {newname}
                    //      pverule editruleset {rulesetname} zone {zonename}
                    // This is where we actually make the edit.
                    if (args.Length > 3)
                    {
                        string rs = args[1];
                        string setting = args[2];
                        string newval = args[3];
                        CuiHelper.DestroyUi(player, NGPVEVALUEEDIT);
                        switch (setting)
                        {
                            case "defload":
                                rs = "default";
                                using (SqliteConnection c = new SqliteConnection(connStr))
                                {
                                    c.Open();
                                    using (SqliteCommand defload = new SqliteCommand("DELETE FROM ngpve_rulesets WHERE name='default'", c))
                                    {
                                        defload.ExecuteNonQuery();
                                    }
                                }
                                LoadDefaultRuleset(false);
                                break;
                            case "enable":
                                using (SqliteConnection c = new SqliteConnection(connStr))
                                {
                                    c.Open();
                                    using (SqliteCommand en = new SqliteCommand($"UPDATE ngpve_rulesets SET enabled='{newval}' WHERE name='{rs}'", c))
                                    {
                                        en.ExecuteNonQuery();
                                    }
                                }
                                break;
                            case "damage":
                                using (SqliteConnection c = new SqliteConnection(connStr))
                                {
                                    c.Open();
                                    using (SqliteCommand dm = new SqliteCommand($"UPDATE ngpve_rulesets SET damage='{newval}' WHERE name='{rs}'", c))
                                    {
                                        dm.ExecuteNonQuery();
                                    }
                                }
                                break;
                            case "invschedule":
                                using (SqliteConnection c = new SqliteConnection(connStr))
                                {
                                    c.Open();
                                    using (SqliteCommand dm = new SqliteCommand($"UPDATE ngpve_rulesets SET invschedule='{newval}' WHERE name='{rs}'", c))
                                    {
                                        dm.ExecuteNonQuery();
                                    }
                                }
                                RunSchedule(true);
                                break;
                            case "name":
                                using (SqliteConnection c = new SqliteConnection(connStr))
                                {
                                    c.Open();
                                    using (SqliteCommand nm = new SqliteCommand($"UPDATE ngpve_rulesets SET name='{newval}' WHERE name='{rs}'", c))
                                    {
                                        nm.ExecuteNonQuery();
                                        rs = newval;
                                    }
                                }
                                break;
                            case "zone":
                                if (newval == "delete")
                                {
                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SqliteCommand zup = new SqliteCommand($"UPDATE ngpve_rulesets SET zone='' WHERE name='{rs}'", c))
                                        {
                                            zup.ExecuteNonQuery();
                                        }
                                    }
                                }
                                else
                                {
                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SqliteCommand zu = new SqliteCommand($"UPDATE ngpve_rulesets SET zone='{newval}' WHERE name='{rs}'", c))
                                        {
                                            zu.ExecuteNonQuery();
                                        }
                                    }
                                }
                                break;
                            case "scheduleday":
                                string newparts = EditScheduleDay(rs, newval);
                                using (SqliteConnection c = new SqliteConnection(connStr))
                                {
                                    c.Open();
                                    using (SqliteCommand us = new SqliteCommand($"UPDATE ngpve_rulesets SET schedule='{newparts}' WHERE name='{rs}'", c))
                                    {
                                        us.ExecuteNonQuery();
                                    }
                                }
                                GUIRulesetEditor(player, rs);
                                GUIEditSchedule(player, rs);
                                RunSchedule(true);
                                return;
                            case "schedulestarthour":
                                {
                                    //            RS      setting           newval
                                    //editruleset,default,schedulestarthour,9
                                    string ns = EditScheduleHM(rs, newval, "sh");
                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SqliteCommand us = new SqliteCommand($"UPDATE ngpve_rulesets SET schedule='{ns}' WHERE name='{rs}'", c))
                                        {
                                            us.ExecuteNonQuery();
                                        }
                                    }
                                    GUIRulesetEditor(player, rs);
                                    GUIEditSchedule(player, rs);
                                    RunSchedule(true);
                                    return;
                                }
                            case "scheduleendhour":
                                {
                                    string ns = EditScheduleHM(rs, newval, "eh");
                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SqliteCommand us = new SqliteCommand($"UPDATE ngpve_rulesets SET schedule='{ns}' WHERE name='{rs}'", c))
                                        {
                                            us.ExecuteNonQuery();
                                        }
                                    }
                                    GUIRulesetEditor(player, rs);
                                    GUIEditSchedule(player, rs);
                                    RunSchedule(true);
                                    return;
                                }
                            case "schedulestartminute":
                                {
                                    string ns = EditScheduleHM(rs, newval, "sm");
                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SqliteCommand us = new SqliteCommand($"UPDATE ngpve_rulesets SET schedule='{ns}' WHERE name='{rs}'", c))
                                        {
                                            us.ExecuteNonQuery();
                                        }
                                    }
                                    GUIRulesetEditor(player, rs);
                                    GUIEditSchedule(player, rs);
                                    RunSchedule(true);
                                    return;
                                }
                            case "scheduleendminute":
                                {
                                    string ns = EditScheduleHM(rs, newval, "em");
                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SqliteCommand us = new SqliteCommand($"UPDATE ngpve_rulesets SET schedule='{ns}' WHERE name='{rs}'", c))
                                        {
                                            us.ExecuteNonQuery();
                                        }
                                    }
                                    GUIRulesetEditor(player, rs);
                                    GUIEditSchedule(player, rs);
                                    RunSchedule(true);
                                    return;
                                }
                            case "schedule":
                                if (newval == "0")
                                {
                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SqliteCommand use = new SqliteCommand($"UPDATE ngpve_rulesets SET schedule='' WHERE name='{rs}'", c))
                                        {
                                            use.ExecuteNonQuery();
                                        }
                                    }
                                }
                                else
                                {
                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SqliteCommand us = new SqliteCommand($"UPDATE ngpve_rulesets SET schedule='{newval}' WHERE name='{rs}'", c))
                                        {
                                            us.ExecuteNonQuery();
                                        }
                                    }
                                }
                                RunSchedule(true);
                                break;
                            case "except":
                                if (args.Length < 5) return;
                                //pverule editruleset {rulesetname} except {pverule.Key} add
                                //pverule editruleset noturret except npcturret_animal delete
                                switch (args[4])
                                {
                                    case "add":
                                        //Utils.DoLog($"SELECT damage, exception FROM ngpve_rulesets WHERE name='{rs}' AND exception='{args[3]}'");
                                        bool isNew = true;
                                        bool damage = false;
                                        using (SqliteConnection c = new SqliteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SqliteCommand ce = new SqliteCommand($"SELECT DISTINCT damage FROM ngpve_rulesets WHERE name='{rs}'", c))
                                            {
                                                using (SqliteDataReader re = ce.ExecuteReader())
                                                {
                                                    while (re.Read())
                                                    {
                                                        damage = re.GetBoolean(0);
                                                    }
                                                }
                                            }
                                        }
                                        using (SqliteConnection c = new SqliteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SqliteCommand ce = new SqliteCommand($"SELECT exception FROM ngpve_rulesets WHERE name='{rs}' AND exception='{args[3]}'", c))
                                            {
                                                using (SqliteDataReader re = ce.ExecuteReader())
                                                {
                                                    while (re.Read())
                                                    {
                                                        isNew = false;
                                                    }
                                                }
                                            }
                                        }
                                        if (isNew)
                                        {
                                            //Utils.DoLog($"INSERT INTO ngpve_rulesets VALUES('{rs}', 1, 1, 0, '', '{args[3]}', '', '')");
                                            string dmg = damage ? "1" : "0";
                                            using (SqliteConnection c = new SqliteConnection(connStr))
                                            {
                                                c.Open();
                                                using (SqliteCommand ae = new SqliteCommand($"INSERT INTO ngpve_rulesets VALUES('{rs}', {dmg}, 1, 0, '', '{args[3]}', '', '', 0, 0)", c))
                                                {
                                                    ae.ExecuteNonQuery();
                                                }
                                            }
                                        }
                                        break;
                                    case "delete":
                                        using (SqliteConnection c = new SqliteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SqliteCommand ad = new SqliteCommand($"DELETE FROM ngpve_rulesets WHERE name='{rs}' AND exception='{args[3]}'", c))
                                            {
                                                ad.ExecuteNonQuery();
                                            }
                                        }
                                        break;
                                }
                                break;
                            case "src_exclude":
                                if (args.Length < 5) return;
                                CuiHelper.DestroyUi(player, NGPVERULEEXCLUSIONS);
                                //pverule editruleset {rulesetname} src_exclude Horse add
                                switch (args[4])
                                {
                                    case "add":
                                        string etype = null;
                                        using (SqliteConnection c = new SqliteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SqliteCommand aee = new SqliteCommand($"SELECT DISTINCT name FROM ngpve_entities WHERE type='{newval}'", c))
                                            {
                                                using (SqliteDataReader aed = aee.ExecuteReader())
                                                {
                                                    while (aed.Read())
                                                    {
                                                        etype = aed.GetString(0) ?? "";
                                                        //Utils.DoLog($"Found type {etype} of {newval}");
                                                    }
                                                }
                                            }
                                        }

                                        if (etype != "")
                                        {
                                            string exception = ""; string oldsrc = "";
                                            using (SqliteConnection c = new SqliteConnection(connStr))
                                            {
                                                c.Open();
                                                using (SqliteCommand cex = new SqliteCommand($"SELECT exception, src_exclude FROM ngpve_rulesets WHERE name='{rs}' AND exception LIKE '{etype}_%'", c))
                                                {
                                                    SqliteDataReader rex = cex.ExecuteReader();
                                                    while (rex.Read())
                                                    {
                                                        exception = rex.GetValue(0).ToString();
                                                        oldsrc = rex.GetValue(1).ToString();
                                                        //Utils.DoLog($"Found existing exception '{exception}' and src_exclude of '{oldsrc}'");
                                                    }
                                                }
                                            }
                                            if (exception != "" && oldsrc != "")
                                            {
                                                if (!oldsrc.Contains(newval))
                                                {
                                                    string newsrc = string.Join(",", oldsrc, newval);
                                                    //Utils.DoLog($"Adding src_exclude of '{newval}' to ruleset: '{rs}' type: '{etype}' - Input was {newval}, oldsrc = '{oldsrc}'");
                                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                                    {
                                                        c.Open();
                                                        //Utils.DoLog($"UPDATE ngpve_rulesets SET src_exclude='{newsrc}' WHERE name='{rs}' AND exception LIKE '{etype}_%'");
                                                        using (SqliteCommand aes = new SqliteCommand($"UPDATE ngpve_rulesets SET src_exclude='{newsrc}' WHERE name='{rs}' AND exception LIKE '{etype}_%'", c))
                                                        {
                                                            aes.ExecuteNonQuery();
                                                        }
                                                    }
                                                }
                                            }
                                            else if (exception != "")
                                            {
                                                if (!oldsrc.Contains(newval))
                                                {
                                                    //Utils.DoLog($"Updating src_exclude of '{newval}' to ruleset: '{rs}' type: '{etype}' - Input was {newval}, oldsrc = '{oldsrc}'");
                                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                                    {
                                                        c.Open();
                                                        //Utils.DoLog($"UPDATE ngpve_rulesets SET src_exclude='{newval}' WHERE name='{rs}' AND exception LIKE '{etype}_%'");
                                                        using (SqliteCommand aes = new SqliteCommand($"UPDATE ngpve_rulesets SET src_exclude='{newval}' WHERE name='{rs}' AND exception LIKE '{etype}_%'", c))
                                                        {
                                                            aes.ExecuteNonQuery();
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    case "delete":
                                        bool foundSrc = false;
                                        string src_excl = "";
                                        using (SqliteConnection c = new SqliteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SqliteCommand findSrc = new SqliteCommand($"SELECT DISTINCT src_exclude FROM ngpve_rulesets WHERE src_exclude = '{newval}'", c))
                                            {
                                                using (SqliteDataReader fs = findSrc.ExecuteReader())
                                                {
                                                    while (fs.Read())
                                                    {
                                                        foundSrc = true;
                                                        src_excl = fs.GetString(0) ?? "";
                                                        //Utils.DoLog($"Found src_exclude {src_excl}");
                                                    }
                                                }
                                            }
                                        }

                                        if (foundSrc)
                                        {
                                            string newsrc = src_excl.Replace(newval, "");
                                            newsrc.Replace(",,", ",");
                                            if (newsrc.Trim() == "," || newsrc.Trim() == ",,") newsrc = "";
                                            using (SqliteConnection c = new SqliteConnection(connStr))
                                            {
                                                c.Open();
                                                //Utils.DoLog($"UPDATE ngpve_rulesets SET src_exclude='{newsrc}' WHERE name='{rs}' AND src_exclude='{src_excl}'");
                                                using (SqliteCommand ads = new SqliteCommand($"UPDATE ngpve_rulesets SET src_exclude='{newsrc}' WHERE name='{rs}' AND src_exclude='{src_excl}'", c))
                                                {
                                                    ads.ExecuteNonQuery();
                                                }
                                            }
                                        }
                                        break;
                                }
                                break;
                            case "tgt_exclude":
                                if (args.Length < 5) return;
                                CuiHelper.DestroyUi(player, NGPVERULEEXCLUSIONS);
                                //pverule editruleset {rulesetname} tgt_exclude Horse delete
                                switch (args[4])
                                {
                                    case "add":
                                        string etype = null;
                                        using (SqliteConnection c = new SqliteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SqliteCommand aee = new SqliteCommand($"SELECT DISTINCT name FROM ngpve_entities WHERE type='{newval}'", c))
                                            {
                                                using (SqliteDataReader aed = aee.ExecuteReader())
                                                {
                                                    while (aed.Read())
                                                    {
                                                        etype = aed.GetString(0) ?? "";
                                                        //Utils.DoLog($"Found type {etype} of {newval}");
                                                    }
                                                }
                                            }
                                        }

                                        if (etype != "")
                                        {
                                            string exception = ""; string oldtgt = "";
                                            using (SqliteConnection c = new SqliteConnection(connStr))
                                            {
                                                c.Open();
                                                using (SqliteCommand cex = new SqliteCommand($"SELECT exception, tgt_exclude FROM ngpve_rulesets WHERE name='{rs}' AND exception LIKE '%_{etype}'", c))
                                                {
                                                    SqliteDataReader rex = cex.ExecuteReader();
                                                    while (rex.Read())
                                                    {
                                                        exception = rex.GetValue(0).ToString();
                                                        oldtgt = rex.GetValue(1).ToString();
                                                        //Utils.DoLog($"Found existing exception {exception} and tgt_exclude of {oldtgt}");
                                                    }
                                                }
                                            }
                                            if (exception != "" && oldtgt != "")
                                            {
                                                if (!oldtgt.Contains(newval))
                                                {
                                                    string newtgt = string.Join(",", oldtgt, newval);
                                                    //Utils.DoLog($"Adding tgt_exclude of '{newtgt}' to ruleset: '{rs}', type: '{etype}' - Input was {newval}");
                                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                                    {
                                                        c.Open();
                                                        //Utils.DoLog($"UPDATE ngpve_rulesets SET tgt_exclude='{newtgt}' WHERE name='{rs}' AND exception LIKE '%_{etype}'");
                                                        using (SqliteCommand aes = new SqliteCommand($"UPDATE ngpve_rulesets SET tgt_exclude='{newtgt}' WHERE name='{rs}' AND exception LIKE '%_{etype}'", c))
                                                        {
                                                            aes.ExecuteNonQuery();
                                                        }
                                                    }
                                                }
                                            }
                                            else if (exception != "")
                                            {
                                                if (!oldtgt.Contains(newval))
                                                {
                                                    //Utils.DoLog($"Updating tgt_exclude of '{newval}' to ruleset: '{rs}' type: '{etype}' - Input was {newval}");
                                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                                    {
                                                        c.Open();
                                                        //Utils.DoLog($"UPDATE ngpve_rulesets SET tgt_exclude='{newval}' WHERE name='{rs}' AND exception LIKE '%_{etype}'");
                                                        using (SqliteCommand aes = new SqliteCommand($"UPDATE ngpve_rulesets SET tgt_exclude='{newval}' WHERE name='{rs}' AND exception LIKE '%_{etype}'", c))
                                                        {
                                                            aes.ExecuteNonQuery();
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    case "delete":
                                        bool foundTgt = false;
                                        string tgt_excl = "";
                                        using (SqliteConnection c = new SqliteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SqliteCommand findTgt = new SqliteCommand($"SELECT DISTINCT tgt_exclude FROM ngpve_rulesets WHERE tgt_exclude = '{newval}'", c))
                                            {
                                                using (SqliteDataReader ft = findTgt.ExecuteReader())
                                                {
                                                    while (ft.Read())
                                                    {
                                                        foundTgt = true;
                                                        tgt_excl = ft.GetString(0) ?? "";
                                                        //Utils.DoLog($"Found tgt_exclude {tgt_excl}");
                                                    }
                                                }
                                            }
                                        }

                                        if (foundTgt)
                                        {
                                            string newtgt = tgt_excl.Replace(newval, "");
                                            newtgt.Replace(",,", ",");
                                            if (newtgt.Trim() == "," || newtgt.Trim() == ",,") newtgt = "";
                                            using (SqliteConnection c = new SqliteConnection(connStr))
                                            {
                                                c.Open();
                                                CuiHelper.DestroyUi(player, NGPVECUSTOMSELECT);
                                                //Utils.DoLog($"UPDATE ngpve_rulesets SET tgt_exclude='{newtgt}' WHERE name='{rs}' AND tgt_exclude='{tgt_excl}'");
                                                using (SqliteCommand ads = new SqliteCommand($"UPDATE ngpve_rulesets SET tgt_exclude='{newtgt}' WHERE name='{rs}' AND tgt_exclude='{tgt_excl}'", c))
                                                {
                                                    ads.ExecuteNonQuery();
                                                }
                                            }
                                        }
                                        break;
                                }
                                break;
                            case "flags":
                                // AND the inbound flag and the existing flags, rewrite. FIXME
                                break;
                        }
                        GUIRuleSets(player);
                        GUIRulesetEditor(player, rs);
                    }
                    //pverule editruleset {rulesetname} delete
                    //pverule editruleset {rulesetname} zone
                    //pverule editruleset new src_exclude add
                    // This is where we either delete or load the dialog to edit values.
                    else if (args.Length > 2)
                    {
                        switch (args[2])
                        {
                            case "delete":
                                using (SqliteConnection c = new SqliteConnection(connStr))
                                {
                                    c.Open();
                                    using (SqliteCommand drs = new SqliteCommand($"DELETE FROM ngpve_rulesets WHERE name='{args[1]}'", c))
                                    {
                                        drs.ExecuteNonQuery();
                                    }
                                }
                                CuiHelper.DestroyUi(player, NGPVEEDITRULESET);
                                GUIRuleSets(player);
                                break;
                            case "name":
                            case "zone":
                                GUIEditValue(player, args[1], args[2]);
                                break;
                            case "schedule":
                                GUIEditSchedule(player, args[1]);
                                break;
                            case "except":
                                GUISelectRule(player, args[1]);
                                break;
                            case "src_exclude":
                            case "tgt_exclude":
                                GUISelectExclusion(player, args[1], args[2]);
                                break;
                            case "clone":
                                int id = 0;
                                string oldname = args[1];
                                string clone = oldname + "1";
                                while (true)
                                {
                                    using (SqliteConnection c = new SqliteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SqliteCommand checkrs = new SqliteCommand($"SELECT DISTINCT name FROM ngpve_rulesets WHERE name='{clone}'", c))
                                        {
                                            using (SqliteDataReader crs = checkrs.ExecuteReader())
                                            {
                                                while (crs.Read())
                                                {
                                                    id++;
                                                    if (id > 4) break;
                                                    clone = oldname + id.ToString();
                                                }
                                            }
                                        }
                                    }
                                    break;
                                }
                                //Utils.DoLog($"Creating clone {clone}");
                                using (SqliteConnection c = new SqliteConnection(connStr))
                                {
                                    c.Open();
                                    using (SqliteCommand newrs = new SqliteCommand($"INSERT INTO ngpve_rulesets (name, damage, enabled, automated, zone, exception, src_exclude, tgt_exclude, schedule) SELECT '{clone}', damage, enabled, automated, zone, exception, src_exclude, tgt_exclude, schedule FROM ngpve_rulesets WHERE name='{oldname}'", c))
                                    {
                                        newrs.ExecuteNonQuery();
                                    }
                                    // Disable the clone for now...
                                    using (SqliteCommand newrs = new SqliteCommand($"UPDATE ngpve_rulesets SET enabled='0' WHERE name = '{clone}'", c))
                                    {
                                        newrs.ExecuteNonQuery();
                                    }
                                }
                                GUIRuleSets(player);
                                GUIRulesetEditor(player, clone);
                                break;
                        }
                    }
                    //pverule editruleset {rulesetname}
                    else if (args.Length > 1)
                    {
                        string newname = args[1];
                        if (newname == "add")
                        {
                            int id = 0;
                            newname = "new";
                            while (true)
                            {
                                using (SqliteConnection c = new SqliteConnection(connStr))
                                {
                                    c.Open();
                                    using (SqliteCommand addrs = new SqliteCommand($"SELECT DISTINCT name FROM ngpve_rulesets WHERE name='{newname}'", c))
                                    {
                                        using (SqliteDataReader addcrsd = addrs.ExecuteReader())
                                        {
                                            while (addcrsd.Read())
                                            {
                                                id++;
                                                if (id > 4) break;
                                                newname = "new" + id.ToString();
                                            }
                                        }
                                    }
                                }
                                break;
                            }
                            //Utils.DoLog($"Creating new ruleset {newname}");
                            using (SqliteConnection c = new SqliteConnection(connStr))
                            {
                                c.Open();
                                // Add this new ruleset as disabled
                                using (SqliteCommand newrs = new SqliteCommand($"INSERT INTO ngpve_rulesets VALUES ('{newname}', 0, 0, 0, 0, '', '', '', 0, 0)", c))
                                {
                                    newrs.ExecuteNonQuery();
                                }
                            }
                            GUIRuleSets(player);
                        }

                        GUIRulesetEditor(player, newname);
                    }
                    break;
                case "editconfig":
                    string cfg = args[1];
                    bool val = GetBoolValue(args[2]);
                    switch (cfg)
                    {
                        case "NPCAutoTurretTargetsPlayers":
                            configData.Options.NPCAutoTurretTargetsPlayers = val;
                            break;
                        case "HeliTurretTargetsPlayers":
                            configData.Options.HeliTurretTargetsPlayers = val;
                            break;
                        case "NPCAutoTurretTargetsNPCs":
                            configData.Options.NPCAutoTurretTargetsNPCs = val;
                            break;
                        case "AutoTurretTargetsPlayers":
                            configData.Options.AutoTurretTargetsPlayers = val;
                            break;
                        case "AutoTurretTargetsNPCs":
                            configData.Options.AutoTurretTargetsNPCs = val;
                            break;
                        case "NPCSamSitesIgnorePlayers":
                            configData.Options.NPCSamSitesIgnorePlayers = val;
                            break;
                        case "SamSitesIgnorePlayers":
                            configData.Options.SamSitesIgnorePlayers = val;
                            break;
                        case "AllowSuicide":
                            configData.Options.AllowSuicide = val;
                            break;
                        case "AllowFriendlyFire":
                            configData.Options.AllowFriendlyFire = val;
                            if (val) configData.Options.HonorRelationships = true;
                            break;
                        case "TrapsIgnorePlayers":
                            configData.Options.TrapsIgnorePlayers = val;
                            break;
                        case "HonorBuildingPrivilege":
                            configData.Options.HonorBuildingPrivilege = val;
                            break;
                        case "UnprotectedBuildingDamage":
                            configData.Options.UnprotectedBuildingDamage = val;
                            break;
                        case "TwigDamage":
                            configData.Options.TwigDamage = val;
                            break;
                        case "HonorRelationships":
                            configData.Options.HonorRelationships = val;
                            if (!val) configData.Options.AllowFriendlyFire = false;
                            break;
                        case "BlockScrapHeliFallDamage":
                            configData.Options.BlockScrapHeliFallDamage = val;
                            break;
                        case "RESET":
                            LoadDefaultFlags();
                            //LoadConfigVariables();
                            break;
                    }
                    SaveConfig(configData);
                    GUIRuleSets(player);
                    break;
                case "addrule":
                case "editrule":
                    string rn = args[1];
                    if (args[0] == "editrule" && rn != null)
                    {
                        if (args.Length > 3)
                        {
                            string mode = args[2];
                            string mod = args[3];

                            //edit
                            using (SqliteConnection c = new SqliteConnection(connStr))
                            {
                                c.Open();
                                using (SqliteCommand cmd = new SqliteCommand($"UPDATE ngpve_rules SET {mode}='{mod}', damage=1 WHERE name='{rn}'", c))
                                {
                                    cmd.ExecuteNonQuery();
                                }
                            }
                        }
                    }
                    else if (args[0] == "addrule" && rn != null)
                    {
                        using (SqliteConnection c = new SqliteConnection(connStr))
                        {
                            c.Open();
                            using (SqliteCommand cmd = new SqliteCommand($"INSERT INTO ngpve_rules VALUES ('{rn}','', 1, 1, null, null)", c))
                            {
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    GUIRuleEditor(player, rn);
                    break;
                case "deleterule":
                    if (args.Length > 1)
                    {
                        using (SqliteConnection c = new SqliteConnection(connStr))
                        {
                            c.Open();
                            using (SqliteCommand cmd = new SqliteCommand($"DELETE FROM ngpve_rules WHERE name='{args[1]}'", c))
                            {
                                cmd.ExecuteNonQuery();
                            }
                        }
                        GUISelectRule(player, null, true);
                    }
                    else
                    {
                        CuiHelper.DestroyUi(player, NGPVERULEEDIT);
                    }
                    break;
                case "closecustom":
                    CuiHelper.DestroyUi(player, NGPVECUSTOMSELECT);
                    break;
                case "close":
                    IsOpen(player.userID, false);
                    CuiHelper.DestroyUi(player, NGPVERULELIST);
                    CuiHelper.DestroyUi(player, NGPVERULEEDIT);
                    CuiHelper.DestroyUi(player, NGPVEVALUEEDIT);
                    CuiHelper.DestroyUi(player, NGPVESCHEDULEEDIT);
                    CuiHelper.DestroyUi(player, NGPVEEDITRULESET);
                    CuiHelper.DestroyUi(player, NGPVEENTSELECT);
                    CuiHelper.DestroyUi(player, NGPVENPCSELECT);
                    CuiHelper.DestroyUi(player, NGPVERULESELECT);
                    CuiHelper.DestroyUi(player, NGPVECUSTOMSELECT);
                    break;
                case "closeexclusions":
                    CuiHelper.DestroyUi(player, NGPVERULEEXCLUSIONS);
                    break;
                case "editent":
                    string entname = args[1];
                    if (args.Length > 2)
                    {
                        string newcat = args[2];
                        if (newcat == "DELETE")
                        {
                            using (SqliteConnection c = new SqliteConnection(connStr))
                            {
                                c.Open();
                                using (SqliteCommand cmd = new SqliteCommand($"DELETE FROM ngpve_entities WHERE type='{entname}'", c))
                                {
                                    cmd.ExecuteNonQuery();
                                }
                            }
                        }
                        using (SqliteConnection c = new SqliteConnection(connStr))
                        {
                            c.Open();
                            using (SqliteCommand cmd = new SqliteCommand($"UPDATE ngpve_entities SET name = '{newcat}' WHERE type='{entname}'", c))
                            {
                                cmd.ExecuteNonQuery();
                            }
                        }
                        CuiHelper.DestroyUi(player, NGPVEENTEDIT);
                        GUISelectEntity(player);
                    }
                    else
                    {
                        GUIEditEntity(player, entname);
                    }
                    break;
                case "closeentedit":
                    CuiHelper.DestroyUi(player, NGPVEENTEDIT);
                    break;
                case "closenpcselect":
                    CuiHelper.DestroyUi(player, NGPVENPCSELECT);
                    break;
                case "closeentselect":
                    CuiHelper.DestroyUi(player, NGPVEENTSELECT);
                    CuiHelper.DestroyUi(player, NGPVECUSTOMSELECT);
                    break;
                case "closeruleselect":
                    CuiHelper.DestroyUi(player, NGPVERULESELECT);
                    CuiHelper.DestroyUi(player, NGPVECUSTOMSELECT);
                    break;
                case "closeruleset":
                    CuiHelper.DestroyUi(player, NGPVEEDITRULESET);
                    break;
                case "closerule":
                    CuiHelper.DestroyUi(player, NGPVERULEEDIT);
                    break;
                case "closerulevalue":
                    CuiHelper.DestroyUi(player, NGPVEVALUEEDIT);
                    break;
                case "closeruleschedule":
                    CuiHelper.DestroyUi(player, NGPVESCHEDULEEDIT);
                    GUIRuleSets(player);
                    break;
                default:
                    GUIRuleSets(player);
                    break;
            }
        }
        else
        {
            if (isopen.Contains(player.userID)) return;
            GUIRuleSets(player);
        }
    }
    #endregion

    #region config
    private void LoadConfigVariables()
    {
        configData = config.ReadObject<ConfigData>();

        if (configData.Version < new VersionNumber(1, 1, 1))
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_fire', 'Player can damage Fire', 1, 0, 'player', 'fire')", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_fire', null, null, null, null)", c))
                {
                    ct.ExecuteNonQuery();
                }
            }
        }

        if (configData.Version < new VersionNumber(1, 1, 3))
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'FrankensteinPet', 0)", c))
                {
                    ct.ExecuteNonQuery();
                }
            }
        }

        if (configData.Version < new VersionNumber(1, 1, 4))
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand ct = new SqliteCommand("DELETE FROM ngpve_entities WHERE type='MLRS'", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT OR REPLACE INTO ngpve_entities VALUES('mlrs', 'MLRS', 0)", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('mlrs_building', 'MLRS can damage Building', 1, 0, 'mlrs', 'building')", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('mlrs_npc', 'MLRS can damage NPC', 1, 0, 'mlrs', 'npc')", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('mlrs_player', 'MLRS can damage Player', 1, 0, 'mlrs', 'player')", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('mlrs_resource', 'MLRS can damage Resource', 1, 0, 'mlrs', 'resource')", c))
                {
                    ct.ExecuteNonQuery();
                }
            }
        }

        if (configData.Version < new VersionNumber(1, 1, 8))
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'ZombieNPC', 0)", c))
                {
                    ct.ExecuteNonQuery();
                }
            }
        }
        if (configData.Version < new VersionNumber(1, 1, 9))
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();

                using (SqliteCommand ct = new SqliteCommand("DELETE FROM ngpve_entities WHERE type='NPCPlayerApex'", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("DELETE FROM ngpve_entities WHERE type='NPCPlayer'", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT OR REPLACE INTO ngpve_entities VALUES('npc', 'NPCPlayer', 0)", c))
                {
                    ct.ExecuteNonQuery();
                }
            }
        }

        if (configData.Version < new VersionNumber(1, 2, 0))
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();

                using (SqliteCommand ct = new SqliteCommand("DELETE FROM ngpve_entities WHERE type='ScientistNPCNew'", c))
                {
                    ct.ExecuteNonQuery();
                }
            }
        }

        if (configData.Version < new VersionNumber(1, 2, 2))
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();

                using (SqliteCommand ct = new SqliteCommand("DELETE FROM ngpve_entities WHERE type='NpcRaider'", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT OR REPLACE INTO ngpve_entities VALUES('npc', 'NpcRaider', 0)", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT OR REPLACE INTO ngpve_entities VALUES('resource', 'AdventCalendar', 0)", c))
                {
                    ct.ExecuteNonQuery();
                }
            }
        }

        if (configData.Version < new VersionNumber(1, 2, 3))
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();

                using (SqliteCommand ct = new SqliteCommand("DELETE FROM ngpve_entities WHERE type='ScarecrowNPC'", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT OR REPLACE INTO ngpve_entities VALUES('npc', 'ScarecrowNPC', 0)", c))
                {
                    ct.ExecuteNonQuery();
                }
            }
        }

        if (configData.Version < new VersionNumber(1, 2, 4))
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();

                using (SqliteCommand ct = new SqliteCommand("DELETE FROM ngpve_entities WHERE type='Polarbear'", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT OR REPLACE INTO ngpve_entities VALUES('animal', 'Polarbear', 0)", c))
                {
                    ct.ExecuteNonQuery();
                }
            }
        }

        if (configData.Version < new VersionNumber(1, 2, 8))
        {
            configData.Options.purgeEnabled = false;
            configData.Options.purgeStartMessage = "";
            configData.Options.purgeEndMessage = "";
            configData.Options.purgeStart = "12/31/1969 12:01";
            configData.Options.purgeEnd = "1/1/1970 14:20";
        }

        if (configData.Version < new VersionNumber(1, 3, 1))
        {
            configData.Options.autoCalcPurge = false;
            configData.Options.autoCalcPurgeDays = 2;
        }

        if (!CheckRelEnables()) configData.Options.HonorRelationships = false;

        configData.Version = Version;
        SaveConfig(configData);
    }

    private bool CheckRelEnables()
    {
        return configData.Options.useClans || configData.Options.useFriends || configData.Options.useTeams;
    }

    public void LoadDefaultConfig()
    {
        Utils.DoLog("Creating new config file.");
        ConfigData config = new ConfigData
        {
            Options = new Options()
            {
                protectedDays = 0f,
                useSchedule = false,
                useGUIAnnouncements = false,
                useMessageBroadcast = false,
                useRealTime = false,
                useFriends = false,
                useClans = false,
                useTeams = false,
                AllowCustomEdit = false,
                AllowDropDatabase = false,
                NPCAutoTurretTargetsPlayers = true,
                NPCAutoTurretTargetsNPCs = true,
                AutoTurretTargetsPlayers = false,
                HeliTurretTargetsPlayers = true,
                AutoTurretTargetsNPCs = false,
                NPCSamSitesIgnorePlayers = false,
                SamSitesIgnorePlayers = false,
                AllowSuicide = false,
                AllowFriendlyFire = false,
                TrapsIgnorePlayers = false,
                HonorBuildingPrivilege = true,
                UnprotectedBuildingDamage = false,
                UnprotectedDeployableDamage = false,
                TwigDamage = false,
                HonorRelationships = false,
                BlockScrapHeliFallDamage = false
            },
            Version = Version
        };
        SaveConfig(config);
    }

    private void SaveConfig(ConfigData configData)
    {
        config.WriteObject(configData, true);
    }

    private class ConfigData
    {
        public Options Options;
        public VersionNumber Version;
    }

    public class Options
    {
        public bool debug;
        public float protectedDays;
        public bool purgeEnabled;
        public string purgeStart;
        public string purgeEnd;
        public string purgeStartMessage;
        public string purgeEndMessage;
        public bool autoCalcPurge;
        public int autoCalcPurgeDays;
        public bool useSchedule;
        public bool useGUIAnnouncements;
        public bool useMessageBroadcast;
        public bool useRealTime;
        public bool useFriends;
        public bool useClans;
        public bool useTeams;
        public bool AllowCustomEdit;
        public bool AllowDropDatabase;

        public bool NPCAutoTurretTargetsPlayers;
        public bool NPCAutoTurretTargetsNPCs;
        public bool AutoTurretTargetsPlayers;
        public bool HeliTurretTargetsPlayers;
        public bool AutoTurretTargetsNPCs;
        public bool NPCSamSitesIgnorePlayers;
        public bool SamSitesIgnorePlayers;
        public bool AllowSuicide;
        public bool AllowFriendlyFire;
        public bool TrapsIgnorePlayers;
        public bool HonorBuildingPrivilege;
        public bool UnprotectedBuildingDamage;
        public bool UnprotectedDeployableDamage;
        public bool TwigDamage;
        public bool HonorRelationships;
        public bool BlockScrapHeliFallDamage;
    }

    protected void LoadDefaultFlags()
    {
        Utils.DoLog("Creating new config defaults.");
        configData.Options.NPCAutoTurretTargetsPlayers = true;
        configData.Options.NPCAutoTurretTargetsNPCs = true;
        configData.Options.AutoTurretTargetsPlayers = false;
        configData.Options.HeliTurretTargetsPlayers = true;
        configData.Options.AutoTurretTargetsNPCs = false;
        configData.Options.NPCSamSitesIgnorePlayers = false;
        configData.Options.SamSitesIgnorePlayers = false;
        configData.Options.AllowSuicide = false;
        configData.Options.AllowFriendlyFire = false;
        configData.Options.TrapsIgnorePlayers = false;
        configData.Options.HonorBuildingPrivilege = true;
        configData.Options.UnprotectedBuildingDamage = false;
        configData.Options.HonorRelationships = false;
        configData.Options.BlockScrapHeliFallDamage = false;
    }
    #endregion

    #region GUI
    private void IsOpen(ulong uid, bool set = false)
    {
        if (set)
        {
            if (configData.Options.debug) Utils.DoLog($"Setting isopen for {uid}");

            if (!isopen.Contains(uid)) isopen.Add(uid);
            return;
        }

        if (configData.Options.debug) Utils.DoLog($"Clearing isopen for {uid}");

        isopen.Remove(uid);
    }

    private void GUIRuleSets(BasePlayer player)
    {
        IsOpen(player.userID, true);
        CuiHelper.DestroyUi(player, NGPVERULELIST);

        CuiElementContainer container = UI.Container(NGPVERULELIST, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
        UI.Label(ref container, NGPVERULELIST, UI.Color("#ffffff", 1f), Lang("nextgenpverulesetsf"), 24, "0.2 0.92", "0.65 1");
        UI.Label(ref container, NGPVERULELIST, UI.Color("#d85540", 1f), Lang("standard"), 12, "0.66 0.95", "0.72 0.98");
        UI.Label(ref container, NGPVERULELIST, UI.Color("#5540d8", 1f), Lang("automated"), 12, "0.73 0.95", "0.79 0.98");

        if (configData.Options.AllowDropDatabase)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#ff0000", 1f), Lang("drop"), 12, "0.01 0.01", "0.18 0.05", "pvedrop gui");
        }
        if (configData.Options.AllowCustomEdit)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#d85540", 1f), Lang("customedit"), 12, "0.6 0.01", "0.69 0.05", "pverule customgui");
        }
        UI.Button(ref container, NGPVERULELIST, UI.Color("#d85540", 1f), Lang("editnpc"), 12, "0.7 0.01", "0.79 0.05", "pverule editnpc");
        UI.Button(ref container, NGPVERULELIST, UI.Color("#d85540", 1f), Lang("backup"), 12, "0.8 0.01", "0.9 0.05", "pverule backup");
        UI.Label(ref container, NGPVERULELIST, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.04");

        if (enabled)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("genabled"), 12, "0.8 0.95", "0.92 0.98", $"pveenable gui");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#ff2222", 1f), Lang("gdisabled"), 12, "0.8 0.95", "0.92 0.98", $"pveenable gui");
        }
        UI.Button(ref container, NGPVERULELIST, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule close");

        int col = 0;
        int row = 0;
        float[] pb = GetButtonPositionP(row, col);
        UI.Label(ref container, NGPVERULELIST, UI.Color("#ffffff", 1f), Lang("rulesets"), 14, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        row++;

        string rssql = " WHERE name='default'";
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand getrs = new SqliteCommand($"SELECT DISTINCT name, automated from ngpve_rulesets{rssql} ORDER BY name", c))
            {
                using (SqliteDataReader rsread = getrs.ExecuteReader())
                {
                    while (rsread.Read())
                    {
                        string rsname = rsread.GetString(0);
                        bool automated = rsread.GetBoolean(1);
                        if (row > 10)
                        {
                            row = 0;
                            col++;
                        }
                        pb = GetButtonPositionP(row, col);
                        string rColor = "#d85540";
                        if (automated) rColor = "#5540d8";

                        UI.Button(ref container, NGPVERULELIST, UI.Color(rColor, 1f), rsname, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rsname}");
                        row++;
                    }
                }
            }
        }

        pb = GetButtonPositionP(row, col);
        UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset add");

        row++;
        pb = GetButtonPositionP(row, col);
        UI.Label(ref container, NGPVERULELIST, UI.Color("#ffffff", 1f), Lang("zonemanagerreq"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) + .1)} {pb[3]}");

        row = 0; col = 6;
        pb = GetButtonPositionP(row, col);
        UI.Label(ref container, NGPVERULELIST, UI.Color("#ffffff", 1f), Lang("flags"), 14, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

        // Global flags
        col = 5; row--;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.AutoTurretTargetsPlayers)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("AutoTurretTargetsPlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig AutoTurretTargetsPlayers false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("AutoTurretTargetsPlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig AutoTurretTargetsPlayers true");
        }
        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.AutoTurretTargetsNPCs)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("AutoTurretTargetsNPCs"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig AutoTurretTargetsNPCs false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("AutoTurretTargetsNPCs"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig AutoTurretTargetsNPCs true");
        }
        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.BlockScrapHeliFallDamage)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("BlockScrapHeliFallDamage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig BlockScrapHeliFallDamage false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("BlockScrapHeliFallDamage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig BlockScrapHeliFallDamage true");
        }
        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.HeliTurretTargetsPlayers)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("HeliTurretTargetsPlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig HeliTurretTargetsPlayers false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("HeliTurretTargetsPlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig HeliTurretTargetsPlayers true");
        }
        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.NPCAutoTurretTargetsPlayers)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("NPCAutoTurretTargetsPlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig NPCAutoTurretTargetsPlayers false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("NPCAutoTurretTargetsPlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig NPCAutoTurretTargetsPlayers true");
        }
        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.NPCAutoTurretTargetsNPCs)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("NPCAutoTurretTargetsNPCs"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig NPCAutoTurretTargetsNPCs false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("NPCAutoTurretTargetsNPCs"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig NPCAutoTurretTargetsNPCs true");
        }

        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.NPCSamSitesIgnorePlayers)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("NPCSamSitesIgnorePlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig NPCSamSitesIgnorePlayers false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("NPCSamSitesIgnorePlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig NPCSamSitesIgnorePlayers true");
        }
        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.SamSitesIgnorePlayers)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("SamSitesIgnorePlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig SamSitesIgnorePlayers false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("SamSitesIgnorePlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig SamSitesIgnorePlayers true");
        }

        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.TrapsIgnorePlayers)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("TrapsIgnorePlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig TrapsIgnorePlayers false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("TrapsIgnorePlayers"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig TrapsIgnorePlayers true");
        }
        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.HonorBuildingPrivilege)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("HonorBuildingPrivilege"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig HonorBuildingPrivilege false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("HonorBuildingPrivilege"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig HonorBuildingPrivilege true");
        }
        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.UnprotectedBuildingDamage)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("UnprotectedBuildingDamage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig UnprotectedBuildingDamage false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("UnprotectedBuildingDamage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig UnprotectedBuildingDamage true");
        }
        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.TwigDamage)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("TwigDamage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig TwigDamage false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("TwigDamage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig TwigDamage true");
        }

        if (CheckRelEnables())
        {
            if (configData.Options.HonorRelationships)
            {
                UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("HonorRelationships"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", "pverule editconfig HonorRelationships false");
            }
            else
            {
                UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("HonorRelationships"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", "pverule editconfig HonorRelationships true");
            }
        }
        else
        {
            if (configData.Options.HonorRelationships)
            {
                UI.Button(ref container, NGPVERULELIST, UI.Color("#33b628", 1f), Lang("HonorRelationships"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", "");
            }
            else
            {
                UI.Button(ref container, NGPVERULELIST, UI.Color("#cccccc", 1f), Lang("HonorRelationships"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", "");
            }
        }
        row++;
        pb = GetButtonPositionF(row, col);
        if (CheckRelEnables())
        {
            if (configData.Options.AllowFriendlyFire)
            {
                UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("AllowFriendlyFire"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", "pverule editconfig AllowFriendlyFire false");
            }
            else
            {
                UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("AllowFriendlyFire"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", "pverule editconfig AllowFriendlyFire true");
            }
        }
        else
        {
            if (configData.Options.AllowFriendlyFire)
            {
                UI.Button(ref container, NGPVERULELIST, UI.Color("#33b628", 1f), Lang("AllowFriendlyFire"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", "");
            }
            else
            {
                UI.Button(ref container, NGPVERULELIST, UI.Color("#cccccc", 1f), Lang("AllowFriendlyFire"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", "");
            }
        }

        row++;
        pb = GetButtonPositionF(row, col);
        if (configData.Options.AllowSuicide)
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#55d840", 1f), Lang("AllowSuicide"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig AllowSuicide false");
        }
        else
        {
            UI.Button(ref container, NGPVERULELIST, UI.Color("#555555", 1f), Lang("AllowSuicide"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig AllowSuicide true");
        }

        row++;
        pb = GetButtonPositionF(row, col);
        UI.Button(ref container, NGPVERULELIST, UI.Color("#d82222", 1f), Lang("deflag"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editconfig RESET true");

        CuiHelper.AddUi(player, container);
    }

    private void GUIRulesetEditor(BasePlayer player, string rulesetname)
    {
        IsOpen(player.userID, true);
        CuiHelper.DestroyUi(player, NGPVEEDITRULESET);
        string rulename = rulesetname;
        bool isEnabled = false;
        bool damage = false;
        string schedule = null;
        string zone = null;
        bool invert = false;

        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand getrs = new SqliteCommand($"SELECT DISTINCT automated, enabled, damage, schedule, zone, invschedule from ngpve_rulesets WHERE name='{rulesetname}'", c))
            {
                using (SqliteDataReader rsread = getrs.ExecuteReader())
                {
                    while (rsread.Read())
                    {
                        if (rsread.GetBoolean(0)) rulename += " (" + Lang("automated") + ")";
                        isEnabled = rsread.GetBoolean(1);
                        damage = rsread.GetBoolean(2);
                        zone = rsread.GetString(4);
                        object inv = rsread.GetValue(5);
                        if (inv is DBNull) inv = 0;
                        invert = Convert.ToBoolean(inv);
                        try
                        {
                            schedule = rsread.GetString(3) ?? "0";
                        }
                        catch
                        {
                            schedule = "0";
                        }
                    }
                }
            }
        }

        CuiElementContainer container = UI.Container(NGPVEEDITRULESET, UI.Color("3b3b3b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
        UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("nextgenpveruleset") + ": " + rulename, 24, "0.15 0.92", "0.7 1");
        UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.04");

        UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#2222ff", 1f), Lang("clone"), 12, "0.63 0.95", "0.69 0.98", $"pverule editruleset {rulesetname} clone");
        if (isEnabled)
        {
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("enabled"), 12, "0.78 0.95", "0.84 0.98", $"pverule editruleset {rulesetname} enable 0");
        }
        else
        {
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#ff2222", 1f), Lang("disabled"), 12, "0.78 0.95", "0.84 0.98", $"pverule editruleset {rulesetname} enable 1");
        }

        if (rulesetname == "default")
        {
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#ff2222", 1f), Lang("defload"), 12, "0.85 0.95", "0.92 0.98", $"pverule editruleset {rulesetname} defload YES");
        }
        else
        {
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#2222ff", 1f), Lang("editname"), 12, "0.7 0.95", "0.76 0.98", $"pverule editruleset {rulesetname} name");
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#ff2222", 1f), Lang("delete"), 12, "0.86 0.95", "0.92 0.98", $"pverule editruleset {rulesetname} delete");
        }
        UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeruleset");

        const string dicolor = "#333333";
        const string encolor = "#ff3333";
        int hdrcol = 0;
        int row = 0;

        float[] pb = GetButtonPositionP(row, hdrcol);
        UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("defaultdamage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

        row += 2;
        pb = GetButtonPositionP(row, hdrcol);
        UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("zone"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

        row += 2;
        pb = GetButtonPositionP(row, hdrcol);

        if (configData.Options.useSchedule)
        {
            switch (invert)
            {
                case true:
                    UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("schedulei"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                    break;
                case false:
                    UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("schedulep"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                    break;
            }

            row += 2;
            pb = GetButtonPositionP(row, hdrcol);
            switch (invert)
            {
                case true:
                    UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#ff3333", 1f), Lang("sinverted"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} invschedule 0");
                    break;
                case false:
                    UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#333333", 1f), Lang("sinvert"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} invschedule 1");
                    break;
            }
        }
        else
        {
            UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("scheduled"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        }

        row = 0; hdrcol++;
        pb = GetButtonPositionP(row, hdrcol);
        string de = Lang("block"); if (!damage) de = Lang("allow");
        UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), de, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

        row++;
        hdrcol = 0;

        pb = GetButtonPositionP(row, hdrcol);
        if (rulesetname == "default")
        {
            UI.Label(ref container, NGPVEEDITRULESET, UI.Color(encolor, 1f), Lang("false"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        }
        else if (damage)
        {
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color(encolor, 1f), Lang("true"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} damage 0");
        }
        else
        {
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color(dicolor, 1f), Lang("false"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} damage 1");
        }

        // Exceptions (block/allow)
        int col = 1;
        int numExceptions = 0;

        //Utils.DoLog($"SELECT DISTINCT exception FROM ngpve_rulesets WHERE name='{rulesetname}' ORDER BY exception");
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand getrs = new SqliteCommand($"SELECT DISTINCT exception FROM ngpve_rulesets WHERE name='{rulesetname}' ORDER BY exception", c))
            {
                using (SqliteDataReader rsread = getrs.ExecuteReader())
                {
                    while (rsread.Read())
                    {
                        string except = rsread.GetValue(0).ToString();
                        //Utils.DoLog($"Found exception: {except}");
                        if (except?.Length == 0) continue;
                        if (row > 11)
                        {
                            row = 1;
                            col++;
                        }
                        numExceptions++;
                        pb = GetButtonPositionP(row, col);
                        UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#d85540", 1f), except, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except");
                        row++;
                    }
                }
            }
        }

        if (numExceptions < 1)
        {
            pb = GetButtonPositionP(row, col);
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except");
        }
        else
        {
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        }

        hdrcol += 2; row = 0;
        if (numExceptions > 11) hdrcol++;
        if (numExceptions > 22) hdrcol++;
        if (numExceptions > 33) hdrcol++;
        pb = GetButtonPositionP(row, hdrcol);
        UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("exclude") + " " + Lang("source"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

        hdrcol++;
        pb = GetButtonPositionP(row, hdrcol);
        UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("exclude") + " " + Lang("target"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

        // Source exclusions from exceptions above
        col++; row = 1;
        bool noExclusions = true;
        if (numExceptions > 0) // Cannot exclude from exceptions that do not exist
        {
            //Utils.DoLog($"SELECT DISTINCT src_exclude FROM ngpve_rulesets WHERE name='{rulesetname}'");
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand getrs = new SqliteCommand($"SELECT DISTINCT src_exclude FROM ngpve_rulesets WHERE name='{rulesetname}'", c))
                {
                    using (SqliteDataReader rsread = getrs.ExecuteReader())
                    {
                        while (rsread.Read())
                        {
                            string exclude = rsread.GetValue(0).ToString();
                            if (exclude?.Length == 0) continue;
                            noExclusions = false;
                            if (row > 11)
                            {
                                row = 0;
                                col++;
                            }
                            noExclusions = false;
                            string[] excl = exclude.Split(',');
                            if (excl.Length > 1)
                            {
                                foreach (string ex in excl)
                                {
                                    //Utils.DoLog($"Adding button for existing src_exclude of {ex}");
                                    pb = GetButtonPositionP(row, col);
                                    UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#d85540", 1f), ex, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude");
                                    row++;
                                }
                            }
                            else
                            {
                                //Utils.DoLog($"Adding button for existing src_exclude of {exclude}");
                                pb = GetButtonPositionP(row, col);
                                UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#d85540", 1f), exclude, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude");
                                row++;
                            }
                        }
                    }
                }
            }
        }
        if (noExclusions && numExceptions > 0)
        {
            row = 1;
            pb = GetButtonPositionP(row, col);
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude");
        }

        // Target exclusions from exceptions above
        col++; row = 1;
        noExclusions = true;
        if (numExceptions > 0) // Cannot exclude from exceptions that do not exist
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand getrs = new SqliteCommand($"SELECT DISTINCT tgt_exclude FROM ngpve_rulesets WHERE name='{rulesetname}'", c))
                {
                    using (SqliteDataReader rsread = getrs.ExecuteReader())
                    {
                        while (rsread.Read())
                        {
                            string exclude = rsread.GetValue(0).ToString();
                            if (exclude?.Length == 0) continue;
                            noExclusions = false;
                            if (row > 11)
                            {
                                row = 0;
                                col++;
                            }
                            noExclusions = false;
                            string[] excl = exclude.Split(',');
                            if (excl.Length > 1)
                            {
                                foreach (string ex in excl)
                                {
                                    //Utils.DoLog($"Adding button for existing tgt_exclude of {ex}");
                                    pb = GetButtonPositionP(row, col);
                                    UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#d85540", 1f), ex, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude");
                                    row++;
                                }
                            }
                            else
                            {
                                //Utils.DoLog($"Adding button for existing tgt_exclude of {exclude}");
                                pb = GetButtonPositionP(row, col);
                                UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#d85540", 1f), exclude, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude");
                            }
                            row++;
                        }
                    }
                }
            }
        }

        if (noExclusions && numExceptions > 0)
        {
            row = 1;
            pb = GetButtonPositionP(row, col);
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude");
        }

        col = 0; row = 3;
        pb = GetButtonPositionP(row, col);
        if (rulesetname == "default")
        {
            UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#d85540", 1f), Lang("default"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        }
        else
        {
            UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone");
        }
        if (rulesetname != "default")
        {
            //                row++;
            //                pb = GetButtonPositionP(row, col);
            //                UI.Label(ref container, NGPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        }

        if (configData.Options.useSchedule)
        {
            col = 0; row = 5;
            pb = GetButtonPositionP(row, col);
            if (!string.IsNullOrEmpty(schedule))
            {
                UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#d85540", 1f), schedule, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedule");
            }
            else
            {
                UI.Button(ref container, NGPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedule");
            }
        }

        CuiHelper.AddUi(player, container);
    }

    private void GUICustomSelect(BasePlayer player)
    {
        IsOpen(player.userID, true);
        CuiHelper.DestroyUi(player, NGPVECUSTOMSELECT);
        CuiElementContainer container = UI.Container(NGPVECUSTOMSELECT, UI.Color("3b3b3b", 1f), "0.25 0.4", "0.7 0.55", true, "Overlay");
        UI.Label(ref container, NGPVECUSTOMSELECT, UI.Color("#ffffff", 1f), Lang("edit"), 12, "0.1 0.92", "0.9 1");
        UI.Button(ref container, NGPVECUSTOMSELECT, UI.Color("#55d840", 1f), Lang("custom") + " " + Lang("rules"), 12, "0.1 0.4", "0.25 0.6", $"pverule customrules");
        UI.Button(ref container, NGPVECUSTOMSELECT, UI.Color("#5540d8", 1f), Lang("all") + " " + Lang("entities"), 12, "0.27 0.4", "0.47 0.6", $"pverule allentities");
        UI.Button(ref container, NGPVECUSTOMSELECT, UI.Color("#5540d8", 1f), Lang("unknown") + " " + Lang("entities"), 12, "0.49 0.4", "0.73 0.6", $"pverule unknownentities");
        UI.Button(ref container, NGPVECUSTOMSELECT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.75 0.4", "0.89 0.6", $"pverule closecustom");

        CuiHelper.AddUi(player, container);
    }

    // Used to select NPC types for targeting exclusions (CanBeTargeted)
    private void GUISelectNPCTypes(BasePlayer player)
    {
        IsOpen(player.userID, true);
        CuiHelper.DestroyUi(player, NGPVENPCSELECT);
        CuiElementContainer container = UI.Container(NGPVENPCSELECT, UI.Color("232323", 1f), "0.15 0.15", "0.78 0.85", true, "Overlay");
        UI.Label(ref container, NGPVENPCSELECT, UI.Color("#ffffff", 1f), Lang("nextgenpvenpcselect"), 24, "0.01 0.92", "0.55 1");
        UI.Label(ref container, NGPVENPCSELECT, UI.Color("#d85540", 1f), Lang("disabled"), 12, "0.66 0.95", "0.72 0.98");
        UI.Label(ref container, NGPVENPCSELECT, UI.Color("#55d840", 1f), Lang("enabled"), 12, "0.73 0.95", "0.79 0.98");
        UI.Button(ref container, NGPVENPCSELECT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closenpcselect");

        UI.Label(ref container, NGPVENPCSELECT, UI.Color("#ffffff", 1f), Lang("npcselectnotes"), 12, "0.1 0.01", "0.9 0.1");

        int row = 0; int col = 0;
        string exq = $"SELECT DISTINCT name FROM ngpve_targetexclusion";
        List<string> exclusions = new List<string>();
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand se = new SqliteCommand(exq, c))
            {
                using (SqliteDataReader re = se.ExecuteReader())
                {
                    while (re.Read())
                    {
                        exclusions.Add(re.GetString(0));
                    }
                }
            }
        }
        string q = $"SELECT DISTINCT type from ngpve_entities WHERE name='npc' ORDER BY type";
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand se = new SqliteCommand(q, c))
            {
                using (SqliteDataReader re = se.ExecuteReader())
                {
                    float[] pb = GetButtonPositionP(row, col);
                    while (re.Read())
                    {
                        string type = re.GetString(0);
                        if (row > 5)
                        {
                            row = 0;
                            col++;
                            if (col > 6) break;
                        }
                        pb = GetButtonPositionP(row, col);
                        if (exclusions.Contains(type))
                        {
                            UI.Button(ref container, NGPVENPCSELECT, UI.Color("#d85540", 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editnpcset {type} -1");
                        }
                        else
                        {
                            UI.Button(ref container, NGPVENPCSELECT, UI.Color("#55d840", 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editnpcset {type} 1");
                        }
                        row++;
                    }
                }
            }
        }
        CuiHelper.AddUi(player, container);
    }

    // Select entity to edit
    private void GUISelectEntity(BasePlayer player, string cat = "unknown", string search = "")
    {
        IsOpen(player.userID, true);
        CuiHelper.DestroyUi(player, NGPVEENTSELECT);

        CuiElementContainer container = UI.Container(NGPVEENTSELECT, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
        UI.Label(ref container, NGPVEENTSELECT, UI.Color("#ffffff", 1f), Lang("nextgenpveentselect"), 24, "0.1 0.92", "0.35 1");
        UI.Label(ref container, NGPVEENTSELECT, UI.Color("#ffffff", 1f), " - " + Lang(cat) + " " + Lang("entities"), 24, "0.36 0.92", "0.5 1");

        UI.Label(ref container, NGPVEENTSELECT, UI.Color("#ffffff", 1f), Lang("search") + " " + Lang("collections"), 12, "0.52 0.92", "0.61 1");
        UI.Label(ref container, NGPVEENTSELECT, UI.Color("#535353", 1f), cat, 12, "0.62 0.92", "0.7 1");
        UI.Input(ref container, NGPVEENTSELECT, UI.Color("#ffffff", 1f), " ", 12, "0.62 0.92", "0.7 1", $"pverule allentities coll ");

        UI.Label(ref container, NGPVEENTSELECT, UI.Color("#ffffff", 1f), Lang("search") + " " + Lang("entities"), 12, "0.72 0.92", "0.8 1");
        UI.Label(ref container, NGPVEENTSELECT, UI.Color("#535353", 1f), Lang("search"), 12, "0.81 0.92", "0.85 1");
        UI.Input(ref container, NGPVEENTSELECT, UI.Color("#ffffff", 1f), " ", 12, "0.81 0.92", "0.85 1", $"pverule allentities ");

        // Bottom
        UI.Label(ref container, NGPVEENTSELECT, UI.Color("#ffffff", 1f), Lang("clicktosetcoll"), 12, "0.2 0.01", "0.89 0.04");
        UI.Label(ref container, NGPVEENTSELECT, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.04");
        UI.Button(ref container, NGPVEENTSELECT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeentselect");

        int row = 0; int col = 0;
        if (cat == null) cat = "unknown";

        string q = $"SELECT DISTINCT name, type from ngpve_entities WHERE name='{cat}' ORDER BY type";
        if (search != "")
        {
            q = $"SELECT name, type from ngpve_entities WHERE type LIKE '%{search}%' ORDER BY type";
        }
        else if (cat == "all")
        {
            q = $"SELECT DISTINCT name, type from ngpve_entities ORDER BY type";
        }
        else
        {
            q = $"SELECT DISTINCT name, type FROM ngpve_entities WHERE name LIKE '%{cat}%' ORDER BY type";
        }

        if (configData.Options.debug) Utils.DoLog($"QUERY IS {q}, COLLECTION IS {cat}, SEARCH IS {search}");

        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand se = new SqliteCommand(q, c))
            {
                using (SqliteDataReader re = se.ExecuteReader())
                {
                    float[] pb = GetButtonPositionP(row, col);
                    while (re.Read())
                    {
                        string oldcat = re.GetString(0);
                        string entname = re.GetString(1);
                        if (row > 13)
                        {
                            row = 0;
                            col++;
                            if (col > 6) break;
                        }
                        pb = GetButtonPositionP(row, col);
                        UI.Button(ref container, NGPVEENTSELECT, UI.Color("#d85540", 1f), entname, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editent {entname}");
                        row++;
                    }
                }
            }
        }

        CuiHelper.AddUi(player, container);
    }

    private void GUIEditEntity(BasePlayer player, string entname, string cat = "unknown")
    {
        IsOpen(player.userID, true);
        CuiHelper.DestroyUi(player, NGPVEENTEDIT);

        CuiElementContainer container = UI.Container(NGPVEENTEDIT, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
        UI.Label(ref container, NGPVEENTEDIT, UI.Color("#ffffff", 1f), Lang("nextgenpveentedit") + ": " + entname + " : " + Lang("setcat"), 24, "0.2 0.92", "0.8 1");
        UI.Label(ref container, NGPVEENTEDIT, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.04");

        UI.Button(ref container, NGPVEENTEDIT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", "pverule closeentedit");
        UI.Button(ref container, NGPVEENTEDIT, UI.Color("#ff3333", 1f), Lang("delete"), 12, "0.93 0.05", "0.99 0.08", $"pverule editent {entname} DELETE");

        int row = 0; int col = 0;
        string name = "";
        float[] pb;

        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            //using (SqliteCommand se = new SqliteCommand($"SELECT DISTINCT type from ngpve_entities ORDER BY type", c))
            using (SqliteCommand se = new SqliteCommand($"SELECT DISTINCT name,type from ngpve_entities WHERE type='{entname}'", c))
            {
                using (SqliteDataReader re = se.ExecuteReader())
                {
                    pb = GetButtonPositionP(row, col);
                    while (re.Read())
                    {
                        name = re.GetString(0);
                        string type = re.GetString(1);
                    }
                }
            }
        }

        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand lt = new SqliteCommand($"SELECT DISTINCT name from ngpve_entities WHERE name != 'unknown'", c))
            {
                using (SqliteDataReader rt = lt.ExecuteReader())
                {
                    pb = GetButtonPositionP(row, col);
                    while (rt.Read())
                    {
                        string newcat = rt.GetString(0);

                        if (row > 13)
                        {
                            row = 0;
                            col++;
                            if (col > 6) break;
                        }
                        pb = GetButtonPositionP(row, col);

                        string uicolor = "#d85540";
                        if (newcat == name) uicolor = "#55d840";

                        UI.Button(ref container, NGPVEENTEDIT, UI.Color(uicolor, 1f), newcat, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editent {entname} {newcat}");
                        row++;
                    }
                }
            }
        }
        pb = GetButtonPositionP(row, col);
        UI.Button(ref container, NGPVEENTEDIT, UI.Color("#777777", 1f), "unknown", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editent {entname} unknown");

        col++; row = 0;
        pb = GetButtonPositionP(row, col);
        UI.Label(ref container, NGPVEENTEDIT, UI.Color("#db5540", 1f), Lang("newcat") + ": ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

        col++;
        pb = GetButtonPositionP(row, col);
        UI.Label(ref container, NGPVEENTEDIT, UI.Color("#535353", 1f), Lang("new"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        UI.Input(ref container, NGPVEENTEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editent {entname} ");

        CuiHelper.AddUi(player, container);
    }

    // Select rule to add to ruleset or to edit
    private void GUISelectRule(BasePlayer player, string rulesetname, bool docustom = false)
    {
        IsOpen(player.userID, true);
        CuiHelper.DestroyUi(player, NGPVERULESELECT);

        CuiElementContainer container = UI.Container(NGPVERULESELECT, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
        UI.Label(ref container, NGPVERULESELECT, UI.Color("#ffffff", 1f), Lang("nextgenpveruleselect"), 24, "0.2 0.92", "0.7 1");
        UI.Label(ref container, NGPVERULESELECT, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.04");

        UI.Button(ref container, NGPVERULESELECT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeruleselect");

        int col = 0;
        int row = 0;

        List<string> exc = new List<string>();
        if (rulesetname != null)
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand crs = new SqliteCommand($"SELECT exception from ngpve_rulesets WHERE name='{rulesetname}' ORDER BY exception", c))
                {
                    using (SqliteDataReader crd = crs.ExecuteReader())
                    {
                        while (crd.Read())
                        {
                            exc.Add(crd.GetString(0));
                        }
                    }
                }
            }
        }
        string custsel = " WHERE custom='1' ";
        if (docustom)
        {
            UI.Button(ref container, NGPVERULESELECT, UI.Color("#d85540", 1f), Lang("add"), 12, "0.72 0.95", "0.78 0.98", "pverule addcustomrule");
        }
        else
        {
            UI.Label(ref container, NGPVERULESELECT, UI.Color("#5555cc", 1f), Lang("stock"), 12, "0.72 0.95", "0.78 0.98");
            UI.Label(ref container, NGPVERULESELECT, UI.Color("#d85540", 1f), Lang("custom"), 12, "0.79 0.95", "0.85 0.98");
            UI.Label(ref container, NGPVERULESELECT, UI.Color("#55d840", 1f), Lang("enabled"), 12, "0.86 0.95", "0.92 0.98");

            custsel = "";
        }

        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand sr = new SqliteCommand($"SELECT DISTINCT name, custom from ngpve_rules {custsel}ORDER BY name", c))
            {
                using (SqliteDataReader rr = sr.ExecuteReader())
                {
                    float[] pb = GetButtonPositionP(row, col);
                    while (rr.Read())
                    {
                        string rulename = rr.GetString(0);
                        bool custom = rr.GetBoolean(1);
                        if (row > 10)
                        {
                            row = 0;
                            col++;
                        }
                        pb = GetButtonPositionP(row, col);
                        if (docustom)
                        {
                            UI.Button(ref container, NGPVERULESELECT, UI.Color("#55d840", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename}");
                        }
                        else if (exc.Contains(rulename))
                        {
                            UI.Button(ref container, NGPVERULESELECT, UI.Color("#55d840", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except {rulename} delete");
                        }
                        else
                        {
                            string ruleColor = "#5555cc";
                            if (custom) ruleColor = "#d85540";
                            UI.Button(ref container, NGPVERULESELECT, UI.Color(ruleColor, 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except {rulename} add");
                        }
                        row++;
                    }
                }
            }
        }

        CuiHelper.AddUi(player, container);
    }

    private void GUISelectExclusion(BasePlayer player, string rulesetname, string srctgt)
    {
        IsOpen(player.userID, true);
        //Utils.DoLog($"GUISelectExclusion called for {rulesetname}");
        CuiHelper.DestroyUi(player, NGPVERULEEXCLUSIONS);
        string t = Lang("source"); if (srctgt == "tgt_exclude") t = Lang("target");

        CuiElementContainer container = UI.Container(NGPVERULEEXCLUSIONS, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
        UI.Button(ref container, NGPVERULEEXCLUSIONS, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeexclusions");
        UI.Label(ref container, NGPVERULEEXCLUSIONS, UI.Color("#ffffff", 1f), Lang("nextgenpveexclusions") + " " + t, 24, "0.2 0.92", "0.7 1");
        UI.Label(ref container, NGPVERULEEXCLUSIONS, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.04");

        int col = 0;
        int row = 0;

        List<string> foundsrc = new List<string>();
        List<string> foundtgt = new List<string>();
        List<string> src_exclude = new List<string>();
        List<string> tgt_exclude = new List<string>();

        // Get ruleset src and tgt exclusions
        //Utils.DoLog($"SELECT src_exclude, tgt_exclude FROM ngpve_rulesets WHERE name='{rulesetname}'");
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand rs = new SqliteCommand($"SELECT src_exclude, tgt_exclude FROM ngpve_rulesets WHERE name='{rulesetname}' ORDER BY src_exclude,tgt_exclude", c))
            {
                using (SqliteDataReader rsd = rs.ExecuteReader())
                {
                    while (rsd.Read())
                    {
                        string a = rsd.GetValue(0).ToString();
                        if (a != "")
                        {
                            //Utils.DoLog($"Adding {a} to src_exclude");
                            src_exclude.Add(a);
                        }
                        string b = rsd.GetValue(1).ToString();
                        if (b != "")
                        {
                            //Utils.DoLog($"Adding {b} to tgt_exclude");
                            tgt_exclude.Add(b);
                        }
                    }
                }
            }
        }

        // Get organized entities list
        Dictionary<string, NextGenPVEEntities> ngpveentities = new Dictionary<string, NextGenPVEEntities>();
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand ent = new SqliteCommand("SELECT name,type,custom FROM ngpve_entities ORDER BY type", c))
            {
                using (SqliteDataReader ntd = ent.ExecuteReader())
                {
                    while (ntd.Read())
                    {
                        string nm = ntd.GetString(0);
                        string tp = ntd.GetString(1);
                        if (nm?.Length == 0 || tp?.Length == 0) continue;
                        bool cs = ntd.GetBoolean(2);
                        //Utils.DoLog($"Adding {nm} {tp} to entities list");
                        if (nm != "" && tp != "")
                        {
                            if (!ngpveentities.ContainsKey(nm))
                            {
                                ngpveentities[nm] = new NextGenPVEEntities() { types = new List<string> { tp }, custom = cs };
                            }
                            else
                            {
                                ngpveentities[nm].types.Add(tp);
                            }
                        }
                    }
                }
            }
        }

        // Go through the ruleset looking for exceptions, which we convert into entity types
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand crs = new SqliteCommand($"SELECT exception from ngpve_rulesets WHERE name='{rulesetname}' ORDER BY exception", c))
            {
                using (SqliteDataReader crd = crs.ExecuteReader())
                {
                    List<string> exc = new List<string>();
                    while (crd.Read())
                    {
                        string rulename = crd.GetValue(0).ToString();
                        if (rulename?.Length == 0) continue;
                        string src = null;
                        string tgt = null;

                        if (!string.IsNullOrEmpty(rulename))
                        {
                            string[] st = rulename.Split('_');
                            src = st[0]; tgt = st[1];
                        }

                        float[] pb = GetButtonPositionP(row, col);
                        switch (srctgt)
                        {
                            case "src_exclude":
                                if (src == null || !ngpveentities.ContainsKey(src)) break;
                                foreach (string type in ngpveentities[src].types)
                                {
                                    //Utils.DoLog($"Checking for '{type}'");
                                    if (type?.Length == 0) continue;
                                    if (foundsrc.Contains(type)) continue;
                                    foundsrc.Add(type);
                                    if (row > 13)
                                    {
                                        row = 0;
                                        col++;
                                    }
                                    pb = GetButtonPositionS(row, col);
                                    string eColor = "#d85540";

                                    //Utils.DoLog($"  Creating button for {type}, src_exclude='{src_exclude}'");
                                    if (!src_exclude.Contains(type))
                                    {
                                        UI.Button(ref container, NGPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude {type} add");
                                    }
                                    else
                                    {
                                        eColor = "#55d840";
                                        UI.Button(ref container, NGPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude {type} delete");
                                    }
                                    row++;
                                }
                                break;
                            case "tgt_exclude":
                                if (tgt == null || !ngpveentities.ContainsKey(tgt)) break;
                                foreach (string type in ngpveentities[tgt].types)
                                {
                                    //Utils.DoLog($"Checking for '{type}'");
                                    if (type?.Length == 0) continue;
                                    if (foundtgt.Contains(type)) continue;
                                    foundtgt.Add(type);
                                    if (row > 13)
                                    {
                                        row = 0;
                                        col++;
                                    }
                                    pb = GetButtonPositionS(row, col);
                                    string eColor = "#d85540";

                                    //Utils.DoLog($"  Creating button for {type}, tgt_exclude='{tgt_exclude}'");
                                    if (!tgt_exclude.Contains(type))
                                    {
                                        UI.Button(ref container, NGPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude {type} add");
                                    }
                                    else
                                    {
                                        eColor = "#55d840";
                                        UI.Button(ref container, NGPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude {type} delete");
                                    }
                                    row++;
                                }
                                break;
                        }
                    }
                    row++;
                }
            }
        }

        CuiHelper.AddUi(player, container);
    }

    private void GUIRuleEditor(BasePlayer player, string rulename = "")
    {
        IsOpen(player.userID, true);
        CuiHelper.DestroyUi(player, NGPVERULEEDIT);

        CuiElementContainer container = UI.Container(NGPVERULEEDIT, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true);//, "Overlay");
        UI.Button(ref container, NGPVERULEEDIT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closerule");
        UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), Lang("nextgenpverule") + ": " + rulename, 24, "0.2 0.92", "0.7 1");
        UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.04");

        int col = 0;
        int row = 0;

        float[] pb = GetButtonPositionP(row, col);
        UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), Lang("name"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        row++;
        pb = GetButtonPositionP(row, col);
        UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), Lang("description"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

        string description = "";
        string source = "";
        string target = "";
        bool damage = false;
        bool custom = false;
        if (rulename != null)
        {
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), Lang("damage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), Lang("source"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), Lang("target"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand getrs = new SqliteCommand($"SELECT DISTINCT * FROM ngpve_rules WHERE name='{rulename}'", c))
                {
                    using (SqliteDataReader rd = getrs.ExecuteReader())
                    {
                        description = "";
                        source = "";
                        target = "";
                        while (rd.Read())
                        {
                            description = rd.GetString(1);
                            damage = rd.GetBoolean(2);
                            custom = rd.GetBoolean(3);
                            source = rd.GetValue(4).ToString();
                            target = rd.GetValue(5).ToString();
                            //Utils.DoLog($"Found rule {rulename}: {description}, {source}, {target}");
                        }
                    }
                }
            }

            row = 0; col = 1;
            pb = GetButtonPositionP(row, col);
            if (!custom)
            {
                UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), description, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), damage.ToString(), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), source, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), target, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else
            {
                UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, NGPVERULEEDIT, UI.Color("#535353", 1f), description, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                UI.Input(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} description ");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), damage.ToString(), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, NGPVERULEEDIT, UI.Color("#535353", 1f), source, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                UI.Input(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} source ");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, NGPVERULEEDIT, UI.Color("#535353", 1f), target, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                UI.Input(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} target ");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, NGPVERULEEDIT, UI.Color("#d85540", 1f), Lang("delete"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule deleterule {rulename}");
            }
        }
        else
        {
            row = 2; col = 0;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), "Source", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), "Target", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row = 0; col = 1;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, NGPVERULEEDIT, UI.Color("#535353", 1f), Lang("name"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            UI.Input(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule addrule ");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, NGPVERULEEDIT, UI.Color("#535353", 1f), Lang("description"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            UI.Input(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} description ");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, NGPVERULEEDIT, UI.Color("#535353", 1f), Lang("source"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            UI.Input(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} source ");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, NGPVERULEEDIT, UI.Color("#535353", 1f), Lang("target"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            UI.Input(ref container, NGPVERULEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} target ");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Button(ref container, NGPVERULEEDIT, UI.Color("#d85540", 1f), Lang("delete"), 12, "0.93 0.95", "0.99 0.98", $"pverule deleterule {rulename}");
        }

        CuiHelper.AddUi(player, container);
    }

    private void GUIEditValue(BasePlayer player, string rulesetname, string key = null)
    {
        IsOpen(player.userID, true);
        CuiHelper.DestroyUi(player, NGPVEVALUEEDIT);

        CuiElementContainer container = UI.Container(NGPVEVALUEEDIT, UI.Color("4b4b4b", 1f), "0.15 0.15", "0.85 0.85", true, "Overlay");
        UI.Button(ref container, NGPVEVALUEEDIT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closerulevalue");
        UI.Label(ref container, NGPVEVALUEEDIT, UI.Color("#ffffff", 1f), Lang("nextgenpvevalue") + ": " + rulesetname + " " + key, 24, "0.2 0.92", "0.7 1");
        UI.Label(ref container, NGPVEVALUEEDIT, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.04");

        int col = 0;
        int row = 0;
        float[] pb = GetButtonPositionZ(row, col);
        string zone = null;

        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand rs = new SqliteCommand($"SELECT DISTINCT zone FROM ngpve_rulesets WHERE name='{rulesetname}'", c))
            {
                using (SqliteDataReader rsd = rs.ExecuteReader())
                {
                    while (rsd.Read())
                    {
                        zone = rsd.GetString(0);
                    }
                }
            }
        }

        switch (key)
        {
            case "name":
                pb = GetButtonPositionZ(row, col);
                UI.Label(ref container, NGPVEVALUEEDIT, UI.Color("#ffffff", 1f), Lang("editname") + ":", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                col++;
                pb = GetButtonPositionZ(row, col);
                UI.Label(ref container, NGPVEVALUEEDIT, UI.Color("#535353", 1f), rulesetname, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                UI.Input(ref container, NGPVEVALUEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} name ");
                break;
            default:
                CuiHelper.DestroyUi(player, NGPVEVALUEEDIT);
                break;
        }

        CuiHelper.AddUi(player, container);
    }

    private void GUIEditSchedule(BasePlayer player, string rulesetname)
    {
        IsOpen(player.userID, true);
        CuiHelper.DestroyUi(player, NGPVESCHEDULEEDIT);

        string schedule = null;
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand rs = new SqliteCommand($"SELECT DISTINCT schedule FROM ngpve_rulesets WHERE name='{rulesetname}'", c))
            {
                using (SqliteDataReader rsd = rs.ExecuteReader())
                {
                    while (rsd.Read())
                    {
                        schedule = rsd.GetValue(0).ToString();
                    }
                }
            }
        }

        CuiElementContainer container = UI.Container(NGPVESCHEDULEEDIT, UI.Color("4b4b4b", 1f), "0.15 0.15", "0.85 0.85", true, "Overlay");
        UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeruleschedule");
        UI.Label(ref container, NGPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Lang("nextgenpveschedule") + ": " + rulesetname, 24, "0.2 0.92", "0.7 1");
        UI.Label(ref container, NGPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.85 0.01", "0.99 0.04");

        int col = 0;
        int row = 0;
        float[] pb = GetButtonPositionSchedule(row, col);
        UI.Label(ref container, NGPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Lang("day"), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        col++;
        pb = GetButtonPositionSchedule(row, col);
        UI.Label(ref container, NGPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Lang("starthour"), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        col++; col++;
        pb = GetButtonPositionSchedule(row, col);
        UI.Label(ref container, NGPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Lang("startminute"), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        col++; col++; col++;
        pb = GetButtonPositionSchedule(row, col);
        UI.Label(ref container, NGPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Lang("endhour"), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
        col++; col++;
        pb = GetButtonPositionSchedule(row, col);
        UI.Label(ref container, NGPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Lang("endminute"), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

        NextGenPVESchedule nextgenschedule = new NextGenPVESchedule();

        row = 1;
        col = 0;
        ParseSchedule(rulesetname, schedule, out nextgenschedule);

        pb = GetButtonPositionSchedule(row, col);
        if (nextgenschedule.day.Contains("*"))
        {
            UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#55d840", 1f), Lang("all"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleday all");
        }
        else
        {
            UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#d85540", 1f), Lang("all"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleday all");
        }

        row++;
        int daynum = -1;
        foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)).OfType<DayOfWeek>().ToList())
        {
            daynum++;
            pb = GetButtonPositionSchedule(row, col);
            if (nextgenschedule.day[0]?.Length == 0)
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#d85540", 1f), Lang(day.ToString()), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleday {daynum}");
            }
            else if (nextgenschedule.day.Contains(daynum.ToString()) || nextgenschedule.day.Contains("*"))
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#55d840", 1f), Lang(day.ToString()), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleday {daynum}");
            }
            else
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#d85540", 1f), Lang(day.ToString()), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleday {daynum}");
            }
            row++;
        }

        pb = GetButtonPositionSchedule(row, col);
        if (nextgenschedule.day.Contains("none"))
        {
            UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#55d840", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleday none");
        }
        else
        {
            UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#d85540", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleday none");
        }

        col++;
        row = 1;
        int i = 0;
        for (int start = 0; start < 24; start++)
        {
            if (i > 11)
            {
                i = 0;
                row = 1;
                col++;
            }
            pb = GetButtonPositionSchedule(row, col);
            if (int.Parse(nextgenschedule.starthour).Equals(start))
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#55d840", 1f), start.ToString(), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedulestarthour {start}");
            }
            else
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#d85540", 1f), start.ToString(), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedulestarthour {start}");
            }
            row++;
            i++;
        }

        col++;
        row = 1;
        i = 0;
        for (int start = 0; start < 60; start++)
        {
            if (i > 19)
            {
                i = 0;
                row = 1;
                col++;
            }
            pb = GetButtonPositionSchedule(row, col);
            if (int.Parse(nextgenschedule.startminute).Equals(start))
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#55d840", 1f), start.ToString(), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedulestartminute {start}");
            }
            else
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#404040", 1f), start.ToString(), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedulestartminute {start}");
            }
            row++;
            i++;
        }

        col++;
        row = 1;
        i = 0;
        for (int start = 0; start < 24; start++)
        {
            if (i > 11)
            {
                i = 0;
                row = 1;
                col++;
            }
            pb = GetButtonPositionSchedule(row, col);
            if (int.Parse(nextgenschedule.endhour).Equals(start))
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#55d840", 1f), start.ToString(), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleendhour {start}");
            }
            else
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#d85540", 1f), start.ToString(), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleendhour {start}");
            }
            row++;
            i++;
        }

        col++;
        row = 1;
        i = 0;
        for (int start = 0; start < 60; start++)
        {
            if (i > 19)
            {
                i = 0;
                row = 1;
                col++;
            }
            pb = GetButtonPositionSchedule(row, col);
            if (int.Parse(nextgenschedule.endminute).Equals(start))
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#55d840", 1f), start.ToString(), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleendminute {start}");
            }
            else
            {
                UI.Button(ref container, NGPVESCHEDULEEDIT, UI.Color("#404040", 1f), start.ToString(), 11, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} scheduleendminute {start}");
            }
            row++;
            i++;
        }
        CuiHelper.AddUi(player, container);
    }

    private int RowNumber(int max, int count) => Mathf.FloorToInt(count / max);

    private float[] GetButtonPositionP(int rowNumber, int columnNumber, float colspan = 1f)
    {
        float offsetX = 0.05f + (0.126f * columnNumber);
        float offsetY = (0.87f - (rowNumber * 0.064f));

        return new float[] { offsetX, offsetY, offsetX + (0.226f * colspan), offsetY + 0.03f };
    }

    private float[] GetButtonPositionSchedule(int rowNumber, int columnNumber, float colspan = 1f)
    {
        float offsetX = 0.01f + (0.09f * columnNumber);
        float offsetY = (0.87f - (rowNumber * 0.04f));

        return new float[] { offsetX, offsetY, offsetX + (0.136f * colspan), offsetY + 0.025f };
    }

    private float[] GetButtonPositionS(int rowNumber, int columnNumber, float colspan = 1f)
    {
        float offsetX = 0.05f + (0.116f * columnNumber);
        float offsetY = (0.87f - (rowNumber * 0.064f));

        return new float[] { offsetX, offsetY, offsetX + (0.206f * colspan), offsetY + 0.03f };
    }

    private float[] GetButtonPositionZ(int rowNumber, int columnNumber)
    {
        float offsetX = 0.05f + (0.156f * columnNumber);
        float offsetY = (0.77f - (rowNumber * 0.052f));

        return new float[] { offsetX, offsetY, offsetX + 0.296f, offsetY + 0.03f };
    }

    // For FLAGS
    private float[] GetButtonPositionF(int rowNumber, int columnNumber)
    {
        float offsetX = 0.05f + (0.156f * columnNumber);
        float offsetY = (0.77f - (rowNumber * 0.042f));

        return new float[] { offsetX, offsetY, offsetX + 0.296f, offsetY + 0.02f };
    }
    #endregion

    #region Specialized_checks
    private bool IsMLRS(HitInfo hitinfo)
    {
        try
        {
            if (hitinfo.WeaponPrefab.ShortPrefabName.Equals("rocket_mlrs"))
            {
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private bool IsBaseHelicopter(BaseEntity entity)
    {
        if ((entity is BaseHelicopter) || entity.ShortPrefabName.Equals("oilfireballsmall") || entity.ShortPrefabName.Equals("napalm"))
        {
            return true;
        }
        return false;
    }

    private bool IsBaseHelicopter(HitInfo hitinfo)
    {
        if (hitinfo.Initiator is BaseHelicopter
           || (hitinfo.Initiator != null && (hitinfo.Initiator.ShortPrefabName.Equals("oilfireballsmall") || hitinfo.Initiator.ShortPrefabName.Equals("napalm"))))
        {
            return true;
        }
        else if (hitinfo.WeaponPrefab != null)
        {
            if (hitinfo.WeaponPrefab.ShortPrefabName.Equals("rocket_heli") || hitinfo.WeaponPrefab.ShortPrefabName.Equals("rocket_heli_napalm"))
            {
                return true;
            }
        }
        return false;
    }

    private bool PlayerOwnsTC(BasePlayer player, BuildingPrivlidge privilege)
    {
        if (!configData.Options.HonorBuildingPrivilege)
        {
            DoLog("HonorBuildingPrivilege set to false.  Skipping owner checks...");
            return true;
        }
        DoLog($"Does player {player.displayName} own {privilege.ShortPrefabName}?");

        BuildingManager.Building building = privilege.GetBuilding();
        if (building != null)
        {
            BuildingPrivlidge privs = building.GetDominatingBuildingPrivilege();
            if (privs == null)
            {
                if (configData.Options.UnprotectedBuildingDamage)
                {
                    DoLog($"Null privileges.  UnprotectedBuildingDamage true.  Player effectively owns {privilege.ShortPrefabName}");
                    return true;
                }
                else
                {
                    DoLog($"Null privileges.  UnprotectedBuildingDamage false.  Player effectively does not own {privilege.ShortPrefabName}");
                    return false;
                }
            }

            foreach (ulong auth in privs.authorizedPlayers.Select(x => x.userid).ToArray())
            {
                if (privilege.OwnerID == player.userID)
                {
                    DoLog($"Player owns BuildingBlock", 2);
                    return true;
                }
                else if (player.userID == auth)
                {
                    DoLog($"Player has privilege on BuildingBlock", 2);
                    return true;
                }
                else if (Utils.IsFriend(auth, privilege.OwnerID))
                {
                    DoLog($"Player is friends with owner of BuildingBlock", 2);
                    return true;
                }
            }
        }

        return false;
    }

    private bool PlayerOwnsItem(BasePlayer player, BaseEntity entity)
    {
        DoLog($"Does player {player.displayName} own {entity.ShortPrefabName}?");
        if (entity is BuildingBlock)
        {
            if (configData.Options.TwigDamage)
            {
                try
                {
                    BuildingBlock block = entity as BuildingBlock;
                    if (block.grade == BuildingGrade.Enum.Twigs)
                    {
                        DoLog("Allowing twig destruction...");
                        return true;
                    }
                }
                catch { }
            }
            if (configData.Options.protectedDays > 0 && entity.OwnerID > 0)
            {
                // Check days since last owner connection
                long lc = 0;
                //                    if (PlayerDatabase != null && configData.Options.usePlayerDatabase)
                //                    {
                //                        lc = (long)PlayerDatabase?.CallHook("GetPlayerData", entity.OwnerID.ToString(), "lc");
                //                    }
                //                    else
                //                    {
                lastConnected.TryGetValue(entity.OwnerID.ToString(), out lc);
                if (lc > 0)
                {
                    long now = ToEpochTime(DateTime.UtcNow);
                    float days = Math.Abs((now - lc) / 86400);
                    if (days > configData.Options.protectedDays)
                    {
                        DoLog($"Allowing damage for offline owner beyond {configData.Options.protectedDays} days");
                        return true;
                    }
                    else
                    {
                        DoLog($"Owner was last connected {days} days ago and is still protected...");
                    }
                }
            }

            if (!configData.Options.HonorBuildingPrivilege) return true;

            BuildingManager.Building building = (entity as BuildingBlock)?.GetBuilding();

            if (building != null)
            {
                DoLog($"Checking building privilege for {entity.ShortPrefabName}");
                BuildingPrivlidge privs = building.GetDominatingBuildingPrivilege();
                if (privs == null)
                {
                    return configData.Options.UnprotectedBuildingDamage;
                }
                foreach (ulong auth in privs.authorizedPlayers.Select(x => x.userid).ToArray())
                {
                    if (entity.OwnerID == player.userID)
                    {
                        DoLog($"Player owns BuildingBlock", 2);
                        return true;
                    }
                    else if (player.userID == auth)
                    {
                        DoLog($"Player has privilege on BuildingBlock", 2);
                        return true;
                    }
                    else if (Utils.IsFriend(auth, entity.OwnerID))
                    {
                        DoLog($"Player is friends with owner of BuildingBlock", 2);
                        return true;
                    }
                }
            }
        }
        else
        {
            // Item is not a building block.  Could be a door, etc.
            BuildingPrivlidge privs = entity.GetBuildingPrivilege();
            bool hasbp = false;
            if (!configData.Options.HonorBuildingPrivilege) hasbp = true;
            if (privs != null && configData.Options.HonorBuildingPrivilege)
            {
                foreach (ulong auth in privs.authorizedPlayers.Select(x => x.userid).ToArray())
                {
                    if (player.userID == auth)
                    {
                        DoLog($"Player has privilege on {entity.ShortPrefabName}", 2);
                        hasbp = true;
                    }
                    else if (Utils.IsFriend(auth, entity.OwnerID))
                    {
                        DoLog($"Player is friends with owner of {entity.ShortPrefabName}", 2);
                        hasbp = true;
                    }
                    else
                    {
                        DoLog($"Player may own {entity.ShortPrefabName} but is blocked by building privilege.", 2);
                    }
                }
            }

            if (Utils.IsFriend(player.userID, entity.OwnerID) && hasbp)
            {
                DoLog($"Player is friends with owner of entity", 2);
                return true;
            }
            else if (entity.OwnerID == player.userID && hasbp)
            {
                DoLog($"Player owns item", 2);
                return true;
            }
            else if (Utils.IsFriend(player.userID, entity.OwnerID) && !hasbp)
            {
                DoLog($"Player is friends with owner of entity but is blocked by building privilege", 2);
            }
            else if (entity.OwnerID == player.userID && !hasbp)
            {
                DoLog($"Player owns item but is blocked by building privilege", 2);
            }
        }
        DoLog("Player does not own or have access to this entity");
        return false;
    }

    // From PlayerDatabase
    private long ToEpochTime(DateTime dateTime)
    {
        DateTime date = dateTime.ToUniversalTime();
        long ticks = date.Ticks - new DateTime(1970, 1, 1, 0, 0, 0, 0).Ticks;
        return ticks / TimeSpan.TicksPerSecond;
    }

    public static string StringToBinary(string data)
    {
        StringBuilder sb = new StringBuilder();

        foreach (char c in data)
        {
            sb.Append(Convert.ToString(c, 2).PadLeft(8, '0'));
        }
        return sb.ToString();
    }

    private static bool GetBoolValue(string value)
    {
        if (value == null) return false;
        value = value.Trim().ToLower();
        switch (value)
        {
            case "t":
            case "true":
            case "1":
            case "yes":
            case "y":
            case "on":
                return true;
            default:
                return false;
        }
    }
    #endregion

    #region classes
    public class NextGenPVERule
    {
        public string description;
        public bool damage;
        public bool custom = true;
        public List<string> source;
        public List<string> target;
    }

    public class NextGenPVESchedule
    {
        public List<string> day = new List<string>();
        public List<string> dayName = new List<string>();
        public string starthour;
        public string startminute;
        public string endhour;
        public string endminute;
        public bool enabled = true;
    }

    // Used only within RunSchedule
    private class NGPVE_Schedule
    {
        public string name;
        public string schedule;
        public bool invert;
    }

    public class NextGenPVEEntities
    {
        public List<string> types;
        public bool custom;
    }

    private class NextGenPVEZoneMap
    {
        public List<string> map;
    }

    public static class UI
    {
        public static CuiElementContainer Container(string panel, string color, string min, string max, bool useCursor = false, string parent = "Overlay")
        {
            return new CuiElementContainer()
                {
                    {
                        new CuiPanel
                        {
                            Image = { Color = color },
                            RectTransform = {AnchorMin = min, AnchorMax = max},
                            CursorEnabled = useCursor
                        },
                        new CuiElement().Parent = parent,
                        panel
                    }
                };
        }

        public static void Panel(ref CuiElementContainer container, string panel, string color, string min, string max, bool cursor = false)
        {
            container.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = min, AnchorMax = max },
                CursorEnabled = cursor
            },
            panel);
        }

        public static void Label(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, TextAnchor align = TextAnchor.MiddleCenter)
        {
            container.Add(new CuiLabel
            {
                Text = { Color = color, FontSize = size, Align = align, Text = text },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            },
            panel);
        }

        public static void Button(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, string command, TextAnchor align = TextAnchor.MiddleCenter)
        {
            container.Add(new CuiButton
            {
                Button = { Color = color, Command = command, FadeIn = 0f },
                RectTransform = { AnchorMin = min, AnchorMax = max },
                Text = { Text = text, FontSize = size, Align = align }
            },
            panel);
        }

        public static void Input(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, string command, TextAnchor align = TextAnchor.MiddleCenter)
        {
            container.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = panel,
                Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Align = align,
                            CharsLimit = 30,
                            Color = color,
                            Command = command + text,
                            FontSize = size,
                            IsPassword = false,
                            Text = text
                        },
                        new CuiRectTransformComponent { AnchorMin = min, AnchorMax = max },
                        new CuiNeedsCursorComponent()
                    }
            });
        }

        public static void Icon(ref CuiElementContainer container, string panel, string color, string imageurl, string min, string max)
        {
            container.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = panel,
                Components =
                    {
                        new CuiRawImageComponent
                        {
                            Url = imageurl,
                            Sprite = "assets/content/textures/generic/fulltransparent.tga",
                            Color = color
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = min,
                            AnchorMax = max
                        }
                    }
            });
        }

        public static string Color(string hexColor, float alpha)
        {
            if (hexColor.StartsWith("#"))
            {
                hexColor = hexColor.Substring(1);
            }
            int red = int.Parse(hexColor.Substring(0, 2), NumberStyles.AllowHexSpecifier);
            int green = int.Parse(hexColor.Substring(2, 2), NumberStyles.AllowHexSpecifier);
            int blue = int.Parse(hexColor.Substring(4, 2), NumberStyles.AllowHexSpecifier);
            return $"{(double)red / 255} {(double)green / 255} {(double)blue / 255} {alpha}";
        }
    }
    #endregion

    #region load_defaults
    // Default entity categories and types to consider
    private void LoadDefaultEntities()
    {
        Utils.DoLog("Trying to recreate entities data...");
        SqliteCommand ct = new SqliteCommand("DROP TABLE IF EXISTS ngpve_entities", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("CREATE TABLE ngpve_entities (name varchar(32), type varchar(32), custom INTEGER(1) DEFAULT 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'BaseAnimalNPC', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'Boar', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'Bear', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'Polarbear', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'Chicken', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'Horse', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'RidableHorse', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'Stag', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'Wolf', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'SimpleShark', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('animal', 'BaseFishNPC', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('balloon', 'HotAirBalloon', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('building', 'BuildingBlock', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('building', 'BuildingPrivlidge', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('building', 'Door', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('fire', 'BaseOven', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('fire', 'FireBall', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('helicopter', 'BaseHelicopter', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('helicopter', 'HelicopterDebris', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('helicopter', 'CH47HelicopterAIController', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('highwall', 'SimpleBuildingBlock', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('highwall', 'wall.external.high.stone', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('highwall', 'wall.external.high.wood', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('minicopter', 'MiniCopter', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('mlrs', 'MLRS', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'NPCPlayer', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'BradleyAPC', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'HelicopterDebris', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'Humanoid', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'HumanNPC', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'TunnelDweller', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'UnderwaterDweller', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'BaseNpc', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'HTNPlayer', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'Murderer', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'NPCMurderer', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'NPCPlayer', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'NpcRaider', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'ScarecrowNPC', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'Scientist', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'ScientistNPC', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'Zombie', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npc', 'ZombieNPC', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('npcturret', 'NPCAutoTurret', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('plant', 'GrowableEntity', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('player', 'BasePlayer', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('resource', 'AdventCalendar', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('resource', 'BaseCorpse', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('resource', 'NPCPlayerCorpse', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('resource', 'PlayerCorpse', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('resource', 'DroppedItemContainer', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('resource', 'HorseCorpse', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('resource', 'LockedByEntCrate', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('resource', 'LootContainer', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('resource', 'ResourceEntity', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('resource', 'StorageContainer', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('scrapcopter', 'ScrapTransportHelicopter', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'AutoTurret', 0)", sqlConnection); // AutoTurret when attacked
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'BaseProjectile', 0)", sqlConnection); // AutoTurret weapon
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'Barricade', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'TrainBarricade', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'BearTrap', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'Bullet', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'FlameTurret', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'Landmine', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'GunTrap', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'ReactiveTarget', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'SamSite', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('trap', 'TeslaCoil', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'Sedan', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'ModularVehicle', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'BaseCar', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'TrainEngine', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'VehicleModuleEngine', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'VehicleModuleStorage', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'VehicleModuleSeating', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'ModularCar', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'ModularCarGarage', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'BaseSubmarine', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('vehicle', 'SubmarineDuo', 0)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_entities VALUES('elevator', 'ElevatorLift', 0)", sqlConnection);
        ct.ExecuteNonQuery();
    }

    // Default rules which can be applied
    private void LoadDefaultRules()
    {
        Utils.DoLog("Trying to recreate rules data...");
        SqliteCommand ct = new SqliteCommand("DROP TABLE IF EXISTS ngpve_rules", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("CREATE TABLE ngpve_rules (name varchar(32),description varchar(255),damage INTEGER(1) DEFAULT 1,custom INTEGER(1) DEFAULT 0,source VARCHAR(32),target VARCHAR(32));", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('npc_animal', 'NPC can damage Animal', 1, 0, 'npc', 'animal')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('npc_npc', 'NPC can damage NPC', 1, 0, 'npc', 'npc')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('npc_resource', 'NPC can damage Resource', 1, 0, 'npc', 'npc')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('npc_player', 'NPC can damage player', 1, 0, 'npc', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_plant', 'Player can damage Plants', 1, 0, 'player', 'plant')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_npc', 'Player can damage NPC', 1, 0, 'player', 'npc')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_player', 'Player can damage Player', 1, 0, 'player', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('npc_building', 'NPC can damage Building', 1, 0, 'npc', 'building')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_building', 'Player can damage Building', 1, 0, 'player', 'building')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_resource', 'Player can damage Resource', 1, 0, 'player', 'resource')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('resource_player', 'Resource can damage Player', 1, 0, 'resource', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_trap', 'Player can damage Trap', 1, 0, 'player', 'trap')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('trap_player', 'Trap can damage Player', 1, 0, 'trap', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('balloon_player', 'Balloon can damage Player', 1, 0, 'balloon', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_balloon', 'Player can damage Balloon', 1, 0, 'player', 'balloon')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_animal', 'Player can damage Animal', 1, 0, 'player', 'animal')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('animal_npc', 'Animal can damage NPC', 1, 0, 'animal', 'npc')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('animal_player', 'Animal can damage Player', 1, 0, 'animal', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('animal_animal', 'Animal can damage Animal', 1, 0, 'animal', 'animal')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('helicopter_player', 'Helicopter can damage Player', 1, 0, 'helicopter', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('helicopter_building', 'Helicopter can damage Building', 1, 0, 'helicopter', 'building')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_minicopter', 'Player can damage MiniCopter', 1, 0, 'player', 'minicopter')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('minicopter_building', 'Minicopter can damage building', 1, 0, 'minicopter', 'building')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('minicopter_player', 'MiniCopter can damage Player', 1, 0, 'minicopter', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('mlrs_building', 'MLRS can damage Building', 1, 0, 'mlrs', 'building')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('mlrs_npc', 'MLRS can damage NPC', 1, 0, 'mlrs', 'npc')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('mlrs_player', 'MLRS can damage Player', 1, 0, 'mlrs', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('mlrs_resource', 'MLRS can damage Resource', 1, 0, 'mlrs', 'resource')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_scrapcopter', 'Player can damage Scrapcopter', 1, 0, 'player', 'scrapcopter')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('scrapcopter_player', 'Scrapcopter can damage Player', 1, 0, 'scrapcopter', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('scrapcopter_building', 'Scrapcopter can damage building', 1, 0, 'scrapcopter', 'building')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_helicopter', 'Player can damage Helicopter', 1, 0, 'player', 'helicopter')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('highwall_player', 'Highwall can damage Player', 1, 0, 'highwall', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_highwall', 'Player can damage Highwall', 1, 0, 'player', 'highwall')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('npcturret_player', 'NPCAutoTurret can damage Player', 1, 0, 'npcturret', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('npcturret_animal', 'NPCAutoTurret can damage Animal', 1, 0, 'npcturret', 'animal')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('npcturret_npc', 'NPCAutoTurret can damage NPC', 1, 0, 'npcturret', 'npc')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('fire_building', 'Fire can damage building', 1, 0, 'fire', 'building')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('fire_player', 'Fire can damage Player', 1, 0, 'fire', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_fire', 'Player can damage Fire', 1, 0, 'player', 'fire')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('fire_resource', 'Fire can damage Resource', 1, 0, 'fire', 'resource')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('helicopter_animal', 'Helicopter can damage animal', 1, 0, 'helicopter', 'animal')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('helicopter_npc', 'Helicopter can damage NPC', 1, 0, 'helicopter', 'npc')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('trap_trap', 'Trap can damage trap', 1, 0, 'trap', 'trap')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('trap_npc', 'Trap can damage npc', 1, 0, 'trap', 'npc')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('trap_helicopter', 'Trap can damage helicopter', 1, 0, 'trap', 'helicopter')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('trap_balloon', 'Trap can damage balloon', 1, 0, 'trap', 'balloon')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('trap_resource', 'Trap can damage Resource', 1, 0, 'trap', 'resource')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('player_vehicle', 'Player can damage Vehicle', 1, 0, 'player', 'vehicle')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('vehicle_player', 'Vehicle can damage Player', 1, 0, 'vehicle', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('vehicle_vehicle', 'Vehicle can damage Vehicle', 1, 0, 'vehicle', 'vehicle')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('vehicle_trap', 'Vehicle can damage Trap', 1, 0, 'vehicle', 'trap')", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rules VALUES('elevator_player', 'Elevator can crush Player', 1, 0, 'elevator', 'player')", sqlConnection);
        ct.ExecuteNonQuery();
    }

    private void LoadNPCTgtExclDb()
    {
        SqliteCommand cd = new SqliteCommand("DROP TABLE IF EXISTS ngpve_targetexclusion", sqlConnection);
        cd.ExecuteNonQuery();
        cd = new SqliteCommand("CREATE TABLE ngpve_targetexclusion (name VARCHAR(255), excluded INT(1) DEFAULT 1);", sqlConnection);
        cd.ExecuteNonQuery();
    }

    private void LoadDefaultRuleset(bool drop = true)
    {
        Utils.DoLog("Trying to recreate ruleset data...");
        if (drop)
        {
            SqliteCommand cd = new SqliteCommand("DROP TABLE IF EXISTS ngpve_rulesets", sqlConnection);
            cd.ExecuteNonQuery();
            cd = new SqliteCommand("CREATE TABLE ngpve_rulesets (name VARCHAR(255), damage INTEGER(1) DEFAULT 0, enabled INTEGER(1) DEFAULT 1, automated INTEGER(1) DEFAULT 0, zone VARCHAR(255), exception VARCHAR(255), src_exclude VARCHAR(255), tgt_exclude VARCHAR(255), schedule VARCHAR(255), invschedule INT(1) DEFAULT 0)", sqlConnection);
            cd.ExecuteNonQuery();
        }
        SqliteCommand ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'animal_player', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'elevator_player', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_animal', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'animal_animal', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'animal_npc', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_balloon', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_helicopter', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_minicopter', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_scrapcopter', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_npc', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_plant', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'npc_animal', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'npc_player', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'npc_resource', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_building', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_resource', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'resource_player', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'npc_npc', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'npcturret_player', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'npcturret_animal', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'npcturret_npc', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_scrapcopter', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'scrapcopter_player', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'helicopter_animal', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'helicopter_building', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'helicopter_npc', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'helicopter_player', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'trap_trap', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'fire_player', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_fire', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'fire_resource', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'fire_building', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_trap', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_vehicle', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'vehicle_vehicle', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'vehicle_trap', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
        ct = new SqliteCommand("INSERT INTO ngpve_rulesets VALUES('default', 0, 1, 0, 0, 'highwall_player', null, null, null, null)", sqlConnection);
        ct.ExecuteNonQuery();
    }
    #endregion
}