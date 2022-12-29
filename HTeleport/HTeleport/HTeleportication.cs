#region License (GPL v2)
/*
    Teleportication - NextGen Teleportation Plugin
    Copyright (c) 2020-2022 RFC1920 <desolationoutpostpve@gmail.com>

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; version 2
    of the License only.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/
#endregion License (GPL v2)
using Auxide;
using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using Mono.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Auxide.Scripting;

internal class HTeleportication : RustScript
{
    #region vars
    private SortedDictionary<ulong, Vector3> SavedPoints = new SortedDictionary<ulong, Vector3>();
    private SortedDictionary<ulong, ulong> TPRRequests = new SortedDictionary<ulong, ulong>();
    private SortedDictionary<string, Vector3> monPos = new SortedDictionary<string, Vector3>();
    private SortedDictionary<string, Vector3> monSize = new SortedDictionary<string, Vector3>();
    private SortedDictionary<string, Vector3> cavePos = new SortedDictionary<string, Vector3>();

    private readonly Dictionary<ulong, TPTimer> TeleportTimers = new Dictionary<ulong, TPTimer>();
    private readonly Dictionary<string, Dictionary<ulong, TPTimer>> CooldownTimers = new Dictionary<string, Dictionary<ulong, TPTimer>>();
    private Dictionary<string, Dictionary<ulong, float>> DailyUsage = new Dictionary<string, Dictionary<ulong, float>>();
    private readonly Dictionary<ulong, TPRTimer> TPRTimers = new Dictionary<ulong, TPRTimer>();
    private int dateInt;

    //private Coroutine townPositionsC;
    private List<Vector3> townPositions = new List<Vector3>();

    private bool newsave;
    private const string HGUI = "gui.homes";

    private const string permTP_Use = "teleportication.use";
    private const string permTP_TP = "teleportication.tp";
    private const string permTP_TPB = "teleportication.tpb";
    private const string permTP_TPR = "teleportication.tpr";
    private const string permTP_Town = "teleportication.town";
    private const string permTP_Bandit = "teleportication.bandit";
    private const string permTP_Outpost = "teleportication.outpost";
    private const string permTP_Tunnel = "teleportication.tunnel";
    private const string permTP_Admin = "teleportication.admin";

    private ConfigData configData;
    private SqliteConnection sqlConnection;
    public TextInfo TI = CultureInfo.CurrentCulture.TextInfo;
    private string connStr;

    private readonly string logfilename = "log";

    //private readonly RustScript HEconomics;//, HLootProtect;
    private readonly IScriptReference HEconomics;

    private readonly int blockLayer = LayerMask.GetMask("Construction");

    public class TPTimer
    {
        public Timer timer;
        public float start;
        public float cooldown;
        public string type;
        public BasePlayer source;
        public string targetName;
        public Vector3 targetLocation;
        public float counter; // Request count while in cooldown to determine bypass go/no-go
    }

    public class TPRTimer
    {
        public Timer timer;
        public float start;
        public float countdown;
        public string type;
    }
    #endregion

    #region init
    public void OnServerInitialized()
    {
        sqlConnection = new SqliteConnection(connStr);
        sqlConnection.Open();

        LoadData();
        LoadConfigVariables();

        // Setup permissions from VIPSettings
        foreach (KeyValuePair<string, CmdOptions> ttype in configData.Types)
        {
            if (ttype.Value.VIPSettings == null) continue;
            if (ttype.Value.VIPSettings.Count > 0)
            {
                foreach (KeyValuePair<string, VIPSetting> x in ttype.Value.VIPSettings)
                {
                    Permissions.RegisterPermission(Name, x.Key);
                }
            }
        }

        if (configData.Options.WipeOnNewSave && newsave)
        {
            newsave = false;
            // Wipe homes and town, etc.
            CreateOrClearTables(true);
            //AutoSpawnTown();
        }

        FindMonuments();

        if (configData.Options.AddTownMapMarker)
        {
            List<string> target = (List<string>)RunSingleSelectQuery("SELECT location FROM rtp_server WHERE name='town'");
            if (target != null)
            {
                foreach (MapMarkerGenericRadius mm in UnityEngine.Object.FindObjectsOfType<MapMarkerGenericRadius>().Where(x => x.name == "town").ToList())
                {
                    mm.Kill();
                }

                Vector3 townPos = StringToVector3(target[0]);
                MapMarkerGenericRadius marker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", townPos) as MapMarkerGenericRadius;
                if (marker != null)
                {
                    marker.alpha = 0.6f;
                    marker.color1 = Color.green;
                    marker.color2 = Color.white;
                    marker.name = "town";
                    marker.radius = 0.2f;
                    marker.Spawn();
                    marker.SendUpdate();
                }
            }
        }
        MidnightDetect(true);
    }

    private void Init()
    {
        // Dummy file, creates the directory for us.
        DynamicConfigFile dataFile = data.GetDatafile(Name + "/teleportication");
        dataFile.Save();
        connStr = $"Data Source={data.Directory}{Path.DirectorySeparatorChar}{Name}{Path.DirectorySeparatorChar}teleportication.db";

        CooldownTimers.Add("Home", new Dictionary<ulong, TPTimer>());
        CooldownTimers.Add("Town", new Dictionary<ulong, TPTimer>());
        CooldownTimers.Add("TPA", new Dictionary<ulong, TPTimer>());
        CooldownTimers.Add("TPB", new Dictionary<ulong, TPTimer>());
        CooldownTimers.Add("TPR", new Dictionary<ulong, TPTimer>());
        CooldownTimers.Add("TP", new Dictionary<ulong, TPTimer>());
        CooldownTimers.Add("Bandit", new Dictionary<ulong, TPTimer>());
        CooldownTimers.Add("Outpost", new Dictionary<ulong, TPTimer>());
        CooldownTimers.Add("Tunnel", new Dictionary<ulong, TPTimer>());

        DailyUsage.Add("Home", new Dictionary<ulong, float>());
        DailyUsage.Add("Town", new Dictionary<ulong, float>());
        DailyUsage.Add("TPA", new Dictionary<ulong, float>());
        DailyUsage.Add("TPB", new Dictionary<ulong, float>());
        DailyUsage.Add("TPR", new Dictionary<ulong, float>());
        DailyUsage.Add("TP", new Dictionary<ulong, float>());
        DailyUsage.Add("Bandit", new Dictionary<ulong, float>());
        DailyUsage.Add("Outpost", new Dictionary<ulong, float>());
        DailyUsage.Add("Tunnel", new Dictionary<ulong, float>());

        //AddCovalenceCommand("home", "CmdHomeTeleport");
        //AddCovalenceCommand("homeg", "CmdHomeGUI");
        //AddCovalenceCommand("sethome", "CmdSetHome");
        //AddCovalenceCommand("town", "CmdTownTeleport");
        //AddCovalenceCommand("bandit", "CmdTownTeleport");
        //AddCovalenceCommand("outpost", "CmdTownTeleport");
        //AddCovalenceCommand("tunnel", "CmdTownTeleport");
        //AddCovalenceCommand("tpa", "CmdTpa");
        //AddCovalenceCommand("tpb", "CmdTpb");
        //AddCovalenceCommand("tpc", "CmdTpc");
        //AddCovalenceCommand("tpr", "CmdTpr");
        //AddCovalenceCommand("tp", "CmdTp");
        //AddCovalenceCommand("tpadmin", "CmdTpAdmin");

        Permissions.RegisterPermission(Name, permTP_Use);
        Permissions.RegisterPermission(Name, permTP_TPB);
        Permissions.RegisterPermission(Name, permTP_TPR);
        Permissions.RegisterPermission(Name, permTP_TP);
        Permissions.RegisterPermission(Name, permTP_Town);
        Permissions.RegisterPermission(Name, permTP_Bandit);
        Permissions.RegisterPermission(Name, permTP_Outpost);
        Permissions.RegisterPermission(Name, permTP_Tunnel);
        Permissions.RegisterPermission(Name, permTP_Admin);
    }

    public void Unload()
    {
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            CuiHelper.DestroyUi(player, HGUI);
        }
        sqlConnection.Close();
    }

    public override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            ["serverinfo"] = "Server Info - ",
            ["locations"] = "Locations:",
            ["flags"] = "Flags:",
            ["notauthorized"] = "You are not authorized for this command!",
            ["homesavail"] = "The following homes have been set:",
            ["homesavailfor"] = "The following homes have been set by {0}:",
            ["nohomes"] = "No homes have been set.",
            ["hometoomany"] = "You cannot set any more homes.  Limit is {0}.",
            ["hometooclose"] = "Too close to another home - minimum distance {0}",
            ["homeset"] = "Home {0} has been set.",
            ["homeremoved"] = "Home {0} has been removed.",
            ["setblocked"] = "Home cannot be set here - {0}",
            ["blocked"] = "You cannot teleport while blocked!",
            ["blockedinvis"] = "You cannot teleport while invisible!",
            ["invalidhome"] = "Home invalid - {0}",
            ["lastused"] = " Last used: {0} minutes ago",
            ["lastuse"] = "last use",
            ["name"] = "name",
            ["lastday"] = " Not used since server restart",
            ["list"] = "list",
            ["home"] = "Home",
            ["tpb"] = "old location",
            ["debug"] = "Debug set to {0}",
            ["tpr"] = "another player",
            ["town"] = "Town",
            ["outpost"] = "Outpost",
            ["outpostset"] = "Outpost location has been set to {0}",
            ["tunnels"] = "Available Tunnel Entrances:\n{0}",
            ["bandit"] = "Bandit",
            ["banditset"] = "Bandit Town location has been set to {0}",
            ["cooldown"] = "Currently in cooldown for {0} for another {1} seconds.",
            ["rcooldown"] = "Currently in cooldown for {0} for another {1} seconds.  Run again to pay for bypass.",
            ["rcooldown2"] = "Currently in cooldown for {0} for another {1} seconds.  Run again to pay {2} for bypass.",
            ["bypassed"] = "Cooldown  for {0} bypassed by paying {1}",
            ["limit"] = "You have hit the daily limit for {0}: ({1} of {2})",
            ["reqdenied"] = "Request to teleport to {0} was denied!",
            ["reqaccepted"] = "Request to teleport to {0} was accepted!",
            ["homemissing"] = "No such home...",
            ["notowned"] = "No privileges at the target location!",
            ["missingfoundation"] = "Foundation missing or offset.",
            ["locationnotset"] = "{0} location has not been set!",
            ["townset"] = "Town location has been set to {0}!",
            ["cavetooclose"] = "You cannot use /{0} so close to a cave.",
            ["montooclose"] = "You cannot use /{0} so close to {1}.",
            ["onhurt"] = "You cannot use /{0} while injured.",
            ["oncold"] = "You are too cold to use /{0}!",
            ["onhot"] = "You are too hot to use /{0}!",
            ["onhostile"] = "You are marked as hostile and cannot use /{0} for {1} minutes...",
            ["onballoon"] = "You cannot use /{0} while on a balloon.",
            ["oncargo"] = "You cannot use /{0} while on the cargo ship.",
            ["onlift"] = "You cannot use /{0} while on a lift.",
            ["onmounted"] = "You cannot use /{0} while mounted.",
            ["onswimming"] = "You cannot use /{0} while swimming.",
            ["onwater"] = "You cannot use /{0} above water.",
            ["intunnel"] = "You cannot use /{0} to/from the tunnel system.",
            ["safezone"] = "You cannot use /{0} from a safe zone.",
            ["remaining"] = "You have {0} {1} teleports remaining for today.",
            ["teleporting"] = "Teleporting to {0} in {1} seconds...",
            ["sortedby"] = "sorted by {0}",
            ["noprevious"] = "No previous location saved.",
            ["teleportinghome"] = "Teleporting to home {0} in {1} seconds...",
            ["BackupDone"] = "Teleportication database has been backed up to {0}",
            ["importhelp"] = "/tpadmin import {r/n} {y/1/yes/true}\n\t import RTeleportion or NTeleportation\n\tadd y or 1 or true to actually import\n\totherwise display data only",
            ["tphelp"] = "/tp X,Z OR /tp X,Y,Z -- e.g. /tp 121,-535 will teleport the player to that location on the map.\nIf Y is not specified, player will be moved to ground level.",
            ["cannottp"] = "Cannot teleport to desired location.",
            ["obstructed"] = "The target location is too close to construction.",
            ["importdone"] = "Homes have been imported from datafile '{0}'",
            ["importing"] = "Importing data for {0}",
            ["tpcancelled"] = "Teleport cancelled!",
            ["tprself"] = "You cannot tpr to yourself.",
            ["tprnotify"] = "{0} has requested to be teleported to you.\nType /tpa to accept.",
            ["tpanotify"] = "{0} has accepted your teleport request.  You will be teleported in {1} seconds.",
            ["tprreject"] = "{0} rejected your request.  Or, the request timed out."
        }, Name);
    }

    private void OnNewSave()
    {
        newsave = true;
    }

    private void LoadData()
    {
        bool found = false;
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand r = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='rtp_server'", c))
            using (SqliteDataReader rtbl = r.ExecuteReader())
            {
                while (rtbl.Read()) { found = true; }
            }
        }
        if (!found) CreateOrClearTables(true);
    }
    #endregion

    #region commands
    //[Command("tp")]
    private void CmdTp(BasePlayer player, string command, string[] args)
    {
        if (configData.Options.debug) { string debug = string.Join(",", args); Utils.DoLog($"{debug}"); }

        if (!Permissions.UserHasPermission(permTP_TP, player.UserIDString)) { Message(player, "notauthorized"); return; }
        if (args.Length > 0)
        {
            string[] input = args[0].Split(',');
            if (input.Length > 1)
            {
                ulong userid = ulong.Parse(player.UserIDString);
                string parsed = null;
                Vector3 pos = new Vector3();
                if (input.Length == 3)
                {
                    parsed = input[0] + "," + input[1] + "," + input[2];
                    pos = StringToVector3(parsed);
                }
                else
                {
                    parsed = input[0] + ",0," + input[1];
                    pos = StringToVector3(parsed);
                    if (TerrainMeta.HeightMap.GetHeight(pos) > pos.y)
                    {
                        // Ensure they are sent above the terrain
                        pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                    }
                }

                if (CanTeleport(player, parsed, "TP"))
                {
                    if (!TeleportTimers.ContainsKey(userid))
                    {
                        AddTimer(player, pos, "TP", "TP");
                        HandleTimer(userid, "TP", true);
                        if (CooldownTimers["TP"].ContainsKey(userid))
                        {
                            CooldownTimers["TP"][userid].timer.Destroy();
                            CooldownTimers["TP"].Remove(userid);
                        }
                        AddCooldown(player, pos, "TP", "TP");
                        HandleCooldown(userid, "TP", true);
                    }
                    else if (TeleportTimers[userid].cooldown == 0)
                    {
                        Teleport(player, pos, "TP");
                    }
                }
            }
        }
        else
        {
            Message(player, "tphelp");
        }
    }

    //[Command("tpadmin")]
    private void CmdTpAdmin(BasePlayer player, string command, string[] args)
    {
        if (configData.Options.debug) { string debug = string.Join(",", args); Utils.DoLog($"{debug}"); }

        if (!Permissions.UserHasPermission(permTP_Admin, player.UserIDString)) { Message(player, "notauthorized"); return; }
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "wipe":
                    Message(player, "Wiping data!");
                    CreateOrClearTables(true);
                    //AutoSpawnTown();
                    FindMonuments();
                    break;
                case "info":
                    Message(player, Title);
                    Message(player, "locations");
                    string loc = null;
                    using (SqliteConnection c = new SqliteConnection(connStr))
                    {
                        c.Open();
                        using (SqliteCommand q = new SqliteCommand($"SELECT name, location FROM rtp_server ORDER BY name", c))
                        using (SqliteDataReader svr = q.ExecuteReader())
                        {
                            while (svr.Read())
                            {
                                string nm = svr.GetValue(0).ToString();
                                string lc = svr.GetValue(1).ToString();
                                loc += "\t" + TI.ToTitleCase(nm) + ": " + lc.TrimEnd() + "\n";
                            }
                        }
                    }
                    Message(player, loc);

                    string flags = "\tHomeRequireFoundation:\t" + configData.Options.HomeRequireFoundation.ToString() + "\n"
                        + "\tStrictFoundationCheck:\t" + configData.Options.StrictFoundationCheck.ToString() + "\n"
                        + "\tHomeRemoveInvalid:\t" + configData.Options.HomeRemoveInvalid.ToString() + "\n"
                        + "\tHonorBuildingPrivilege:\t" + configData.Options.HonorBuildingPrivilege.ToString() + "\n"
                        + "\tHonorRelationships:\t" + configData.Options.HonorRelationships.ToString() + "\n"
                        + "\tAutoGenBandit:\t" + configData.Options.AutoGenBandit.ToString() + "\n"
                        + "\tAutoGenOutpost:\t" + configData.Options.AutoGenOutpost.ToString() + "\n"
                        + "\tHomeMinimumDistance:\t" + configData.Options.HomeMinimumDistance.ToString() + "\n"
                        + "\tDefaultMonoumentSize:\t" + configData.Options.DefaultMonumentSize.ToString() + "\n"
                        + "\tCaveDistanceSmall:\t" + configData.Options.CaveDistanceSmall.ToString() + "\n"
                        + "\tCaveDistanceMedium:\t" + configData.Options.CaveDistanceMedium.ToString() + "\n"
                        + "\tCaveDistanceLarge:\t" + configData.Options.CaveDistanceLarge.ToString() + "\n"
                        + "\tMinimumTemp:\t" + configData.Options.MinimumTemp.ToString() + "\n"
                        + "\tMaximumTemp:\t" + configData.Options.MaximumTemp.ToString() + "\n"
                        + "\tSetCommand:\t" + configData.Options.SetCommand + "\n"
                        + "\tListCommand:\t" + configData.Options.ListCommand + "\n"
                        + "\tRemoveCommand:\t" + configData.Options.RemoveCommand;
                    Message(player, "flags");
                    Message(player, flags);

                    break;
                case "debug":
                    configData.Options.debug = !configData.Options.debug;
                    Message(player, "debug", configData.Options.debug.ToString());
                    break;
            }
        }
    }

    //[Command("sethome")]
    private void CmdSetHome(BasePlayer player, string command, string[] args)
    {
        if (args.Length == 1) CmdHomeTeleport(player, "home", new string[] { configData.Options.SetCommand, args[0] });
    }

    //[Command("home")]
    private void CmdHomeTeleport(BasePlayer player, string command, string[] args)
    {
        if (configData.Options.debug) { string debug = string.Join(",", args); Utils.DoLog($"{debug}"); }

        if (!Permissions.UserHasPermission(permTP_Use, player.UserIDString)) { Message(player, "notauthorized"); return; }
        if (player.UserIDString == "server_console") return;

        if (args.Length < 1 || (args.Length == 1 && args[0] == configData.Options.ListCommand))
        {
            // List homes
            string available = Lang("homesavail") + "\n";
            bool hashomes = false;
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                string qh = $"SELECT name, location, lastused FROM rtp_player WHERE userid='{player.userID}'";
                //Utils.DoLog(qh);
                using (SqliteCommand q = new SqliteCommand(qh, c))
                using (SqliteDataReader home = q.ExecuteReader())
                {
                    while (home.Read())
                    {
                        string test = home.GetValue(0).ToString();
                        Vector3 position = StringToVector3(home.GetValue(1).ToString());
                        string pos = PositionToGrid(position);

                        if (test != "")
                        {
                            string timesince = Math.Floor((Time.realtimeSinceStartup / 60) - (Convert.ToSingle(home.GetString(2)) / 60)).ToString();
                            if (int.Parse(timesince) < 0)
                            {
                                available += test + ": " + position + " [" + pos + "] " + Lang("lastday") + "\n";
                            }
                            else
                            {
                                available += test + ": " + position + " [" + pos + "] " + Lang("lastused", null, timesince) + "\n";
                            }
                            hashomes = true;
                        }
                    }
                }
            }
            if (hashomes)
            {
                Message(player, available);
            }
            else
            {
                Message(player, "nohomes");
            }
        }
        else if (args.Length == 2 && (args[0] == configData.Options.ListCommand) && configData.Options.HonorRelationships)
        {
            // List a friend's homes
            BasePlayer target = BasePlayer.Find(args[1]);
            if (target != null && Utils.IsFriend(player.userID, target.userID))
            {
                string available = Lang("homesavailfor", null, RemoveSpecialCharacters(target.displayName)) + "\n";
                bool hashomes = false;
                using (SqliteConnection c = new SqliteConnection(connStr))
                {
                    c.Open();
                    using (SqliteCommand q = new SqliteCommand($"SELECT name, location, lastused FROM rtp_player WHERE userid='{target.userID}'", c))
                    using (SqliteDataReader home = q.ExecuteReader())
                    {
                        while (home.Read())
                        {
                            string test = home.GetValue(0).ToString();
                            if (test != "")
                            {
                                string timesince = Math.Floor((Time.realtimeSinceStartup / 60) - (Convert.ToSingle(home.GetString(2)) / 60)).ToString();
                                //Utils.DoLog($"Time since {timesince}");
                                available += test + ": " + home.GetString(1) + " " + Lang("lastused", null, timesince) + "\n";
                                hashomes = true;
                            }
                        }
                    }
                }
                if (hashomes)
                {
                    Message(player, available);
                }
                else
                {
                    Message(player, "nohomes");
                }
            }
            else
            {
                Message(player, "notauthorized");
            }
        }
        else if (args.Length == 2 && args[0] == configData.Options.SetCommand)
        {
            // Set home
            string reason;
            if (CanSetHome(player, player.transform.position, out reason))
            {
                string home = args[1];
                bool found = false;
                using (SqliteConnection c = new SqliteConnection(connStr))
                {
                    c.Open();
                    string q = $"SELECT name FROM rtp_player WHERE userid='{player.userID}' AND name='{home}'";
                    Utils.DoLog(q);
                    using (SqliteCommand ct = new SqliteCommand(q, c))
                    using (SqliteDataReader pl = ct.ExecuteReader())
                    {
                        while (pl.Read())
                        {
                            if (pl.GetString(0) == home) found = true;
                        }
                    }
                }
                if (found)
                {
                    RunUpdateQuery($"UPDATE rtp_player SET location='{player.transform.position}' WHERE userid='{player.userID}' AND name='{home}'");
                }
                else
                {
                    RunUpdateQuery($"INSERT INTO rtp_player VALUES('{player.userID}', '{home}', '{player.transform.position}', '{Time.realtimeSinceStartup}', 0)");
                }
                Message(player, "homeset", home);
            }
            else
            {
                Message(player, "setblocked", reason);
            }
        }
        else if (args.Length == 2 && args[0] == configData.Options.RemoveCommand)
        {
            // Remove home
            string home = args[1];
            List<string> found = (List<string>)RunSingleSelectQuery($"SELECT location FROM rtp_player WHERE userid='{player.userID}' AND name='{home}'");
            if (found != null)
            {
                RunUpdateQuery($"DELETE FROM rtp_player WHERE userid='{player.userID}' AND name='{home}'");
                Message(player, "homeremoved", home);
            }
            else
            {
                Message(player, "homemissing");
            }
        }
        else if (args.Length == 2)
        {
            // Use a friend's home: /home Playername home1
            BasePlayer target = BasePlayer.Find(args[0]);
            if (target != null && Utils.IsFriend(player.userID, target.userID))
            {
                string home = args[1];
                List<string> homes = (List<string>)RunSingleSelectQuery($"SELECT location FROM rtp_player WHERE userid='{target.userID}' AND name='{home}'");

                if (CanTeleport(player, homes[0], "Home"))
                {
                    if (!TeleportTimers.ContainsKey(player.userID))
                    {
                        AddTimer(player, StringToVector3(homes[0]), "Home", "Home");
                        HandleTimer(player.userID, "Home", true);
                        if (CooldownTimers["Home"].ContainsKey(player.userID))
                        {
                            CooldownTimers["Home"][player.userID].timer.Destroy();
                            CooldownTimers["Home"].Remove(player.userID);
                        }
                        AddCooldown(player, StringToVector3(homes[0]), "Home", "Home");
                        HandleCooldown(player.userID, "Home", true);

                        if (!DailyUsage["Home"].ContainsKey(player.userID)) DailyUsage["Home"].Add(player.userID, 0);
                        float usage = GetDailyLimit(player.userID, "Home") - DailyUsage["Home"][player.userID];
                        if (usage > 0)
                        {
                            Message(player, "remaining", usage.ToString(), "Home");
                        }

                        Message(player, "teleportinghome", home + "(" + RemoveSpecialCharacters(target.displayName) + ")", configData.Types["Home"].CountDown.ToString());
                    }
                    else if (TeleportTimers[player.userID].cooldown == 0)
                    {
                        Teleport(player, StringToVector3(homes[0]), "home");
                    }
                }
            }
        }
        else if (args.Length == 1)
        {
            CuiHelper.DestroyUi(player, HGUI);
            // Use an already set home
            string home = args[0];
            List<string> homes = (List<string>)RunSingleSelectQuery($"SELECT location FROM rtp_player WHERE userid='{player.userID}' AND name='{home}'");
            if (homes == null)
            {
                Message(player, "homemissing");
                return;
            }

            //string reason;
            //if (!CanSetHome(player, StringToVector3(homes[0]), out reason))
            //{
            //    if (configData.Options.HomeRemoveInvalid)
            //    {
            //        RunUpdateQuery($"DELETE FROM rtp_player WHERE userid='{player.userID}' AND name='{home}'");
            //    }
            //    Message(player, "invalidhome", reason);
            //    return;
            //}
            if (CanTeleport(player, homes[0], "Home"))
            {
                if (!TeleportTimers.ContainsKey(player.userID))
                {
                    AddTimer(player, StringToVector3(homes[0]), "Home", "Home");
                    HandleTimer(player.userID, "Home", true);
                    if (CooldownTimers["Home"].ContainsKey(player.userID))
                    {
                        CooldownTimers["Home"][player.userID].timer.Destroy();
                        CooldownTimers["Home"].Remove(player.userID);
                    }
                    AddCooldown(player, StringToVector3(homes[0]), "Home", "Home");
                    HandleCooldown(player.userID, "Home", true);

                    if (!DailyUsage["Home"].ContainsKey(player.userID)) DailyUsage["Home"].Add(player.userID, 0);
                    float usage = GetDailyLimit(player.userID, "Home") - DailyUsage["Home"][player.userID];
                    if (usage > 0)
                    {
                        Message(player, "remaining", usage.ToString(), "Home");
                    }

                    Message(player, "teleportinghome", home, configData.Types["Home"].CountDown.ToString());
                }
                else if (TeleportTimers[player.userID].cooldown == 0)
                {
                    Teleport(player, StringToVector3(homes[0]), "home");
                }
            }
        }
    }

    //[Command("homeg")]
    private void CmdHomeGUI(BasePlayer player, string command, string[] args)
    {
        if (configData.Options.debug) { string debug = string.Join(",", args); Utils.DoLog($"{debug}"); }

        if (!Permissions.UserHasPermission(permTP_Use, player.UserIDString)) { Message(player, "notauthorized"); return; }
        if (player.UserIDString == "server_console") return;

        string sort = "alpha";

        if (args.Length > 0)
        {
            sort = args[0];
        }
        if (sort == "closeit")
        {
            CuiHelper.DestroyUi(player, HGUI);
            return;
        }
        HomeGUI(player, sort);
    }

    //[Command("town")]
    private void CmdTownTeleport(BasePlayer player, string command, string[] args)
    {
        if (player.UserIDString == "server_console") return;
        if (args.Length > 0 && args[0] == configData.Options.SetCommand)
        {
            if (!Permissions.UserHasPermission(permTP_Admin, player.UserIDString)) { Message(player, "notauthorized"); return; }
            RunUpdateQuery($"INSERT OR REPLACE INTO rtp_server VALUES('{command}', '{player.transform.position}')");
            switch (command)
            {
                case "town":
                    Message(player, "townset", player.transform.position.ToString());
                    if (configData.Options.AddTownMapMarker)
                    {
                        foreach (MapMarkerGenericRadius mm in UnityEngine.Object.FindObjectsOfType<MapMarkerGenericRadius>().Where(x => x.name == "town").ToList())
                        {
                            mm.Kill();
                        }
                        MapMarkerGenericRadius marker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", player.transform.position) as MapMarkerGenericRadius;
                        if (marker != null)
                        {
                            marker.alpha = 0.6f;
                            marker.color1 = Color.green;
                            marker.color2 = Color.white;
                            marker.name = "town";
                            marker.radius = 0.2f;
                            marker.Spawn();
                            marker.SendUpdate();
                        }
                    }
                    //if (configData.Options.TownZoneId.Length > 0)
                    //{
                    //    string[] zone_args = { "name", "Town", "radius", "150" };
                    //    object ckzone = ZoneManager?.Call("CheckZoneID", configData.Options.TownZoneId);

                    //    if (ckzone != null && ckzone is bool && !(bool)ckzone)
                    //    {
                    //        configData.Options.TownZoneId = UnityEngine.Random.Range(1, 99999999).ToString();
                    //        SaveConfig(configData);
                    //    }
                    //    if (configData.Options.TownZoneEnterMessage.Length > 0)
                    //    {
                    //        List<string> arglist = zone_args.ToList();
                    //        arglist.Add("enter_message");
                    //        arglist.Add(configData.Options.TownZoneEnterMessage);
                    //        zone_args = arglist.ToArray();
                    //    }
                    //    if (configData.Options.TownZoneLeaveMessage.Length > 0)
                    //    {
                    //        List<string> arglist = zone_args.ToList();
                    //        arglist.Add("leave_message");
                    //        arglist.Add(configData.Options.TownZoneLeaveMessage);
                    //        zone_args = arglist.ToArray();
                    //    }
                    //    ZoneManager?.Call("CreateOrUpdateZone", configData.Options.TownZoneId, zone_args, player.transform.position);
                    //    if (configData.Options.TownZoneFlags.Count > 0)
                    //    {
                    //        foreach (string flag in configData.Options.TownZoneFlags)
                    //        {
                    //            ZoneManager?.Call("AddFlag", configData.Options.TownZoneId, flag);
                    //        }
                    //    }
                    //}
                    break;
                case "bandit":
                    Message(player, "banditset", player.transform.position.ToString());
                    break;
                case "outpost":
                    Message(player, "outpostset", player.transform.position.ToString());
                    break;
            }
            return;
        }

        switch (command)
        {
            case "tunnel":
                if (!Permissions.UserHasPermission(permTP_Tunnel, player.UserIDString)) { Message(player, "notauthorized"); return; }
                string dtarget = null;
                if (args.Length > 0)
                {
                    dtarget = string.Join(" ", args);
                }
                if (dtarget == null)
                {
                    string res = "";
                    using (SqliteConnection c = new SqliteConnection(connStr))
                    {
                        c.Open();
                        using (SqliteCommand q = new SqliteCommand($"SELECT name FROM rtp_server WHERE name LIKE '%Tunnel%' ORDER BY name", c))
                        using (SqliteDataReader svr = q.ExecuteReader())
                        {
                            while (svr.Read())
                            {
                                string nm = svr.GetValue(0).ToString().Replace("Tunnel", "");
                                res += $" {nm}\n";
                            }
                        }
                    }
                    Message(player, "tunnels", res);
                    return;
                }
                const string dtype = "Tunnel";
                List<string> tunnel = (List<string>)RunSingleSelectQuery($"SELECT location FROM rtp_server WHERE name='Tunnel {dtarget}'");

                if (tunnel != null)
                {
                    if (CanTeleport(player, tunnel[0], dtype))
                    {
                        if (!TeleportTimers.ContainsKey(player.userID))
                        {
                            AddTimer(player, StringToVector3(tunnel[0]), dtype, Lang("town"));
                            HandleTimer(player.userID, dtype, true);
                            if (CooldownTimers[dtype].ContainsKey(player.userID))
                            {
                                CooldownTimers[dtype][player.userID].timer.Destroy();
                                CooldownTimers[dtype].Remove(player.userID);
                            }
                            AddCooldown(player, StringToVector3(tunnel[0]), dtype, Lang("town"));
                            HandleCooldown(player.userID, dtype, true);

                            if (!DailyUsage[dtype].ContainsKey(player.userID)) DailyUsage[dtype].Add(player.userID, 0);
                            float usage = GetDailyLimit(player.userID, dtype) - DailyUsage[dtype][player.userID];
                            if (usage > 0)
                            {
                                Message(player, "remaining", usage.ToString(), Lang(dtype));
                            }

                            Message(player, "teleporting", command, configData.Types[dtype].CountDown.ToString());
                        }
                        else if (TeleportTimers[player.userID].cooldown == 0)
                        {
                            Teleport(player, StringToVector3(tunnel[0]), command);
                        }
                    }
                    break;
                }
                Message(player, "locationnotset", Lang(command));
                break;
            case "town":
                if (!Permissions.UserHasPermission(permTP_Town, player.UserIDString)) { Message(player, "notauthorized"); return; }
                goto case "all";
            case "bandit":
                if (!Permissions.UserHasPermission(permTP_Bandit, player.UserIDString)) { Message(player, "notauthorized"); return; }
                goto case "all";
            case "outpost":
                if (!Permissions.UserHasPermission(permTP_Outpost, player.UserIDString)) { Message(player, "notauthorized"); return; }
                goto case "all";
            case "all":
                List<string> target = (List<string>)RunSingleSelectQuery($"SELECT location FROM rtp_server WHERE name='{command}'");
                string type = TI.ToTitleCase(command);
                if (target != null)
                {
                    if (CanTeleport(player, target[0], type))
                    {
                        if (!TeleportTimers.ContainsKey(player.userID))
                        {
                            AddTimer(player, StringToVector3(target[0]), type, Lang("town"));
                            HandleTimer(player.userID, type, true);
                            if (CooldownTimers[type].ContainsKey(player.userID))
                            {
                                CooldownTimers[type][player.userID].timer.Destroy();
                                CooldownTimers[type].Remove(player.userID);
                            }
                            AddCooldown(player, StringToVector3(target[0]), type, Lang("town"));
                            HandleCooldown(player.userID, type, true);
                            if (!DailyUsage[dtype].ContainsKey(player.userID)) DailyUsage[dtype].Add(player.userID, 0);
                            float usage = GetDailyLimit(player.userID, dtype) - DailyUsage[dtype][player.userID];
                            if (usage > 0)
                            {
                                Message(player, "remaining", usage.ToString(), Lang(dtype));
                            }

                            Message(player, "teleporting", command, configData.Types[type].CountDown.ToString());
                        }
                        else if (TeleportTimers[player.userID].cooldown == 0)
                        {
                            Teleport(player, StringToVector3(target[0]), command);
                        }
                    }
                    break;
                }
                Message(player, "locationnotset", Lang(command));
                break;
            default:
                Message(player, "locationnotset", Lang(command));
                break;
        }
    }

    //[Command("tpb")]
    private void CmdTpb(BasePlayer player, string command, string[] args)
    {
        if (player.UserIDString== "server_console") return;
        if (!Permissions.UserHasPermission(permTP_TPB, player.UserIDString)) { Message(player, "notauthorized"); return; }
        if (SavedPoints.ContainsKey(player.userID))
        {
            Vector3 oldloc = SavedPoints[player.userID];

            if (CanTeleport(player, oldloc.ToString(), "TPB"))
            {
                if (TeleportTimers.ContainsKey(player.userID)) TeleportTimers.Remove(player.userID);
                AddTimer(player, oldloc, "TPB", Lang("tpb"));
                HandleTimer(player.userID, "TPB", true);
                if (CooldownTimers["TPB"].ContainsKey(player.userID))
                {
                    CooldownTimers["TPB"][player.userID].timer.Destroy();
                    CooldownTimers["TPB"].Remove(player.userID);
                }
                AddCooldown(player, oldloc, "TPB", Lang("tpb"));
                HandleCooldown(player.userID, "TPB", true);
                if (!DailyUsage["TPB"].ContainsKey(player.userID)) DailyUsage["TPB"].Add(player.userID, 0);
                float usage = GetDailyLimit(player.userID, "TPB") - DailyUsage["TPB"][player.userID];
                if (usage > 0)
                {
                    Message(player, "remaining", usage.ToString(), "TPB");
                }

                Message(player, "teleporting", Lang("tpb"), configData.Types["TPB"].CountDown.ToString());
                SavedPoints.Remove(player.userID);
            }
        }
        else
        {
            Message(player, "noprevious");
        }
    }

    //[Command("tpc")]
    private void CmdTpc(BasePlayer player, string command, string[] args)
    {
        if (player.UserIDString == "server_console") return;
        HandleTimer(player.userID, "tpc");
        Message(player, "tpcancelled");
    }

    //[Command("tpr")]
    private void CmdTpr(BasePlayer player, string command, string[] args)
    {
        if (player.UserIDString == "server_console") return;

        if (configData.Options.debug) { string debug = string.Join(",", args); Utils.DoLog($"{debug}"); }

        if (!Permissions.UserHasPermission(permTP_TPR, player.UserIDString)) { Message(player, "notauthorized"); return; }
        if (args.Length == 1)
        {
            BasePlayer target = FindPlayerByName(args[0]);
            if (target != null)
            {
                ulong sourceId = Convert.ToUInt64(player.UserIDString);
                ulong targetId = target.userID;
                if (sourceId == targetId)
                {
                    if (configData.Options.debug)
                    {
                        Utils.DoLog("Allowing tpr to self in debug mode.");
                    }
                    else
                    {
                        Message(player, "tprself");
                        return;
                    }
                }
                if (configData.Types["TPR"].AutoAccept)
                {
                    if (Utils.IsFriend(sourceId, targetId))
                    {
                        DoLog("AutoTPA!");
                        if (TeleportTimers.ContainsKey(sourceId)) TeleportTimers.Remove(sourceId);
                        AddTimer(player, target.transform.position, "TPR", player.displayName);
                        //TeleportTimers.Add(sourceId, new TPTimer() { type = "TPR", start = Time.realtimeSinceStartup, countdown = configData.Types["TPR"].CountDown, source = (player), targetName = player.Name, targetLocation = target.transform.position });
                        HandleTimer(sourceId, "TPR", true);
                    }
                }
                else
                {
                    TPRSetup(sourceId, targetId);
                }
            }
        }
    }

    //[Command("tpa")]
    private void CmdTpa(BasePlayer player, string command, string[] args)
    {
        if (player.UserIDString == "server_console") return;
        DoLog($"Checking for tpr request for {player.UserIDString}");
        if (TPRRequests.ContainsValue(Convert.ToUInt64(player.UserIDString)))
        {
            ulong sourceId = TPRRequests.FirstOrDefault(x => x.Value == Convert.ToUInt64(player.UserIDString)).Key;
            DoLog($"Found a request from {sourceId}");
            BasePlayer src = BasePlayer.Find(sourceId.ToString());
            if (src != null)
            {
                DoLog($"Setting timer for {src.displayName} to tp to {player.displayName}");
                if (TeleportTimers.ContainsKey(sourceId)) TeleportTimers.Remove(sourceId);
                BasePlayer srcpl = src;
                BasePlayer pl = player;
                TeleportTimers.Add(sourceId, new TPTimer() { type = "TPR", start = Time.realtimeSinceStartup, cooldown = configData.Types["TPR"].CountDown, source = srcpl, targetName = player.displayName, targetLocation = pl.transform.position });
                HandleTimer(sourceId, "TPR", true);

                if (!DailyUsage["TPR"].ContainsKey(srcpl.userID)) DailyUsage["TPR"].Add(srcpl.userID, 0);
                float usage = GetDailyLimit(srcpl.userID, "TPR") - DailyUsage["TPR"][srcpl.userID];
                if (usage > 0)
                {
                    Message(player, "remaining", usage.ToString(), "TPR");
                }

                Message(src, "tpanotify", player.displayName, configData.Types["TPR"].CountDown.ToString());
            }
        }
    }
    #endregion

    #region InboundHooks
    private bool AddServerTp(string name, Vector3 location) => SetServerTp(name, location);

    private bool SetServerTp(string name, Vector3 location)
    {
        List<string> reserved = new List<string>() { "bandit", "outpost", "town" };
        if (reserved.Contains(name)) return false;

        List<string> target = (List<string>)RunSingleSelectQuery($"SELECT location FROM rtp_server WHERE name='{name}'");
        if (target == null) return false;

        RunUpdateQuery($"INSERT OR REPLACE INTO rtp_server VALUES('{name}', '{location}')");
        return true;
    }

    private bool RemoveServerTp(string name) => UnsetServerTp(name);

    private bool UnsetServerTp(string name)
    {
        List<string> reserved = new List<string>() { "bandit", "outpost", "town" };
        if (reserved.Contains(name)) return false;

        List<string> target = (List<string>)RunSingleSelectQuery($"SELECT location FROM rtp_server WHERE name='{name}')");
        if (target == null || target.Count == 0) return false;

        RunUpdateQuery($"DELETE FROM rtp_server WHERE name='{name}'");
        return true;
    }

    private object GetServerTp(string name = "")
    {
        if (name.Length > 0)
        {
            List<string> target = (List<string>)RunSingleSelectQuery($"SELECT location FROM rtp_server WHERE name='{name}'");
            if (target == null || target.Count == 0) return false;

            Vector3 pos = StringToVector3(target[0]);
            if (pos != default(Vector3) && pos != Vector3.zero) return pos;
        }

        Dictionary<string, Vector3> targets = new Dictionary<string, Vector3>();

        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            const string qh = "SELECT name, location, FROM rtp_server";
            using (SqliteCommand q = new SqliteCommand(qh, c))
            using (SqliteDataReader tgts = q.ExecuteReader())
            {
                while (tgts.Read())
                {
                    string nom = tgts.GetValue(0).ToString();
                    string loc = tgts.GetValue(1).ToString();
                    targets.Add(name, StringToVector3(loc));
                }
            }
        }

        if (targets == null || targets.Count == 0) return null;
        return targets;
    }

    private bool ResetServerTp()
    {
        List<string> target = (List<string>)RunSingleSelectQuery("SELECT location FROM rtp_server WHERE name NOT IN ('town', 'outpost', 'bandit')");
        if (target == null || target.Count == 0) return false;

        RunUpdateQuery("DELETE FROM rtp_server WHERE name NOT IN ('town', 'outpost', 'bandit')");
        return true;
    }
    #endregion

    #region main
    private void TPRSetup(ulong sourceId, ulong targetId)
    {
        //if (TPRRequests.ContainsValue(targetId))
        //{
        //    foreach (KeyValuePair<ulong, ulong> item in TPRRequests.Where(kvp => kvp.Value == targetId).ToList())
        //    {
        //        TPRRequests.Remove(item.Key);
        //    }
        //}
        if (TPRRequests.ContainsKey(sourceId)) TPRRequests.Remove(sourceId);
        TPRRequests.Add(sourceId, targetId);

        if (TPRTimers.ContainsKey(sourceId)) TPRTimers.Remove(sourceId);
        TPRTimers.Add(sourceId, new TPRTimer() { type = "TPR", start = Time.realtimeSinceStartup, countdown = configData.Types["TPR"].CountDown });
        HandleTimer(sourceId, "TPR", true);
        //NextTick(() => TPRNotification());
        TPRNotification();
    }

    private void TPRNotification(bool reject = false)
    {
        foreach (KeyValuePair<ulong, ulong> req in TPRRequests)
        {
            if (TPRTimers.ContainsKey(req.Key))
            {
                BasePlayer src = BasePlayer.Find(req.Key.ToString());
                BasePlayer tgt = BasePlayer.Find(req.Value.ToString());
                if (reject)
                {
                    Message(src, "tprreject", req.Value.ToString());
                    TPRTimers[req.Key].timer.Destroy();
                    TPRTimers.Remove(req.Key);
                    return;
                }
                Message(tgt, "tprnotify", src.displayName);
                TPRTimers[req.Key].timer.Destroy();
            }
        }
    }

    private bool CanTeleport(BasePlayer player, string location, string type, bool requester = true)
    {
        // OBSTRUCTION
        if (type == "TP" && Obstructed(StringToVector3(location)))
        {
            Message(player, "obstructed");
            return false;
        }

        // LIMIT
        DoLog($"Checking daily usage vs. limit for {player.displayName} for {type}");
        float limit = 0;
        if (AtLimit(player.userID, type, out limit))
        {
            Message(player, "limit", type.ToLower(), DailyUsage[type][player.userID].ToString(), limit.ToString());
            return false;
        }

        // COOLDOWN
        float cooldown = 0;
        double bypass = 0;
        if (CheckCooldown(player.userID, type, out cooldown, out bypass))
        {
            if (bypass > 0)
            {
                Message(player, "bypassed", type.ToLower(), bypass.ToString());
            }
            else if (configData.Options.useEconomics || configData.Options.useServerRewards)
            {
                Message(player, "rcooldown2", type.ToLower(), cooldown, configData.Types[type].BypassAmount.ToString());
                return false;
            }
            else
            {
                Message(player, "cooldown", type.ToLower(), cooldown);
                return false;
            }
        }

        // HOSTILE
        BaseCombatEntity pbce = player as BaseCombatEntity;
        if (pbce.IsHostile() && configData.Types[type].BlockOnHostile)
        {
            float unHostileTime = (float)player.State.unHostileTimestamp;
            float currentTime = (float)Network.TimeEx.currentTimestamp;
            string pt = ((int)Math.Abs(unHostileTime - currentTime) / 60).ToString();
            if ((unHostileTime - currentTime) < 60) pt = "<1";
            Message(player, "onhostile", type, pt);
            return false;
        }

        string monName = NearMonument(player);
        if (monName != null)
        {
            if (monName.Contains("Oilrig") && configData.Types[type].BlockOnRig)
            {
                Message(player, "montooclose", type.ToLower(), monName);
                return false;
            }
            else if (monName.Contains("Excavator") && configData.Types[type].BlockOnExcavator)
            {
                Message(player, "montooclose", type.ToLower(), monName);
                return false;
            }
            else if (configData.Types[type].BlockOnMonuments)
            {
                Message(player, "montooclose", type.ToLower(), monName);
                return false;
            }
        }

        string cave = NearCave(player);
        if (cave != null && configData.Types[type].BlockOnCave)
        {
            Message(player, "cavetooclose", cave);
            return false;
        }
        if (player.InSafeZone() && configData.Types[type].BlockOnSafe)
        {
            Message(player, "safezone", type.ToLower());
            return false;
        }

        CargoShip oncargo = player.GetComponentInParent<CargoShip>();
        if (oncargo && configData.Types[type].BlockOnCargo)
        {
            Message(player, "oncargo", type.ToLower());
            return false;
        }
        HotAirBalloon onballoon = player.GetComponentInParent<HotAirBalloon>();
        if (onballoon && configData.Types[type].BlockOnBalloon)
        {
            Message(player, "onballoon", type.ToLower());
            return false;
        }
        Lift onlift = player.GetComponentInParent<Lift>();
        if (onlift && configData.Types[type].BlockOnLift)
        {
            Message(player, "onlift", type.ToLower());
            return false;
        }

        if (InTunnel(player) && configData.Types[type].BlockInTunnel)
        {
            Message(player, "intunnel", type.ToLower());
            return false;
        }
        if (AboveWater(player) && configData.Types[type].BlockOnWater)
        {
            Message(player, "onwater", type.ToLower());
            return false;
        }
        if (player.IsSwimming() && configData.Types[type].BlockOnSwimming)
        {
            Message(player, "onswimming", type.ToLower());
            return false;
        }
        if (player.IsWounded() && requester && configData.Types[type].BlockOnHurt)
        {
            Message(player, "onhurt", type.ToLower());
            return false;
        }
        if (player.metabolism.temperature.value <= configData.Options.MinimumTemp && configData.Types[type].BlockOnCold)
        {
            Message(player, "oncold", type.ToLower());
            return false;
        }
        if (player.metabolism.temperature.value >= configData.Options.MaximumTemp && configData.Types[type].BlockOnHot)
        {
            Message(player, "onhot", type.ToLower());
            return false;
        }
        if (player.isMounted && configData.Types[type].BlockOnMounted)
        {
            Message(player, "onmounted", type.ToLower());
            return false;
        }

        // Passed!
        return true;
    }

    private bool CanSetHome(BasePlayer player, Vector3 position, out string reason)
    {
        reason = null;
        bool rtrn = true;

        List<string> checkhome = (List<string>)RunSingleSelectQuery($"SELECT location FROM rtp_player WHERE userid='{player.userID}'");
        if (checkhome != null)
        {
            float homelimit = configData.Types["Home"].HomesLimit;
            string isvip = "";
            // Check all listed VIP permissions, if any, and set the user's limit to that if they have that permission
            // Use the maximum value obtained among all matching vip permissions.
            if (configData.Types["Home"].VIPSettings != null)
            {
                foreach (KeyValuePair<string, VIPSetting> vip in configData.Types["Home"].VIPSettings)
                {
                    if (Permissions.UserHasPermission(vip.Key, player.UserIDString) && vip.Value.VIPHomesLimit > homelimit)
                    {
                        isvip = $" (from {vip.Key} permission)";
                        homelimit = vip.Value.VIPHomesLimit;
                    }
                }
            }
            DoLog($"Homelimit for {player.displayName}, set to {homelimit}{isvip}.");

            if (homelimit > 0 && checkhome.Count >= homelimit)
            {
                reason = Lang("hometoomany", null, homelimit.ToString());
                return false;
            }

            foreach (string home in checkhome)
            {
                if (Vector3.Distance(player.transform.position, StringToVector3(home)) < configData.Options.HomeMinimumDistance)
                {
                    reason = Lang("hometooclose", null, configData.Options.HomeMinimumDistance.ToString());
                    return false;
                }
            }
        }
        if (configData.Options.HomeRequireFoundation)
        {
            DoLog($"Checking for foundation/floor at target {position}");
            RaycastHit hitinfo;
            if (Physics.Raycast(position, Vector3.down, out hitinfo, 0.2f, blockLayer))
            {
                BaseEntity entity = hitinfo.GetEntity();
                if (entity.ShortPrefabName.Equals("foundation") || entity.ShortPrefabName.Equals("floor")
                    || entity.ShortPrefabName.Equals("foundation.triangle") || entity.ShortPrefabName.Equals("floor.triangle")
                    || position.y < entity.WorldSpaceBounds().ToBounds().max.y)
                {
                    DoLog("  Found one.  Checking block perms, etc...");
                    rtrn = true;
                    if (!BlockCheck(entity, player, position, out reason, configData.Options.HonorBuildingPrivilege))
                    {
                        rtrn = false;
                    }
                }
            }
            else
            {
                reason = Lang("missingfoundation");
                rtrn = false;
            }
        }

        return rtrn;
    }

    private bool BlockCheck(BaseEntity entity, BasePlayer player, Vector3 position, out string reason, bool checktc = false)
    {
        reason = null;
        DoLog($"BlockCheck() called for {entity.ShortPrefabName}");
        if (configData.Options.StrictFoundationCheck)
        {
            Vector3 center = entity.CenterPoint();

            List<BaseEntity> ents = new List<BaseEntity>();
            Vis.Entities(center, 1.5f, ents);
            foreach (BaseEntity wall in ents)
            {
                if (wall.name.Contains("external.high"))
                {
                    DoLog($"    Found: {wall.name} @ center {center}, pos {position}");
                    reason = Lang("highwall");
                    return false;
                }
            }
            DoLog($"  Checking block: {entity.name} @ center {center}, pos: {position}");
            if (entity.PrefabName.Contains("triangle.prefab"))
            {
                if (Math.Abs(center.x - position.x) < 0.46f && Math.Abs(center.z - position.z) < 0.46f)
                {
                    DoLog($"    Found: {entity.ShortPrefabName} @ center: {center}, pos: {position}");
                    if (checktc && !CheckCupboardBlock(entity as BuildingBlock, player))
                    {
                        reason = Lang("notowned");
                        return false;
                    }

                    return true;
                }
            }
            else if (entity.ShortPrefabName.Equals("foundation") || entity.ShortPrefabName.Equals("floor"))
            {
                if (Math.Abs(center.x - position.x) < 0.7f && Math.Abs(center.z - position.z) < 0.7f)
                {
                    DoLog($"    Found: {entity.ShortPrefabName} @ center: {center}, pos: {position}");
                    if (checktc && !CheckCupboardBlock(entity as BuildingBlock, player))
                    {
                        reason = Lang("notowned");
                        return false;
                    }

                    return true;
                }
            }
        }
        else if (checktc)
        {
            if (!CheckCupboardBlock(entity as BuildingBlock, player))
            {
                DoLog("No strict foundation check, but HonorBuildingPrivilege true - no perms");
                reason = Lang("notowned");
                return false;
            }
        }

        return false;
    }

    // Check that a building block is owned by/attached to a cupboard and that the user has privileges
    private bool CheckCupboardBlock(BuildingBlock block, BasePlayer player)
    {
        BuildingManager.Building building = block.GetBuilding();

        if (building != null)
        {
            if (building.GetDominatingBuildingPrivilege() == null)
            {
                return false;
            }

            if (building.buildingPrivileges == null)
            {
                return false;
            }
            DoLog("Building priv not null, checking authorizedPlayers...");
            foreach (BuildingPrivlidge priv in building.buildingPrivileges)
            {
                foreach (ulong auth in priv.authorizedPlayers.Select(x => x.userid).ToArray())
                {
                    // If the player is authed, or is a friend of the authed player, return true if HonorRelationships is enabled.
                    // This should avoid TP to a home location where building priv has been lost (PVP).
                    if (auth == player.userID || (configData.Options.HonorRelationships && Utils.IsFriend(player.userID, auth)))
                    {
                        DoLog($"Player {player.userID} has privileges...");
                        return true;
                    }
                }
            }
            // No matching priv
            DoLog("NO BUILDING PRIV");
            return false;
        }
        DoLog("NO BUILDING AT ALL");
        return true;
    }

    // Check a location to verify that it is not obstructed by construction.
    public bool Obstructed(Vector3 location)
    {
        List<BaseEntity> ents = new List<BaseEntity>();
        Vis.Entities(location, 1, ents, blockLayer);
        foreach (BaseEntity ent in ents)
        {
            return true;
        }
        return false;
    }

    public bool InTunnel(BasePlayer player)
    {
        if (player.transform.position.y < -60f)
        {
            return true;
        }
        List<BaseEntity> ents = new List<BaseEntity>();
        Vis.Entities(player.transform.position, 50, ents);
        foreach (BaseEntity entity in ents)
        {
            if (entity.name.Contains("tunnel")) return true;
        }

        return false;
    }

    public bool AboveWater(BasePlayer player)
    {
        Vector3 pos = player.transform.position;
        DoLog($"Player position: {pos}.  Checking for water...");

        if ((TerrainMeta.HeightMap.GetHeight(pos) - TerrainMeta.WaterMap.GetHeight(pos)) >= 0)
        {
            DoLog("Player not above water.");
            return false;
        }
        else
        {
            DoLog("Player is above water!");
            return true;
        }
    }

    private string NearMonument(BasePlayer player)
    {
        Vector3 pos = player.transform.position;

        foreach (KeyValuePair<string, Vector3> entry in monPos)
        {
            string monname = entry.Key;
            Vector3 monvector = entry.Value;
            float realDistance = monSize[monname].z;
            monvector.y = pos.y;
            float dist = Vector3.Distance(pos, monvector);

            DoLog($"Checking {monname} dist: {dist}, realDistance: {realDistance}");
            if (dist < realDistance)
            {
                DoLog($"Player in range of {monname}");
                return monname;
            }
        }
        return null;
    }

    private string NearCave(BasePlayer player)
    {
        Vector3 pos = player.transform.position;

        foreach (KeyValuePair<string, Vector3> entry in cavePos)
        {
            string cavename = entry.Key;
            float realDistance = 0f;

            if (cavename.Contains("Small"))
            {
                realDistance = configData.Options.CaveDistanceSmall;
            }
            else if (cavename.Contains("Large"))
            {
                realDistance = configData.Options.CaveDistanceLarge;
            }
            else if (cavename.Contains("Medium"))
            {
                realDistance = configData.Options.CaveDistanceMedium;
            }

            Vector3 cavevector = entry.Value;
            cavevector.y = pos.y;
            string cpos = cavevector.ToString();
            float dist = Vector3.Distance(pos, cavevector);

            if (dist < realDistance)
            {
                DoLog($"NearCave: {cavename} nearby.");
                return cavename;
            }
            else
            {
                DoLog("NearCave: Not near this cave.");
            }
        }
        return null;
    }
    #endregion

    #region helpers
    public static string RemoveSpecialCharacters(string str)
    {
        return Regex.Replace(str, "[^a-zA-Z0-9_.]+", "", RegexOptions.Compiled);
    }

    private static BasePlayer FindPlayerByName(string name)
    {
        BasePlayer result = null;
        foreach (BasePlayer current in BasePlayer.activePlayerList)
        {
            if (current.displayName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || current.UserIDString.Contains(name, CompareOptions.OrdinalIgnoreCase)
                || current.displayName.Contains(name, CompareOptions.OrdinalIgnoreCase))
            {
                result = current;
            }
        }
        return result;
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

    private void DoLog(string message, int indent = 0)
    {
        if (configData.Options.debug)
        {
            if (configData.Options.logtofile)
            {
                LogToFile(logfilename, "".PadLeft(indent, ' ') + message, this);
            }
            else
            {
                Utils.DoLog(message);
            }
        }
    }

    public static Vector3 StringToVector3(string sVector)
    {
        // Remove the parentheses
        if (sVector.StartsWith("(") && sVector.EndsWith(")"))
        {
            sVector = sVector.Substring(1, sVector.Length - 2);
        }

        // split the items
        string[] sArray = sVector.Split(',');

        // return as a Vector3
        return new Vector3(
            float.Parse(sArray[0]),
            float.Parse(sArray[1]),
            float.Parse(sArray[2])
        );
    }

    public string PositionToGrid(Vector3 position)
    {
        // From GrTeleport for display only
        Vector2 r = new Vector2((World.Size / 2) + position.x, (World.Size / 2) + position.z);
        float x = Mathf.Floor(r.x / 146.3f) % 26;
        float z = Mathf.Floor(World.Size / 146.3f) - Mathf.Floor(r.y / 146.3f);

        return $"{(char)('A' + x)}{z - 1}";
    }

    //public void AutoSpawnTown()
    //{
    //    Utils.DoLog("Running AutoSpawnTown");
    //    if (CopyPaste != null && !string.IsNullOrEmpty(configData.Options.TownCopyPasteString))
    //    {
    //        Utils.DoLog($"Finding Town Position for {configData.Options.TownCopyPasteString}");
    //       //ServerMgr.Instance.StartCoroutine(FindTownPosition());
    //        FindTownPosition();
    //        if (townPositions.Count > 0)
    //        {
    //            int pos = UnityEngine.Random.Range(0, townPositions.Count);
    //            Utils.DoLog($"Trying to paste {configData.Options.TownCopyPasteString} as new town at {townPositions[pos].ToString()} with zoneid {configData.Options.TownZoneId}");
    //            //CopyPaste.Call("TryPaste", townPositions[pos], configData.Options.TownCopyPasteString, null, 0f, new string[] { "autoheight", "true", "stability", "true", "deployables", "true", "inventories", "true", "entityowner", "true" });
    //            CopyPaste.Call("TryPasteFromVector3", townPositions[pos], 0f, configData.Options.TownCopyPasteString, new string[] { "autoheight", "true", "stability", "true", "deployables", "true", "inventories", "true", "entityowner", "true" }, null);

    //            RunUpdateQuery($"INSERT OR REPLACE INTO rtp_server VALUES('town', '{townPositions[pos].ToString()}')");

    //            string[] args = { "name", "Town", "radius", "150" };
    //            ZoneManager?.Call("CreateOrUpdateZone", configData.Options.TownZoneId, args, townPositions[pos]);

    //            if (configData.Options.AddTownMapMarker)
    //            {
    //                foreach (MapMarkerGenericRadius mm in UnityEngine.Object.FindObjectsOfType<MapMarkerGenericRadius>().Where(x => x.name == "town").ToList())
    //                {
    //                    mm.Kill();
    //                }
    //                MapMarkerGenericRadius marker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", townPositions[pos]) as MapMarkerGenericRadius;
    //                if (marker != null)
    //                {
    //                    marker.alpha = 0.6f;
    //                    marker.color1 = Color.green;
    //                    marker.color2 = Color.white;
    //                    marker.name = "town";
    //                    marker.radius = 0.2f;
    //                    marker.Spawn();
    //                    marker.SendUpdate();
    //                }
    //            }

    //            //timer.Once(20f, () => LootProtect?.Call("ShareBuilding", townPositions[pos], configData.Options.TownCopyPasteOwnerID));
    //            //NextTick(() =>
    //            //{
    //            //    Utils.DoLog($"Trying to share items in town at {townPositions[pos].ToString()}");
    //            //    LootProtect?.Call("ShareBuilding", townPositions[pos], configData.Options.TownCopyPasteOwnerID);
    //            //});

    //            return;
    //        }
    //        Utils.DoLog("Unable to automatically set town :(");
    //    }
    //}

    //private IEnumerator FindTownPosition(int max = 10)
    private void FindTownPosition(int max = 500)
    {
        List<Vector3> locations = Pool.GetList<Vector3>();
        float mapSizeX = TerrainMeta.Size.x / 2;
        float mapSizeZ = TerrainMeta.Size.z / 2;
        Vector3 rand = Vector3.zero;
        for (int i = 0; i < max; i++)
        {
            rand.x = UnityEngine.Random.Range(-mapSizeX, mapSizeX);
            rand.z = UnityEngine.Random.Range(-mapSizeZ, mapSizeZ);
            rand.y = TerrainMeta.HeightMap.GetHeight(rand);
            if (ValidTownPosition(ref rand))
            {
                locations.Add(rand);
            }

            //if (i % 20 == 0)
            //{
            //    yield return CoroutineEx.waitForEndOfFrame;
            //}
        }

        Utils.DoLog($"Found {locations.Count} possible town positions");
        townPositions.AddRange(locations);
        Pool.FreeList(ref locations);
        //townPositionsC = null;
    }

    public bool ValidTownPosition(ref Vector3 pos)
    {
        //Utils.DoLog($"Checking position {pos.ToString()}, biome {GetBiome(pos).ToString()}, terrain {Enum.Parse(typeof(TerrainTopology), GetTopology(pos).ToString(), true)}");
        Utils.DoLog($"Checking position {pos}");

        float sl = GetSlope(pos);
        if (0 > sl || sl > 30)
        {
            Utils.DoLog($"Bad slope: {Math.Abs(sl)} degrees");
            return false;
        }
        if (GetWater(pos) > 1)
        {
            Utils.DoLog("In the water :(");
            return false;
        }

        TerrainSplat.Enum sp = GetSplat(pos);
        if (sp != TerrainSplat.Enum.Dirt
            && sp != TerrainSplat.Enum.Grass
            && sp != TerrainSplat.Enum.Gravel
            && sp != TerrainSplat.Enum.Sand
            && sp != TerrainSplat.Enum.Snow)
        {
            Utils.DoLog("Bad splat position :(");
            return false;
        }

        RaycastHit hitinfo;
        bool x = Physics.Raycast(pos, Vector3.down, out hitinfo, 5f, LayerMask.GetMask("Terrain"));
        if (!x)
        {
            Utils.DoLog("Not above terrain :(");
            return false;
        }
        else if (hitinfo.collider.name.IndexOf("road", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Utils.DoLog("Above road :(");
            return false;
        }
        else if (hitinfo.collider.name.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Utils.DoLog("Above water :(");
            return false;
        }

        foreach (KeyValuePair<string, Vector3> entry in monPos)
        {
            string monname = entry.Key;
            Vector3 monvector = entry.Value;
            const float realDistance = 300f;
            monvector.y = pos.y;
            float dist = Vector3.Distance(pos, monvector);

            DoLog($"Checking {monname} dist: {dist}, realDistance: {realDistance}");
            if (dist < realDistance)
            {
                Utils.DoLog($"Too close to {monname}");
                return false;
            }
        }

        bool enabled;
        Dictionary<TerrainBiome.Enum, bool> biomes = new Dictionary<TerrainBiome.Enum, bool>
            {
                { TerrainBiome.Enum.Arctic, false },
                { TerrainBiome.Enum.Arid, true },
                { TerrainBiome.Enum.Temperate, true },
                { TerrainBiome.Enum.Tundra, false}
            };

        TerrainBiome.Enum tp = GetBiome(pos);
        if (biomes.TryGetValue(tp, out enabled) && enabled)
        {
            Utils.DoLog("Good Biome!");
            return true;
        }
        else
        {
            Utils.DoLog("BAD Biome :(");
            return false;
        }
    }

    private static float GetSlope(Vector3 pos) => TerrainMeta.HeightMap.GetSlope(pos);
    private static float GetWater(Vector3 pos) => TerrainMeta.WaterMap.GetHeight(pos);
    private static TerrainBiome.Enum GetBiome(Vector3 pos) => (TerrainBiome.Enum)TerrainMeta.BiomeMap.GetBiomeMaxType(pos);
    private static TerrainSplat.Enum GetSplat(Vector3 pos) => (TerrainSplat.Enum)TerrainMeta.SplatMap.GetSplatMaxType(pos);
    private static TerrainTopology.Enum GetTopology(Vector3 pos) => (TerrainTopology.Enum)TerrainMeta.TopologyMap.GetTopology(pos);

    public void FindMonuments()
    {
        Vector3 extents = Vector3.zero;
        float realWidth = 0f;
        string name = null;
        bool ishapis = ConVar.Server.level.Contains("Hapis");

        foreach (MonumentInfo monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
        {
            if (monument.name.Contains("power_sub")) continue;
            realWidth = 0f;
            name = null;

            if (monument.name == "OilrigAI")
            {
                name = "Small Oilrig";
                realWidth = 100f;
            }
            else if (monument.name == "OilrigAI2")
            {
                name = "Large Oilrig";
                realWidth = 200f;
            }
            else
            {
                if (ishapis)
                {
                    foreach (Match e in Regex.Matches(monument.name, @"\w{4,}|\d{1,}"))
                    {
                        if (e.Value.Equals("MONUMENT")) continue;
                        if (e.Value.Contains("Label")) continue;
                        name += e.Value + " ";
                    }
                    name = name.Trim();
                }
                else
                {
                    name = Regex.Match(monument.name, @"\w{6}\/(.+\/)(.+)\.(.+)").Groups[2].Value.Replace("_", " ").Replace(" 1", "").Titleize();
                }
            }
            if (monPos.ContainsKey(name)) continue;
            if (cavePos.ContainsKey(name)) name += RandomString();

            extents = monument.Bounds.extents;
            if (realWidth > 0f)
            {
                extents.z = realWidth;
            }

            if (monument.name.Contains("cave"))
            {
                //DoLog("  Adding to cave list");
                cavePos.Add(name, monument.transform.position);
            }
            else if (monument.name.Contains("compound") && configData.Options.AutoGenOutpost)
            {
                DoLog("  Adding Outpost target");
                Vector3 mt = Vector3.zero;
                Vector3 bbq = Vector3.zero;
                List<BaseEntity> ents = new List<BaseEntity>();
                Vis.Entities(monument.transform.position, 50, ents);
                foreach (BaseEntity entity in ents)
                {
                    if (entity.PrefabName.Contains("marketterminal") && mt == Vector3.zero)
                    {
                        mt = entity.transform.position;
                    }
                    else if (entity.PrefabName.Contains("bbq"))
                    {
                        bbq = entity.transform.position;
                    }
                    if (mt != Vector3.zero && bbq != Vector3.zero)
                    {
                        Vector3 outpost = Vector3.Lerp(mt, bbq, 0.3f) + new Vector3(1f, 0.1f, 1f);
                        RunUpdateQuery($"INSERT OR REPLACE INTO rtp_server VALUES('outpost', '{outpost}')");
                    }
                    break;
                }
            }
            else if (monument.name.Contains("bandit") && configData.Options.AutoGenBandit)
            {
                DoLog("  Adding BanditTown target");
                List<BaseEntity> ents = new List<BaseEntity>();
                Vis.Entities(monument.transform.position, 50, ents);
                foreach (BaseEntity entity in ents)
                {
                    if (entity.PrefabName.Contains("workbench"))
                    {
                        Vector3 bandit = Vector3.Lerp(monument.transform.position, entity.transform.position, 0.45f) + new Vector3(0, 1.5f, 0);
                        RunUpdateQuery($"INSERT OR REPLACE INTO rtp_server VALUES('bandit', '{bandit}')");
                        break;
                    }
                }
            }
            else
            {
                if (extents.z < 1)
                {
                    extents.z = configData.Options.DefaultMonumentSize;
                }
                monPos.Add(name, monument.transform.position);
                monSize.Add(name, extents);
                //DoLog($"Adding Monument: {name}, pos: {monument.transform.position.ToString()}, size: {extents.ToString()}");
                if (name.Contains("Entrance Bunker") && configData.Options.AutoGenTunnels)
                {
                    Vector3 pos = monument.transform.position;
                    pos.y = TerrainMeta.HeightMap.GetHeight(monument.transform.position);
                    string tname = name;
                    tname = tname.Replace("Entrance Bunker ", "Tunnel ");
                    //DoLog($"Adding {tname}, pos: {pos.ToString()}");
                    RunUpdateQuery($"INSERT OR REPLACE INTO rtp_server VALUES('{tname}', '{pos}')");
                }
            }
        }

        monPos.OrderBy(x => x.Key);
        monSize.OrderBy(x => x.Key);
        cavePos.OrderBy(x => x.Key);
    }

    public string GetClosest(Vector3 startPosition)
    {
        Vector3 bestTarget = new Vector3();
        float closestDistanceSqr = Mathf.Infinity;

        foreach (Vector3 potentialTarget in monPos.Values)
        {
            Vector3 direction = potentialTarget - startPosition;

            float dSqrToTarget = direction.sqrMagnitude;

            if (dSqrToTarget < closestDistanceSqr)
            {
                closestDistanceSqr = dSqrToTarget;
                bestTarget = potentialTarget;
            }
        }

        return (from rv in monPos where rv.Value.Equals(bestTarget) select rv.Key).FirstOrDefault();
    }

    private bool RunUpdateQuery(string query)
    {
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand cmd = new SqliteCommand(query, c))
            {
                cmd.ExecuteNonQuery();
            }
        }
        return true;
    }

    private object RunSingleSelectQuery(string query)
    {
        List<string> output = new List<string>();
        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            using (SqliteCommand q = new SqliteCommand(query, c))
            using (SqliteDataReader rtbl = q.ExecuteReader())
            {
                while (rtbl.Read())
                {
                    string test = rtbl.GetValue(0).ToString();
                    if (test != "")
                    {
                        output.Add(test);
                    }
                }
            }
        }
        if (output.Count > 0) return output;
        return null;
    }

    private string RandomString()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        List<char> charList = chars.ToList();

        string random = "";

        for (int i = 0; i <= UnityEngine.Random.Range(5, 10); i++)
        {
            random += charList[UnityEngine.Random.Range(0, charList.Count - 1)];
        }

        return random;
    }

    public void AddTimer(BasePlayer player, Vector3 targetLoc, string type, string typeName)
    {
        DoLog($"Creating a {type} Countdown TPTimer object for {player.UserIDString}.");
        TeleportTimers.Add(
            player.userID,
            new TPTimer()
            {
                type = type,
                source = player,
                targetName = Lang(typeName),
                targetLocation = targetLoc
            }
        );
    }

    public void HandleTimer(ulong userid, string type, bool start = false)
    {
        if (TeleportTimers.ContainsKey(userid))
        {
            if (start)
            {
                float countdown = configData.Types[type].CountDown;
                string isvip = "";
                if (configData.Types[type].VIPSettings != null)
                {
                    foreach (KeyValuePair<string, VIPSetting> vip in configData.Types[type].VIPSettings)
                    {
                        if (Permissions.UserHasPermission(vip.Key, userid.ToString()) && vip.Value.VIPCountDown < countdown)
                        {
                            isvip = $" (from {vip.Key} permission)";
                            countdown = vip.Value.VIPCoolDown + vip.Value.VIPCountDown;
                        }
                    }
                }

                DoLog($"Creating a {type} countdown timer for {userid}.  Timer will be set to {countdown} seconds{isvip}.");
                TeleportTimers[userid].start = Time.realtimeSinceStartup;
                TeleportTimers[userid].cooldown = countdown;
                TeleportTimers[userid].timer = timer.Once(TeleportTimers[userid].cooldown, () => Teleport(TeleportTimers[userid].source, TeleportTimers[userid].targetLocation, type));
            }
            else
            {
                RunUpdateQuery($"UPDATE rtp_player SET lastused='{Time.realtimeSinceStartup}' WHERE userid='{userid}' AND name='{TeleportTimers[userid].targetName}'");
                if (TeleportTimers.ContainsKey(userid))
                {
                    TeleportTimers[userid].timer.Destroy();
                    TeleportTimers.Remove(userid);
                }
                if (TPRTimers.ContainsKey(userid))
                {
                    TPRTimers[userid].timer.Destroy();
                    TPRTimers.Remove(userid);
                }
            }
        }
        else if (TPRTimers.ContainsKey(userid))
        {
            if (start)
            {
                TPRTimers[userid].timer = timer.Once(TPRTimers[userid].countdown, () => TPRNotification(true));
            }
            else
            {
                TPRTimers[userid].timer.Destroy();
                TPRTimers.Remove(userid);
            }
        }
    }

    public void AddCooldown(BasePlayer player, Vector3 targetLoc, string type, string typeName)
    {
        DoLog($"Creating a {type} Cooldown TPTimer object for {player.UserIDString}.");
        CooldownTimers[type].Add(
            player.userID,
            new TPTimer()
            {
                type = type,
                source = player,
                targetName = Lang(typeName),
                targetLocation = targetLoc
            }
        );
    }

    public bool CheckCooldown(ulong userid, string type, out float cooldown, out double bypass)
    {
        cooldown = 0;
        bypass = 0;
        if (CooldownTimers[type].ContainsKey(userid))
        {
            cooldown = (float)Math.Floor((CooldownTimers[type][userid].start + CooldownTimers[type][userid].cooldown) - Time.realtimeSinceStartup);
            DoLog($"Found a {type} cooldown timer for {userid} with {cooldown} second(s) remaining");
            DoLog($"Player has made {CooldownTimers[type][userid].counter} previous requests.");

            if (CooldownTimers[type][userid].counter > 0)
            {
                // This is a secondary request, so we will deduct the bypass amount from their account and destroy the countdown timer.
                if (configData.Types[type].AllowBypass && configData.Types[type].BypassAmount > 0 && (configData.Options.useEconomics || configData.Options.useServerRewards) && HandleMoney(userid, configData.Types[type].BypassAmount, true))
                {
                    bypass = configData.Types[type].BypassAmount;
                    CooldownTimers[type][userid].timer.Destroy();
                    CooldownTimers[type].Remove(userid);
                    return true;
                }
            }
            else if (configData.Types[type].AllowBypass && configData.Types[type].BypassAmount > 0 && (configData.Options.useEconomics || configData.Options.useServerRewards) && HandleMoney(userid, configData.Types[type].BypassAmount, false))
            {
                // If a check of their accounts shows money present, tick the counter so that if they repeat the command they will pay and be teleported.
                CooldownTimers[type][userid].counter++;
                return true;
            }
        }
        return false;
    }

    public void HandleCooldown(ulong userid, string type, bool start = false, bool canbypass = false, double bypassamount = 0, bool dobypass = false, bool kill = false)
    {
        if (start)
        {
            float cooldown = configData.Types[type].CoolDown + configData.Types[type].CountDown;
            string isvip = "";
            if (configData.Types[type].VIPSettings != null)
            {
                foreach (KeyValuePair<string, VIPSetting> vip in configData.Types[type].VIPSettings)
                {
                    if (Permissions.UserHasPermission(vip.Key, userid.ToString()) && (vip.Value.VIPCoolDown + vip.Value.VIPCountDown) < cooldown)
                    {
                        isvip = $" (from {vip.Key} permission)";
                        cooldown = vip.Value.VIPCoolDown + vip.Value.VIPCountDown;
                    }
                }
            }

            DoLog($"Creating a {type} cooldown timer for {userid}.  Timer will be set to {cooldown} seconds{isvip} including countdown.");
            CooldownTimers[type][userid].start = Time.realtimeSinceStartup;
            CooldownTimers[type][userid].cooldown = cooldown;
            CooldownTimers[type][userid].timer = timer.Once(cooldown, () => HandleCooldown(userid, type, false, canbypass, bypassamount, dobypass, true));
        }
        else if (kill)
        {
            DoLog($"Destroying {type} cooldown timer for {userid}");
            CooldownTimers[type][userid].timer.Destroy();
            CooldownTimers[type].Remove(userid);
        }
    }

    // Check limit for any userid and type based on current activity
    public bool AtLimit(ulong userid, string type, out float limit)
    {
        float current = 0;
        if (!DailyUsage[type].TryGetValue(userid, out current))
        {
            DailyUsage[type].Add(userid, 0);
        }

        limit = GetDailyLimit(userid, type);
        return current >= limit && limit != 0;
    }

    private float GetDailyLimit(ulong userid, string type)
    {
        float limit = configData.Types[type].DailyLimit;
        // Check for player VIP permissions
        if (configData.Types[type].VIPSettings != null)
        {
            foreach (KeyValuePair<string, VIPSetting> perm in configData.Types[type].VIPSettings)
            {
                if (Permissions.UserHasPermission(perm.Key, userid.ToString()))
                {
                    float newlimit = perm.Value.VIPDailyLimit;
                    if (newlimit == 0)
                    {
                        limit = 0;
                    }
                    else if (newlimit > limit)
                    {
                        limit = newlimit;
                    }
                }
            }
        }

        return limit;
    }

    private bool HandleMoney(ulong userID, double bypass, bool withdraw = false, bool deposit = false)
    {
        double balance;
        bool hasmoney = false;

        string userid = userID.ToString();
        // Check Economics first.  If not in use or balance low, check ServerRewards below
        if (configData.Options.useEconomics && HEconomics != null)
        {
            balance = (double)BroadcastReturn("Balance", userid);
            if (balance >= bypass)
            {
                hasmoney = true;
                if (withdraw)
                {
                    return (bool)BroadcastReturn("Withdraw", userid, bypass);
                }
                else if (deposit)
                {
                    return (bool)BroadcastReturn("Deposit", userid, bypass);
                }
            }
        }

        // Just checking balance without withdrawal or deposit - did we find anything?
        return hasmoney;
    }

    // For TPB
    public void SaveLocation(BasePlayer player)
    {
        SavedPoints[player.userID] = player.transform.position;
    }

    private void MidnightDetect(bool startup = false)
    {
        DateTime dt = TOD_Sky.Instance.Cycle.DateTime;
        if (startup)
        {
            dateInt = Convert.ToInt32(dt.Hour.ToString().PadLeft(2, '0') + dt.Minute.ToString().PadLeft(2, '0') + dt.Second.ToString().PadLeft(2, '0'));
            DoLog($"Startup: Set start time to {dateInt.ToString().PadLeft(6, '0')} for daily limits");
            timer.Once(60f, () => MidnightDetect());
            return;
        }

        // Has game midnight passed since the last run?
        int now = Convert.ToInt32(dt.Hour.ToString().PadLeft(2, '0') + dt.Minute.ToString().PadLeft(2, '0') + dt.Second.ToString().PadLeft(2, '0'));
        if (now > dateInt)
        {
            //DoLog($"MidnightDetect: Still same day.  NOW {now.ToString().PadLeft(6, '0')} > Startup {dateInt.ToString().PadLeft(6, '0')}.");
            timer.Once(60f, () => MidnightDetect());
            return;
        }
        DoLog($"MidnightDetect: Day changed!  NOW {now.ToString().PadLeft(6, '0')} < Startup {dateInt.ToString().PadLeft(6, '0')}.  Clearing the daily limits.");
        dateInt = now;
        DailyUsage = new Dictionary<string, Dictionary<ulong, float>>
            {
                { "Home", new Dictionary<ulong, float>() },
                { "Town", new Dictionary<ulong, float>() },
                { "TPA", new Dictionary<ulong, float>() },
                { "TPB", new Dictionary<ulong, float>() },
                { "TPR", new Dictionary<ulong, float>() },
                { "TP", new Dictionary<ulong, float>() },
                { "Bandit", new Dictionary<ulong, float>() },
                { "Outpost", new Dictionary<ulong, float>() },
                { "Tunnel", new Dictionary<ulong, float>() }
            };
        timer.Once(60f, () => MidnightDetect());
    }

    //        public void TeleportToPlayer(BasePlayer player, BasePlayer target) => Teleport(player, target.transform.position);
    //        public void TeleportToPosition(BasePlayer player, float x, float y, float z) => Teleport(player, new Vector3(x, y, z));

    public void Teleport(BasePlayer player, Vector3 position, string type = "")
    {
        SaveLocation(player);
        HandleTimer(player.userID, type);
        HandleCooldown(player.userID, type);
        DailyUsage[type][player.userID]++;

        if (player.net?.connection != null) player.SetPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot, true);

        player.SetParent(null, true, true);
        player.EnsureDismounted();
        player.Teleport(position);
        player.UpdateNetworkGroup();
        player.StartSleeping();
        player.SendNetworkUpdateImmediate(false);

        if (player.net?.connection != null) player.ClientRPCPlayer(null, player, "StartLoading");
    }

    private void StartSleeping(BasePlayer player)
    {
        if (player.IsSleeping()) return;
        player.SetPlayerFlag(BasePlayer.PlayerFlags.Sleeping, true);
        if (!BasePlayer.sleepingPlayerList.Contains(player)) BasePlayer.sleepingPlayerList.Add(player);
        player.CancelInvoke("InventoryUpdate");
        //player.inventory.crafting.CancelAll(true);
        //player.UpdatePlayerCollider(true, false);
    }
    #endregion

    #region config
    private void LoadConfigVariables()
    {
        configData = config.ReadObject<ConfigData>();

        if (configData.Version < new VersionNumber(1, 1, 18))
        {
            using (SqliteConnection c = new SqliteConnection(connStr))
            {
                c.Open();
                using (SqliteCommand ct = new SqliteCommand("CREATE TABLE new_player (userid VARCHAR(255), name VARCHAR(255) NOT NULL, location VARCHAR(255), lastused VARCHAR(255), total INTEGER(32))", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("INSERT INTO new_player SELECT * FROM rtp_player", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("DROP TABLE IF EXISTS rtp_player", c))
                {
                    ct.ExecuteNonQuery();
                }
                using (SqliteCommand ct = new SqliteCommand("ALTER TABLE new_player RENAME TO rtp_player", c))
                {
                    ct.ExecuteNonQuery();
                }
            }
        }

        if (configData.Version < new VersionNumber(1, 2, 8))
        {
            configData.Types["Home"].HomesLimit = 0;
        }

        configData.Version = Version;
        SaveConfig(configData);
    }

    public void LoadDefaultConfig()
    {
        Utils.DoLog("Creating new config file.");
        ConfigData config = new ConfigData
        {
            Options = new Options()
            {
                SetCommand = "set",
                ListCommand = "list",
                RemoveCommand = "remove",
                HomeMinimumDistance = 10f,
                DefaultMonumentSize = 120f,
                CaveDistanceSmall = 40f,
                CaveDistanceMedium = 60f,
                CaveDistanceLarge = 100f,
                AutoGenBandit = true,
                AutoGenOutpost = true,
                AutoGenTunnels = false,
                MinimumTemp = 0f,
                MaximumTemp = 40f,
                TownZoneEnterMessage = "Welcome to Town!",
                TownZoneLeaveMessage = "Thanks for stopping by!",
                TownZoneFlags = new List<string>()
                    {
                        "nodecay",
                        "nohelitargeting"
                    }
            },
            Version = Version
        };

        config.Types["Home"] = new CmdOptions()
        {
            CountDown = 5f,
            CoolDown = 120f,
            DailyLimit = 30f,
            HomesLimit = 10f,
            BypassAmount = 0f,
        };
        config.Types["Town"] = new CmdOptions()
        {
            CountDown = 5f,
            CoolDown = 120f,
            DailyLimit = 30f,
            BypassAmount = 0f
        };
        config.Types["Bandit"] = new CmdOptions()
        {
            CountDown = 5f,
            CoolDown = 120f,
            DailyLimit = 30f,
            BlockOnHostile = true,
            BypassAmount = 0f
        };
        config.Types["Outpost"] = new CmdOptions()
        {
            CountDown = 5f,
            CoolDown = 120f,
            DailyLimit = 30f,
            BlockOnHostile = true,
            BypassAmount = 0f
        };
        config.Types["TPB"] = new CmdOptions()
        {
            CountDown = 5f,
            CoolDown = 120f,
            DailyLimit = 30f,
            BypassAmount = 0f
        };
        config.Types["TPC"] = new CmdOptions()
        {
            CountDown = 5f,
            CoolDown = 120f,
            DailyLimit = 30f,
            BypassAmount = 0f
        };
        config.Types["TPR"] = new CmdOptions()
        {
            CountDown = 5f,
            CoolDown = 120f,
            DailyLimit = 30f,
            BypassAmount = 0f
        };
        config.Types["TP"] = new CmdOptions()
        {
            CountDown = 2f,
            CoolDown = 10f,
            DailyLimit = 30f,
            BypassAmount = 0f
        };

        SaveConfig(config);
    }

    private void SaveConfig(ConfigData configuration)
    {
        config.WriteObject(configuration, true);
    }

    private class ConfigData
    {
        public Options Options;
        public Dictionary<string, CmdOptions> Types = new Dictionary<string, CmdOptions>();
        public VersionNumber Version;

        public ConfigData()
        {
            Types.Add("Home", new CmdOptions());
            Types.Add("Town", new CmdOptions());
            Types.Add("Bandit", new CmdOptions());
            Types.Add("Outpost", new CmdOptions());
            Types.Add("Tunnel", new CmdOptions());
            Types.Add("TPB", new CmdOptions());
            Types.Add("TPC", new CmdOptions());
            Types.Add("TPR", new CmdOptions());
            Types.Add("TP", new CmdOptions());
        }
    }

    public class Options
    {
        public bool debug;
        public bool logtofile;
        public bool useClans;
        public bool useFriends;
        public bool useTeams;
        public bool useEconomics;
        public bool useServerRewards;
        public bool useNoEscape;
        public bool useVanish;
        public bool HomeRequireFoundation;
        public bool StrictFoundationCheck;
        public bool HomeRemoveInvalid;
        public bool HonorBuildingPrivilege;
        public bool HonorRelationships;
        public bool WipeOnNewSave;
        public bool AutoGenBandit;
        public bool AutoGenOutpost;
        public bool AutoGenTunnels;
        public float HomeMinimumDistance;
        public float DefaultMonumentSize;
        public float CaveDistanceSmall;
        public float CaveDistanceMedium;
        public float CaveDistanceLarge;
        public float MinimumTemp;
        public float MaximumTemp;
        public string SetCommand;
        public string ListCommand;
        public string RemoveCommand;
        public bool AddTownMapMarker;
        public string TownZoneId;
        public string TownZoneEnterMessage;
        public string TownZoneLeaveMessage;
        public List<string> TownZoneFlags;
        //public string TownCopyPasteString;
        //public ulong TownCopyPasteOwnerID;
    }

    private class CmdOptions : VIPOptions
    {
        public bool BlockOnHurt;
        public bool BlockOnCold;
        public bool BlockOnHot;
        public bool BlockOnCave;
        public bool BlockOnRig;
        public bool BlockOnMonuments;
        public bool BlockOnHostile;
        public bool BlockOnSafe;
        public bool BlockOnBalloon;
        public bool BlockOnCargo;
        public bool BlockOnExcavator;
        public bool BlockOnLift;
        public bool BlockOnMounted;
        public bool BlockOnSwimming;
        public bool BlockOnWater;
        public bool BlockForNoEscape;
        public bool BlockIfInvisible;
        public bool BlockInTunnel = true;
        public bool AutoAccept;
        public float DailyLimit;
        public float HomesLimit;
        public float CountDown;
        public float CoolDown;
        public bool AllowBypass;
        public double BypassAmount;
    }

    private class VIPOptions
    {
        public Dictionary<string, VIPSetting> VIPSettings { get; set; }
    }

    public class VIPSetting
    {
        public float VIPDailyLimit;
        public float VIPHomesLimit;
        public float VIPCountDown;
        public float VIPCoolDown;
        public bool VIPAllowBypass;
        public double VIPBypassAmount;
    }
    #endregion

    #region UI
    private void HomeGUI(BasePlayer player, string orderby = "alpha")
    {
        if (player == null) return;
        CuiHelper.DestroyUi(player, HGUI);

        CuiElementContainer container = UI.Container(HGUI, UI.Color("222222", 0.9f), "0.2 0.2", "0.8 0.8", true, "Overlay");

        string append = null;
        string label = null;
        switch (orderby)
        {
            case "last":
                UI.Button(ref container, HGUI, UI.Color("#4055d8", 1f), Lang("alpha"), 12, "0.82 0.93", "0.91 0.99", "homeg alpha");
                append = " ORDER BY name";
                label = Lang("homesavail") + " " + Lang("sortedby", null, Lang("lastuse"));

                break;
            case "alpha":
            default:
                UI.Button(ref container, HGUI, UI.Color("#4055d8", 1f), Lang("last"), 12, "0.82 0.93", "0.91 0.99", "homeg last");
                append = " ORDER BY lastused";
                label = Lang("homesavail") + " " + Lang("sortedby", null, Lang("name"));
                break;
        }

        UI.Label(ref container, HGUI, UI.Color("#ffffff", 1f), label, 14, "0.1 0.93", "0.8 0.99");
        UI.Button(ref container, HGUI, UI.Color("#d85540", 1f), Lang("close"), 12, "0.92 0.93", "0.99 0.99", "homeg closeit");

        int row = 0;
        int col = 0;
        float[] posb = new float[4];

        using (SqliteConnection c = new SqliteConnection(connStr))
        {
            c.Open();
            string qh = $"SELECT name, location, lastused FROM rtp_player WHERE userid={player.userID}{append}";
            using (SqliteCommand q = new SqliteCommand(qh, c))
            using (SqliteDataReader home = q.ExecuteReader())
            {
                while (home.Read())
                {
                    if (row > 10)
                    {
                        row = 0;
                        col++;
                    }

                    string hname = home.GetValue(0).ToString();
                    Vector3 position = StringToVector3(home.GetValue(1).ToString());
                    string pos = PositionToGrid(position);

                    posb = GetButtonPositionZ(row, col);
                    UI.Button(ref container, HGUI, UI.Color("#d85540", 1f), $"{hname} ({pos})", 10, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"home {hname}");//, UI.Color("#ffffff", 1));
                    row++;
                }
            }
        }

        CuiHelper.AddUi(player, container);
    }

    private int RowNumber(int max, int count) => Mathf.FloorToInt(count / max);

    private float[] GetButtonPosition(int rowNumber, int columnNumber)
    {
        float offsetX = 0.05f + (0.096f * columnNumber);
        float offsetY = (0.80f - (rowNumber * 0.064f));

        return new float[] { offsetX, offsetY, offsetX + 0.196f, offsetY + 0.03f };
    }
    //private float[] GetButtonPositionP(int rowNumber, int columnNumber)
    //{
    //    float offsetX = 0.05f + (0.186f * columnNumber);
    //    float offsetY = (0.85f - (rowNumber * 0.074f));

    //    return new float[] { offsetX, offsetY, offsetX + 0.256f, offsetY + 0.03f };
    //}
    private float[] GetButtonPositionP(int rowNumber, int columnNumber, float colspan = 1f)
    {
        float offsetX = 0.05f + (0.126f * columnNumber);
        float offsetY = (0.87f - (rowNumber * 0.064f));

        return new float[] { offsetX, offsetY, offsetX + (0.226f * colspan), offsetY + 0.03f };
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

    #region IMPORT
    /// <summary>
    ///  Classes for import of data from N/RTeleportation
    /// </summary>
    private class OtherConfigData
    {
        public SettingsData Settings { get; set; }
        public GameVersionData GameVersion { get; set; }
        public AdminSettingsData Admin { get; set; }
        public HomesSettingsData Home { get; set; }
        public TPRData TPR { get; set; }
        public TownData Town { get; set; }
        public TownData Outpost { get; set; }
        public TownData Bandit { get; set; }
        public VersionNumber Version { get; set; }
    }

    private class SettingsData
    {
        public string ChatName { get; set; }
        public bool HomesEnabled { get; set; }
        public bool TPREnabled { get; set; }
        public bool TownEnabled { get; set; }
        public bool OutpostEnabled { get; set; }
        public bool BanditEnabled { get; set; }
        public bool InterruptTPOnHurt { get; set; }
        public bool InterruptTPOnCold { get; set; }
        public bool InterruptTPOnHot { get; set; }
        public bool InterruptTPOnHostile { get; set; }
        public bool InterruptTPOnSafe { get; set; }
        public bool InterruptTPOnBalloon { get; set; }
        public bool InterruptTPOnCargo { get; set; }
        public bool InterruptTPOnRig { get; set; }
        public bool InterruptTPOnExcavator { get; set; }
        public bool InterruptTPOnLift { get; set; }
        public bool InterruptTPOnMonument { get; set; }
        public bool InterruptTPOnMounted { get; set; }
        public bool InterruptTPOnSwimming { get; set; }
        public bool InterruptAboveWater { get; set; }
        public bool StrictFoundationCheck { get; set; }
        public float CaveDistanceSmall { get; set; }
        public float CaveDistanceMedium { get; set; }
        public float CaveDistanceLarge { get; set; }
        public float DefaultMonumentSize { get; set; }
        public float MinimumTemp { get; set; }
        public float MaximumTemp { get; set; }
        public Dictionary<string, string> BlockedItems { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string BypassCMD { get; set; }
        public bool UseEconomics { get; set; }
        public bool UseServerRewards { get; set; }
        public bool WipeOnUpgradeOrChange { get; set; }
        public bool AutoGenOutpost { get; set; }
        public bool AutoGenBandit { get; set; }
    }

    private class GameVersionData
    {
        public int Network { get; set; }
        public int Save { get; set; }
        public string Level { get; set; }
        public string LevelURL { get; set; }
        public int WorldSize { get; set; }
        public int Seed { get; set; }
    }

    private class AdminSettingsData
    {
        public bool AnnounceTeleportToTarget { get; set; }
        public bool UseableByAdmins { get; set; }
        public bool UseableByModerators { get; set; }
        public int LocationRadius { get; set; }
        public int TeleportNearDefaultDistance { get; set; }
    }

    private class HomesSettingsData
    {
        public int HomesLimit { get; set; }
        public Dictionary<string, int> VIPHomesLimits { get; set; }
        public int Cooldown { get; set; }
        public int Countdown { get; set; }
        public int DailyLimit { get; set; }
        public Dictionary<string, int> VIPDailyUsage { get; set; }
        public Dictionary<string, int> VIPCooldowns { get; set; }
        public Dictionary<string, int> VIPCountdowns { get; set; }
        public int LocationRadius { get; set; }
        public bool ForceOnTopOfFoundation { get; set; }
        public bool CheckFoundationForOwner { get; set; }
        public bool UseFriends { get; set; }
        public bool UseClans { get; set; }
        public bool UseTeams { get; set; }
        public bool UsableOutOfBuildingBlocked { get; set; }
        public bool UsableIntoBuildingBlocked { get; set; }
        public bool CupOwnerAllowOnBuildingBlocked { get; set; }
        public bool AllowIceberg { get; set; }
        public bool AllowCave { get; set; }
        public bool AllowCraft { get; set; }
        public bool AllowAboveFoundation { get; set; }
        public bool CheckValidOnList { get; set; }
        public int Pay { get; set; }
        public int Bypass { get; set; }
    }

    private class TPRData
    {
        public int Cooldown { get; set; }
        public int Countdown { get; set; }
        public int DailyLimit { get; set; }
        public Dictionary<string, int> VIPDailyUsage { get; set; }
        public Dictionary<string, int> VIPCooldowns { get; set; }
        public Dictionary<string, int> VIPCountdowns { get; set; }
        public int RequestDuration { get; set; }
        public bool OffsetTPRTarget { get; set; }
        public bool AutoAcceptTPR { get; set; }
        public bool BlockTPAOnCeiling { get; set; }
        public bool UsableOutOfBuildingBlocked { get; set; }
        public bool UsableIntoBuildingBlocked { get; set; }
        public bool CupOwnerAllowOnBuildingBlocked { get; set; }
        public bool AllowCraft { get; set; }
        public int Pay { get; set; }
        public int Bypass { get; set; }
    }

    private class TownData
    {
        public int Cooldown { get; set; }
        public int Countdown { get; set; }
        public int DailyLimit { get; set; }
        public Dictionary<string, int> VIPDailyUsage { get; set; }
        public Dictionary<string, int> VIPCooldowns { get; set; }
        public Dictionary<string, int> VIPCountdowns { get; set; }
        public string Location { get; set; }
        public bool UsableOutOfBuildingBlocked { get; set; }
        public bool AllowCraft { get; set; }
        public int Pay { get; set; }
        public int Bypass { get; set; }
    }

    private class HomeData
    {
        [JsonProperty("l")]
        public Dictionary<string, Vector3> Locations { get; set; } = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("t")]
        public TeleportData Teleports { get; set; } = new TeleportData();
    }

    private class TeleportData
    {
        [JsonProperty("a")]
        public int Amount { get; set; }

        [JsonProperty("d")]
        public string Date { get; set; }

        [JsonProperty("t")]
        public int Timestamp { get; set; }
    }

    private class UnityVector3Converter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            Vector3 vector = (Vector3)value;
            writer.WriteValue($"{vector.x} {vector.y} {vector.z}");
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.String)
            {
                string[] values = reader.Value.ToString().Trim().Split(' ');
                return new Vector3(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]));
            }
            JObject o = JObject.Load(reader);
            return new Vector3(Convert.ToSingle(o["x"]), Convert.ToSingle(o["y"]), Convert.ToSingle(o["z"]));
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(Vector3);
        }
    }

    private class CustomComparerDictionaryCreationConverter<T> : CustomCreationConverter<IDictionary>
    {
        private readonly IEqualityComparer<T> comparer;

        public CustomComparerDictionaryCreationConverter(IEqualityComparer<T> comparer)
        {
            if (comparer == null)
                throw new ArgumentNullException(nameof(comparer));
            this.comparer = comparer;
        }

        public override bool CanConvert(Type objectType)
        {
            return HasCompatibleInterface(objectType) && HasCompatibleConstructor(objectType);
        }

        private static bool HasCompatibleInterface(Type objectType)
        {
            return objectType.GetInterfaces().Any(i => HasGenericTypeDefinition(i, typeof(IDictionary<,>)) && typeof(T).IsAssignableFrom(i.GetGenericArguments().First()));
        }

        private static bool HasGenericTypeDefinition(Type objectType, Type typeDefinition)
        {
            return objectType.IsGenericType && objectType.GetGenericTypeDefinition() == typeDefinition;
        }

        private static bool HasCompatibleConstructor(Type objectType)
        {
            return objectType.GetConstructor(new[] { typeof(IEqualityComparer<T>) }) != null;
        }

        public override IDictionary Create(Type objectType)
        {
            return Activator.CreateInstance(objectType, comparer) as IDictionary;
        }
    }
    #endregion IMPORT

    #region defaults
    private void CreateOrClearTables(bool drop = true)
    {
        if (drop)
        {
            SqliteCommand cd = new SqliteCommand("DROP TABLE IF EXISTS rtp_server", sqlConnection);
            cd.ExecuteNonQuery();
            cd = new SqliteCommand("CREATE TABLE rtp_server (name VARCHAR(255) NOT NULL UNIQUE, location VARCHAR(255))", sqlConnection);
            cd.ExecuteNonQuery();

            cd = new SqliteCommand("DROP TABLE IF EXISTS rtp_player", sqlConnection);
            cd.ExecuteNonQuery();
            cd = new SqliteCommand("CREATE TABLE rtp_player (userid VARCHAR(255), name VARCHAR(255) NOT NULL, location VARCHAR(255), lastused VARCHAR(255), total INTEGER(32))", sqlConnection);
            cd.ExecuteNonQuery();
        }
        else
        {
            SqliteCommand cd = new SqliteCommand("DELETE FROM rtp_server", sqlConnection);
            cd.ExecuteNonQuery();
            cd = new SqliteCommand("DELETE FROM rtp_player", sqlConnection);
            cd.ExecuteNonQuery();
        }
    }
    #endregion
}
