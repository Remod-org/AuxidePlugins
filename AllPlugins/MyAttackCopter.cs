#region License (GPL v2)
/*
    DESCRIPTION
    Copyright (c) 2023 RFC1920 <desolationoutpostpve@gmail.com>

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License v2.0

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/
#endregion License Information (GPL v2)
#region License Notice
/*
 * Hovering class modified from code at https://umod.org/plugins/helicopter-hover,
   originally licensed under the following license:

   MIT License
    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
 */
#endregion
using Auxide;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

[Info("My Attack Copter", "RFC1920", "0.1.1")]
[Description("Spawn an Attack Helicopter")]
public class MyAttackCopter : RustScript
{
    //[PluginReference]
    //private readonly Plugin NoEscape, Friends, Clans;
    public static MyAttackCopter Instance;

    private const string prefab = "assets/content/vehicles/attackcopter/attackcopter.entity.prefab";
    private ConfigData configData;

    private const string AttackcopterSpawn = "myattackcopter.spawn";
    private const string AttackcopterFetch = "myattackcopter.fetch";
    private const string AttackcopterWhere = "myattackcopter.where";
    private const string AttackcopterAdmin = "myattackcopter.admin";
    private const string AttackcopterCooldown = "myattackcopter.cooldown";
    private const string AttackcopterUnlimited = "myattackcopter.unlimited";
    //private const string AttackcopterCanHover = "myattackcopter.canhover";

    private static LayerMask layerMask = LayerMask.GetMask("Terrain", "World", "Construction");

    private Dictionary<ulong, ulong> currentMounts = new Dictionary<ulong, ulong>();
    //private Dictionary<int, Hovering> hovers = new Dictionary<int, Hovering>();
    //private Dictionary<ulong, DateTime> hoverDelayTimers = new Dictionary<ulong, DateTime>();
    private static readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0);

    private class StoredData
    {
        public Dictionary<ulong, NetworkableId> playerattackID = new Dictionary<ulong, NetworkableId>();
        public Dictionary<ulong, double> playercounter = new Dictionary<ulong, double>();
    }
    private StoredData storedData;

    private bool HasPermission(ConsoleSystem.Arg arg, string permname)
    {
        return !(arg.Connection.player is BasePlayer) || Permissions.UserHasPermission(permname, (arg.Connection.player as BasePlayer)?.UserIDString);
    }

    #region loadunload
    private void OnServerInitialized()
    {
        LoadConfigVariables();

        if (configData?.VIPSettings?.Count > 0)
        {
            foreach (string vipperm in configData.VIPSettings.Keys)
            {
                string perm = vipperm.StartsWith($"{Name.ToLower()}.") ? vipperm : $"{Name.ToLower()}.{vipperm}";
                DoLog($"Registering vip perm {perm}");
                Permissions.RegisterPermission(Name, perm);
            }
        }

        if (((configData.Global.cooldownmin * 60) <= 120) && configData.Global.useCooldown)
        {
            DoLog("Please set a longer cooldown time. Attackmum is 2 min.");
            configData.Global.cooldownmin = 2;
            SaveConfig(configData);
        }

        LoadData();
        foreach (KeyValuePair<ulong, NetworkableId> playerAttack in storedData.playerattackID)
        {
            AttackHelicopter attackCopter = BaseNetworkable.serverEntities.Find(playerAttack.Value) as AttackHelicopter;
            if (attackCopter == null) continue;
            BasePlayer pl = FindPlayerById(playerAttack.Key);
            if (pl == null) continue;

            VIPSettings vipsettings;
            GetVIPSettings(pl, out vipsettings);
            bool vip = vipsettings != null;

            //if (Permissions.UserHasPermission(AttackcopterCanHover, playerAttack.Key.ToString()))
            //{
            //    hovers.Add(attackCopter.GetInstanceID(), attackCopter.gameObject.AddComponent<Hovering>());
            //}
            DoLog("Setting up fuel");
            FieldInfo fuelPerSec = typeof(Minicopter).GetField("fuelPerSec", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            IFuelSystem fs = attackCopter?.GetComponent<VehicleEngineController<PlayerHelicopter>>()?.FuelSystem;
            ItemContainer fsinv = fs?.GetInventory();
            if (Permissions.UserHasPermission(AttackcopterUnlimited, playerAttack.Key.ToString()) || (vip && vipsettings.unlimited))
            {
                fuelPerSec?.SetValue(fs, 0);
                if (fs?.IsValidEntityReference() == true)
                {
                    if (fsinv?.IsEmpty() != true)
                    {
                        DoLog($"Setting fuel for MiniCopter {playerAttack.Value} owned by {playerAttack.Key}.");
                        ItemManager.CreateByItemID(-946369541, 1)?.MoveToContainer(fsinv);
                        fsinv.MarkDirty();
                    }

                    // Default for unlimited fuel
                    fsinv?.SetFlag(ItemContainer.Flag.NoItemInput, true);
                    if (configData.Global.allowFuelIfUnlimited || (vip && vipsettings.canloot))
                    {
                        fsinv?.SetFlag(ItemContainer.Flag.NoItemInput, false);
                    }
                }
                continue;
            }
            else
            {
                DoLog("Owner does not have unlimited Permission");
                // Done here in case player's unlimited permission was revoked.
                fsinv?.SetFlag(ItemContainer.Flag.NoItemInput, false);
            }
            DoLog("Setting fuel utilization");
            fuelPerSec.SetValue(fs, vip ? vipsettings.stdFuelConsumption : configData.Global.stdFuelConsumption);
        }
    }

    private void OnNewSave()
    {
        storedData = new StoredData();
        SaveData();
    }

    private void Init()
    {
        Instance = this;

        //AddCovalenceCommand("myheli", "SpawnMyAttackcopterCommand");
        //AddCovalenceCommand("noheli", "KillMyAttackcopterCommand");
        //AddCovalenceCommand("gheli", "GetMyAttackMyCopterCommand");
        //AddCovalenceCommand("wheli", "WhereisMyAttackMyCopterCommand");
        //AddCovalenceCommand("reheli", "ReSpawnMyAttackcopterCommand");
        //AddCovalenceCommand("hheli", "HoverMyAttackcopterCommand");

        Permissions.RegisterPermission(Name, AttackcopterSpawn);
        Permissions.RegisterPermission(Name, AttackcopterFetch);
        Permissions.RegisterPermission(Name, AttackcopterWhere);
        Permissions.RegisterPermission(Name, AttackcopterAdmin);
        Permissions.RegisterPermission(Name, AttackcopterCooldown);
        Permissions.RegisterPermission(Name, AttackcopterUnlimited);
        //Permissions.RegisterPermission(Name, AttackcopterCanHover);
    }

    private void Unload()
    {
        SaveData();
        //foreach (KeyValuePair<int, Hovering> hover in hovers) UnityEngine.Object.Destroy(hover.Value);
    }
    #endregion

    #region Messages
    public override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
            {
                {"MyAttackHelp", "Spawn attackcopter in front of you." },
                {"NoAttackHelp", "Destroy your attackcopter if in range ({0} meters)." },
                {"WAttackHelp", "Find your attackcopter." },
                {"GetAttackHelp", "Retrieve your attackcopter." },
                {"AlreadyMsg", "You already have a attack helicopter.\nUse command '/noheli' to remove it."},
                {"SpawnedMsg", "Your attack copter has spawned !\nUse command '/noheli' to remove it."},
                {"KilledMsg", "Your attack copter has been removed/killed."},
                {"NoPermMsg", "You are not allowed to do this."},
                {"RaidBlockMsg", "You are not allowed to do this while raid blocked!"},
                {"SpawnUsage", "You need to supply a valid SteamId."},
                {"NoFoundMsg", "You do not have an active copter."},
                {"FoundMsg", "Your copter is located at {0}."},
                {"CooldownMsg", "You must wait {0} seconds before spawning a new attack copter."},
                {"DistanceMsg", "You must be within {0} meters of your attack copter."},
                {"FlyingMsg", "Your copter is currently flying and cannot be fetched."},
                {"RunningMsg2", "Your copter is currently running and cannot be fetched."},
                {"BlockedMsg", "You cannot spawn or fetch your copter while building blocked."},
                {"NotFlying", "The copter is not flying" },
                {"NoAccess", "You do not have permission to access this attackcopter" },
                {"NoPermission", "You do not have permission to hover" },
                {"HoverEnabled", "AttackCopter hover: enabled" },
                {"HoverDisabled", "AttackCopter hover: disabled" },
                {"NotInHelicopter", "You are not in a attackcopter" },
                {"NoPassengerToggle", "Passengers cannot toggle hover" }
            }, Name);

        //lang.RegisterMessages(new Dictionary<string, string>
        //    {
        //        {"MyAttackHelp", "Créez un attack hélicoptère devant vous." },
        //        {"NoAttackHelp", "Détruisez votre attack hélicoptère si il est à portée. ({0} mètres)." },
        //        {"GetAttackHelp", "Récupérez votre attack hélicoptère." },
        //        {"AlreadyMsg", "Vous avez déjà un attack hélicoptère\nUtilisez la commande '/noheli' pour le supprimer."},
        //        {"SpawnedMsg", "Votre attack hélico est arrivé !\nUtilisez la commande '/noheli' pour le supprimer."},
        //        {"KilledMsg", "Votre attack hélico a disparu du monde."},
        //        {"NoPermMsg", "Vous n'êtes pas autorisé."},
        //        {"RaidBlockMsg", "Vous n'êtes pas autorisé à faire cela pendant que le raid est bloqué!"},
        //        {"SpawnUsage", "Vous devez fournir un SteamId valide."},
        //        {"NoFoundMsg", "Vous n'avez pas de attack hélico actif"},
        //        {"FoundMsg", "Votre attack hélico est situé à {0}."},
        //        {"CooldownMsg", "Vous devez attendre {0} secondes avant de créer un nouveau attack hélico."},
        //        {"DistanceMsg", "Vous devez être à moins de {0} mètres de votre attack-hélico."},
        //        {"BlockedMsg", "Vous ne pouvez pas faire apparaître ou aller chercher votre hélico lorsque la construction est bloquée."},
        //        {"NotFlying", "L'hélicoptère ne vole pas"},
        //        {"NoPermission", "Vous n'êtes pas autorisé à survoler" },
        //        {"HoverEnabled", "Vol stationnaire attack hélicoptère: activé" },
        //        {"HoverDisabled", "Vol stationnaire attack hélicoptère: désactivé" },
        //        {"NotInHelicopter", "Vous n'êtes pas dans un attack hélicoptère" },
        //        {"NoPassengerToggle", "Les passagers ne peuvent pas basculer en vol stationnaire" }
        //    }, "fr");
    }

    private void OnPlayerInput(BasePlayer player, InputState input)
    {
        if (player?.UserIDString.IsSteamId() != true || input == null) return;
        if (!configData.Global.UseKeystrokeForHover) return;
        //if (!Permissions.UserHasPermission(AttackcopterCanHover, player.UserIDString)) return;
        ////if (input.current.buttons > 0) DoLog($"OnPlayerInput: {input.current.buttons}");
        //if (!player.isMounted) return;
        //int hoverkey = configData.Global.HoverKey > 0 ? configData.Global.HoverKey : (int)BUTTON.FIRE_THIRD; // MMB
        //bool dohover = input.current.buttons == hoverkey;
        //bool stabilize = input.current.buttons == (int)BUTTON.BACKWARD;

        //if (!(dohover || stabilize)) return;
        //PlayerHelicopter attack = player.GetMountedVehicle() as PlayerHelicopter;
        //if (attack == null) return;

        //DoLog($"Stabilize: {stabilize}, hover toggle: {dohover}");
        //DoLog($"HoverDelay: {hoverDelayTimers.ContainsKey(player.userID)}");
        //// Process hoverDelayTimers for user regardless of hover or stabilize.
        ////   If trying to hover and timer not expired, return.
        ////   If timer expired, remove timer and continue.
        //if (hoverDelayTimers.ContainsKey(player.userID))
        //{
        //    if (DateTime.Now - hoverDelayTimers[player.userID] < TimeSpan.FromMilliseconds(1000) && dohover)
        //    {
        //        DoLog("Hover delay not elapsed, returning");
        //        return;
        //    }
        //    hoverDelayTimers.Remove(player.userID);
        //}

        //// Now, if trying to hover, setup a new delay timer for the next keystroke.
        //if (dohover)
        //{
        //    DoLog("Resetting hover delay timer");
        //    hoverDelayTimers.Remove(player.userID);
        //    hoverDelayTimers.Add(player.userID, DateTime.Now);
        //}

        //// Process hover or stablize
        //if (storedData.playerattackID.ContainsKey(player.userID) && attack?.net.ID.Value == storedData.playerattackID[player.userID].Value)
        //{
        //    if (dohover && player != attack?.GetDriver() && !configData.Global.PassengerCanToggleHover)
        //    {
        //        Message(player, "NoPassengerToggle");
        //        return;
        //    }

        //    //if (attack.IsEngineOn() && attack.GetDriver())
        //    //{
        //    //    int iid = attack.GetInstanceID();
        //    //    DoLog($"Hovers contains {iid}: {hovers.ContainsKey(iid)}");
        //    //    if (stabilize && hovers.ContainsKey(iid) && hovers[iid].isHovering)
        //    //    {
        //    //        DoLog($"Stabilizing {attack.net.ID}");
        //    //        hovers[iid]?.Stabilize();
        //    //    }
        //    //    else if (dohover && hovers.ContainsKey(iid))
        //    //    {
        //    //        DoLog($"Toggling hover for {attack.net.ID}");
        //    //        hovers[iid]?.ToggleHover();
        //    //    }
        //    //}
        //}
    }

    private object OnEngineStart(AttackHelicopter attack)
    {
        if (storedData.playerattackID.ContainsValue(attack.net.ID))
        {
            BasePlayer player = BasePlayer.Find(attack.OwnerID.ToString());
            if (player != null)
            {
                VIPSettings vipsettings;
                GetVIPSettings(player, out vipsettings);
                bool fast = vipsettings != null ? vipsettings.FastStart : configData.Global.FastStart;
                if (fast)
                {
                    //attack?.engineController?.FinishStartingEngine();
                }
            }
        }
        return null;
    }

    // Chat message to online player with ulong
    private void ChatPlayerOnline(ulong userid, string message)
    {
        BasePlayer player = BasePlayer.FindByID(userid);
        if (player != null)
        {
            Message(player, Lang("KilledMsg"));
        }
    }
    #endregion

    #region Commands
    // Chat spawn
    [Command("myheli")]
    private void SpawnMyAttackcopterCommand(BasePlayer player, string command, string[] args)
    {
        double secondsSinceEpoch = DateTime.UtcNow.Subtract(epoch).TotalSeconds;

        if (!Permissions.UserHasPermission(AttackcopterSpawn, player.UserIDString))
        {
            Message(player, "NoPermMsg");
            return;
        }
        if (IsRaidBlocked(player))
        {
            Message(player, "RaidBlockMsg");
            return;
        }

        if (storedData.playerattackID.ContainsKey(player.userID))
        {
            if (!configData.Global.allowRespawnWhenActive)
            {
                Message(player, "AlreadyMsg");
                return;
            }
            KillMyAttackcopterPlease(player, true);
        }

        if (player.IsBuildingBlocked() && !configData.Global.allowWhenBlocked)
        {
            Message(player, "BlockedMsg");
            return;
        }

        bool hascooldown = Permissions.UserHasPermission(AttackcopterCooldown, player.UserIDString);
        if (!configData.Global.useCooldown) hascooldown = false;

        int secsleft;
        if (hascooldown)
        {
            VIPSettings vipsettings;
            GetVIPSettings(player, out vipsettings);
            float cooldownMin = vipsettings != null ? vipsettings.cooldownmin : configData.Global.cooldownmin;

            if (!storedData.playercounter.ContainsKey(player.userID))
            {
                storedData.playercounter.Add(player.userID, secondsSinceEpoch);
                SaveData();
            }
            else
            {
                double count;
                storedData.playercounter.TryGetValue(player.userID, out count);

                if ((secondsSinceEpoch - count) > (cooldownMin * 60))
                {
                    DoLog("Player reached cooldown.  Clearing data.");
                    storedData.playercounter.Remove(player.userID);
                    SaveData();
                }
                else
                {
                    secsleft = Math.Abs((int)((cooldownMin * 60) - (secondsSinceEpoch - count)));

                    if (secsleft > 0)
                    {
                        DoLog($"Player DID NOT reach cooldown. Still {secsleft} secs left.");
                        Message(player, "CooldownMsg", secsleft.ToString());
                        return;
                    }
                }
            }
        }
        else
        {
            if (storedData.playercounter.ContainsKey(player.userID))
            {
                storedData.playercounter.Remove(player.userID);
                SaveData();
            }
        }
        SpawnMyAttackcopter(player);
    }

    // Fetch copter
    [Command("gheli")]
    private void GetMyAttackMyCopterCommand(BasePlayer player, string command, string[] args)
    {
        if (player.IsBuildingBlocked() && !configData.Global.allowWhenBlocked)
        {
            Message(player, "BlockedMsg");
            return;
        }

        bool canspawn = Permissions.UserHasPermission(AttackcopterSpawn, player.UserIDString);
        bool canfetch = Permissions.UserHasPermission(AttackcopterFetch, player.UserIDString);
        if (!(canspawn && canfetch))
        {
            Message(player, "NoPermMsg");
            return;
        }
        if (IsRaidBlocked(player))
        {
            Message(player, "RaidBlockMsg");
            return;
        }

        VIPSettings vipsettings;
        GetVIPSettings(player, out vipsettings);
        bool vip = vipsettings != null;

        if (storedData.playerattackID.ContainsKey(player.userID))
        {
            NetworkableId findme;
            storedData.playerattackID.TryGetValue(player.userID, out findme);
            BaseNetworkable foundent = BaseNetworkable.serverEntities.Find(findme);
            if (foundent != null)
            {
                // Distance check - need a Y check as well... maybe.
                float gattackDistance = vip ? vipsettings.gattackdistance : configData.Global.gattackdistance;
                if (gattackDistance > 0f && Vector3.Distance(player.transform.position, foundent.transform.position) > gattackDistance)
                {
                    Message(player, "DistanceMsg", gattackDistance);
                    return;
                }

                AttackHelicopter copter = foundent as AttackHelicopter;
                float terrainHeight = TerrainMeta.HeightMap.GetHeight(foundent.transform.position);
                if (copter.CurEngineState == VehicleEngineController<PlayerHelicopter>.EngineState.On)
                {
                    if (!configData.Global.StopEngineOnGAttack)
                    {
                        Message(player, "RunningMsg2");
                        return;
                    }
                    VehicleEngineController<PlayerHelicopter> EngineController = copter.gameObject.GetComponent<VehicleEngineController<PlayerHelicopter>>();
                    EngineController?.StopEngine();
                }

                // Check for and dismount all players before moving the copter
                foreach (BaseVehicle.MountPointInfo mountPointInfo in copter.mountPoints)
                {
                    if (mountPointInfo.mountable != null)
                    {
                        BasePlayer mounted = mountPointInfo.mountable.GetMounted();
                        if (mounted)
                        {
                            if (mounted.transform.position.y - terrainHeight > 10f)
                            {
                                Message(player, "FlyingMsg");
                                return;
                            }

                            Vector3 player_pos = mounted.transform.position + new Vector3(1, 0, 1);
                            mounted.DismountObject();
                            mounted.MovePosition(player_pos);
                            mounted.SendNetworkUpdateImmediate(false);
                            mounted.ClientRPC(RpcTarget.Player("ForcePositionTo", mounted));
                            mountPointInfo.mountable.DismountAllPlayers();
                        }
                    }
                }
                Vector3 newLoc = new Vector3(player.transform.position.x + 2f, player.transform.position.y + 2f, player.transform.position.z + 2f);
                foundent.transform.position = newLoc;
                Message(player, "FoundMsg", newLoc);
            }
        }
        else
        {
            Message(player, "NoFoundMsg");
        }
    }

    // Find copter
    [Command("wheli")]
    private void WhereisMyAttackMyCopterCommand(BasePlayer player, string command, string[] args)
    {
        if (!Permissions.UserHasPermission(AttackcopterWhere, player.UserIDString))
        {
            Message(player, "NoPermMsg");
            return;
        }
        if (storedData.playerattackID.ContainsKey(player.userID))
        {
            NetworkableId findme;
            storedData.playerattackID.TryGetValue(player.userID, out findme);
            BaseNetworkable foundit = BaseNetworkable.serverEntities.Find(findme);
            if (foundit != null)
            {
                string loc = foundit.transform.position.ToString();
                Message(player, "FoundMsg", loc);
            }
        }
        else
        {
            Message(player, "NoFoundMsg");
        }
    }

    // Chat despawn
    [Command("reheli")]
    private void ReSpawnMyAttackcopterCommand(BasePlayer player, string command, string[] args)
    {
        if (IsRaidBlocked(player))
        {
            Message(player, "RaidBlockMsg");
            return;
        }
        KillMyAttackcopterCommand(player, "noheli", new string[0]);
        SpawnMyAttackcopterCommand(player, "myheli", new string[0]);
    }

    [Command("noheli")]
    private void KillMyAttackcopterCommand(BasePlayer player, string command, string[] args)
    {
        if (!Permissions.UserHasPermission(AttackcopterSpawn, player.UserIDString))
        {
            Message(player, "NoPermMsg");
            return;
        }
        if (IsRaidBlocked(player))
        {
            Message(player, "RaidBlockMsg");
            return;
        }
        KillMyAttackcopterPlease(player);
    }
    #endregion

    #region consolecommands
    // Console spawn
    private void SpawnMyAttackcopterConsoleCommand(ConsoleSystem.Arg arg)
    {
        if (arg.IsRcon)
        {
            if (arg.Args == null)
            {
                DoLog("You need to supply a valid SteamId.");
                return;
            }
        }
        else if (!HasPermission(arg, AttackcopterAdmin))
        {
            Message(arg.Connection.player as BasePlayer, Lang("NoPermMsg"));
            return;
        }
        else if (arg.Args == null)
        {
            Message(arg.Connection.player as BasePlayer, Lang("SpawnUsage"));
            return;
        }

        if (arg.Args.Length == 1)
        {
            ulong steamid = Convert.ToUInt64(arg.Args[0]);
            if (steamid == 0) return;
            if (!steamid.IsSteamId()) return;
            BasePlayer player = BasePlayer.FindByID(steamid);
            if (player != null)
            {
                SpawnMyAttackcopter(player);
            }
        }
    }

    // Console despawn
    private void KillMyAttackcopterConsoleCommand(ConsoleSystem.Arg arg)
    {
        if (arg.IsRcon)
        {
            if (arg.Args == null)
            {
                DoLog("You need to supply a valid SteamId.");
                return;
            }
        }
        else if (!HasPermission(arg, AttackcopterAdmin))
        {
            Message(arg.Connection.player as BasePlayer, Lang("NoPermMsg"));
            return;
        }
        else if (arg.Args == null)
        {
            Message(arg.Connection.player as BasePlayer, Lang("SpawnUsage"));
            return;
        }

        if (arg.Args.Length == 1)
        {
            ulong steamid = Convert.ToUInt64(arg.Args[0]);
            if (steamid == 0) return;
            if (!steamid.IsSteamId()) return;
            BasePlayer player = BasePlayer.FindByID(steamid);
            if (player != null)
            {
                KillMyAttackcopterPlease(player);
            }
        }
    }
    #endregion

    #region ourhooks
    // Spawn hook
    private void SpawnMyAttackcopter(BasePlayer player)
    {
        if (player.IsBuildingBlocked() && !configData.Global.allowWhenBlocked)
        {
            Message(player, Lang("BlockedMsg"));
            return;
        }

        VIPSettings vipsettings;
        GetVIPSettings(player, out vipsettings);
        bool vip = vipsettings != null;

        Quaternion rotation = player.GetNetworkRotation();
        Vector3 forward = rotation * Vector3.forward;
        // Make straight perpendicular to up axis so we don't spawn into ground or above player's head.
        Vector3 straight = Vector3.Cross(Vector3.Cross(Vector3.up, forward), Vector3.up).normalized;
        Vector3 position = player.transform.position + (straight * 5f);
        position.y = player.transform.position.y + 2.5f;

        if (position == default(Vector3)) return;
        BaseVehicle vehicleAttack = (BaseVehicle)GameManager.server.CreateEntity(prefab, position, new Quaternion());
        if (vehicleAttack == null) return;
        vehicleAttack.OwnerID = player.userID;

        AttackHelicopter attackCopter = vehicleAttack as AttackHelicopter;
        DoLog("Checking fuelPerSec");
        FieldInfo fuelPerSec = typeof(Minicopter).GetField("fuelPerSec", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        DoLog("Checking fuel system");
        IFuelSystem fs = attackCopter?.GetComponent<VehicleEngineController<PlayerHelicopter>>()?.FuelSystem;
        ItemContainer fsinv = fs?.GetInventory();
        DoLog("Spawning copter");
        vehicleAttack.Spawn();
        //if (Permissions.UserHasPermission(AttackcopterCanHover, player.UserIDString))
        //{
        //    hovers.Add(attackCopter.GetInstanceID(), attackCopter.gameObject.AddComponent<Hovering>());
        //}
        if (Permissions.UserHasPermission(AttackcopterUnlimited, player.UserIDString) || (vip && vipsettings.unlimited))
        {
            // Set fuel requirements to 0
            DoLog("Setting fuel requirements to zero");
            fuelPerSec.SetValue(fs, 0);
            if (!configData.Global.allowFuelIfUnlimited && !(vip && vipsettings.canloot))
            {
                // If the player is not allowed to use the fuel container, add 1 fuel so the copter will start.
                // Also lock fuel container since there is no point in adding/removing fuel
                if (fs.IsValidEntityReference() == true)
                {
                    ItemManager.CreateByItemID(-946369541, 1)?.MoveToContainer(fsinv);
                    fsinv.MarkDirty();
                    fsinv.SetFlag(ItemContainer.Flag.NoItemInput, true);
                }
            }
        }
        else if (configData.Global.startingFuel > 0 || (vip && vipsettings.startingFuel > 0))
        {
            if (fsinv.IsValidEntityReference() == true)
            {
                float sf = vip ? vipsettings.startingFuel : configData.Global.startingFuel;
                ItemManager.CreateByItemID(-946369541, Convert.ToInt32(sf))?.MoveToContainer(fsinv);
                fsinv.MarkDirty();
            }
        }
        else
        {
            fuelPerSec.SetValue(fs, vip ? vipsettings.stdFuelConsumption : configData.Global.stdFuelConsumption);
        }

        Message(player, Lang("SpawnedMsg"));
        NetworkableId attackcopteruint = vehicleAttack.net.ID;
        DoLog($"SPAWNED attackCOPTER {attackcopteruint} for player {player?.displayName} OWNER {vehicleAttack?.OwnerID}");
        storedData.playerattackID.Remove(player.userID);
        ulong myKey = currentMounts.FirstOrDefault(x => x.Value == player.userID).Key;
        currentMounts.Remove(myKey);
        storedData.playerattackID.Add(player.userID, attackcopteruint);
        SaveData();

        attackCopter = null;
    }

    // Kill attackcopter hook
    private void KillMyAttackcopterPlease(BasePlayer player, bool killalways = false)
    {
        bool foundcopter = false;
        VIPSettings vipsettings;
        GetVIPSettings(player, out vipsettings);
        float minDistance = vipsettings != null ? vipsettings.mindistance : configData.Global.mindistance;

        if (minDistance == 0f || killalways)
        {
            foundcopter = true;
        }
        else
        {
            List<BaseEntity> copterlist = new List<BaseEntity>();
            Vis.Entities(player.transform.position, minDistance, copterlist);

            foreach (BaseEntity p in copterlist)
            {
                AttackHelicopter foundent = p.GetComponentInParent<AttackHelicopter>();
                if (foundent != null)
                {
                    foundcopter = true;
                }
            }
        }

        if (storedData.playerattackID.ContainsKey(player.userID) && foundcopter)
        {
            NetworkableId findPlayerId;
            storedData.playerattackID.TryGetValue(player.userID, out findPlayerId);
            BaseNetworkable tokill = BaseNetworkable.serverEntities.Find(findPlayerId);
            tokill?.Kill(BaseNetworkable.DestroyMode.Gib);
            storedData.playerattackID.Remove(player.userID);
            ulong myKey = currentMounts.FirstOrDefault(x => x.Value == player.userID).Key;
            currentMounts.Remove(myKey);

            if (storedData.playercounter.ContainsKey(player.userID) && !configData.Global.useCooldown)
            {
                storedData.playercounter.Remove(player.userID);
            }
            SaveData();
            LoadData();
        }
        else if (!foundcopter)
        {
            DoLog("Player too far from copter to destroy.");
            Message(player, Lang("DistanceMsg", null, minDistance));
        }
    }

    private bool IsRaidBlocked(BasePlayer player)
    {
        return false;
    }
    #endregion

    #region hooks
    private object CanMountEntity(BasePlayer player, BaseMountable mountable)
    {
        if (mountable == null) return null;
        AttackHelicopter attack = mountable?.GetComponentInParent<AttackHelicopter>();
        if (attack == null) return null;

        DoLog($"CanMountEntity: Player {player?.userID} wants to mount seat id {mountable?.net.ID}");
        NetworkableId currentseat = new NetworkableId(attack.net.ID.Value);
        currentseat.Value += 3; // Start with driver seat
        for (int i = 0; i < 2; i++)
        {
            // Find copter and seats in storedData
            DoLog($"  Is this our copter with ID {attack.net.ID.Value}?");
            if (storedData.playerattackID.ContainsValue(attack.net.ID))
            {
                DoLog("    yes, it is...");
                if (player?.UserIDString.IsSteamId() != true) return true; // Block mounting by NPCs
                BaseVehicle attackmount = BaseNetworkable.serverEntities.Find(attack.net.ID) as BaseVehicle;
                DoLog($"Does {player.userID} match {attackmount?.OwnerID}, or are they a friend?");
                if (!Utils.IsFriend(player.userID, attackmount.OwnerID))
                {
                    DoLog("Player does not own attackcopter, and is not a friend of the owner.");
                    Message(player, "NoAccess");
                    return false;
                }

                if (currentMounts.ContainsValue(player.userID))
                {
                    if (!player.GetMounted())
                    {
                        ulong myKey = currentMounts.FirstOrDefault(x => x.Value == player.userID).Key;
                        currentMounts.Remove(myKey);
                    }
                    return false;
                }
                break;
            }
            currentseat.Value++;
        }
        return null;
    }

    private void OnEntityMounted(BaseMountable mountable, BasePlayer player)
    {
        AttackHelicopter attack = mountable.GetComponentInParent<AttackHelicopter>();
        if (attack != null)
        {
            DoLog($"OnEntityMounted: Player {player.userID} mounted seat id {mountable.net.ID}");
            // Check this seat's ID to see if the copter is one of ours
            NetworkableId currentseat = new NetworkableId(attack.net.ID.Value);
            currentseat.Value += 3; // Start with driver seat
            for (int i = 0; i < 2; i++)
            {
                // Find copter in storedData
                DoLog($"Is this our copter with ID {attack.net.ID.Value}?");
                if (storedData.playerattackID.ContainsValue(attack.net.ID))
                {
                    DoLog("    yes, it is...");
                    DoLog($"Removing {player.displayName}'s ID {player.userID} from currentMounts for seat {mountable.net.ID} on {currentseat.Value}");
                    currentMounts.Remove(mountable.net.ID.Value);
                    DoLog($"Adding {player.displayName}'s ID {player.userID} to currentMounts for seat {mountable.net.ID} on {currentseat.Value}");
                    currentMounts.Add(mountable.net.ID.Value, player.userID);
                    break;
                }
                currentseat.Value++;
            }
        }
    }

    private object CanDismountEntity(BasePlayer player, BaseMountable mountable)
    {
        if (player?.UserIDString.IsSteamId() != true) return null;
        AttackHelicopter attack = mountable?.GetComponentInParent<AttackHelicopter>();
        DoLog($"CanDismountEntity: Player {player.userID} wants to dismount seat id {mountable.net.ID}");

        // Only operates if attack is not null and if we are flying above attackmum height
        if (attack != null && !Physics.Raycast(new Ray(mountable.transform.position, Vector3.down), configData.Global.minDismountHeight, layerMask))
        {
            DoLog($"Is this our copter with ID {attack.net.ID.Value}?");
            NetworkableId passenger = new NetworkableId(attack.net.ID.Value);
            passenger.Value += 4;
            NetworkableId driver = new NetworkableId(attack.net.ID.Value);
            driver.Value += 3;
            if (storedData.playerattackID.ContainsValue(attack.net.ID))
            {
                DoLog("    yes, it is...");
                if (!configData.Global.allowDriverDismountWhileFlying)
                {
                    DoLog("DENY PILOT DISMOUNT");
                    return false;
                }
                ulong myKey = currentMounts.FirstOrDefault(x => x.Value == player.userID).Key;
                currentMounts.Remove(myKey);
            }
            else if (storedData.playerattackID.ContainsValue(passenger))
            {
                if (!configData.Global.allowPassengerDismountWhileFlying)
                {
                    DoLog("DENY PASSENGER DISMOUNT");
                    return false;
                }
                ulong myKey = currentMounts.FirstOrDefault(x => x.Value == player.userID).Key;
                currentMounts.Remove(myKey);
            }
        }
        return null;
    }

    private void OnEntityDismounted(BaseMountable mountable, BasePlayer player)
    {
        AttackHelicopter attack = mountable.GetComponentInParent<AttackHelicopter>();
        if (attack != null)
        {
            DoLog($"OnEntityDismounted: Player {player.userID} dismounted seat id {mountable.net.ID}");
            NetworkableId currentseat = new NetworkableId(attack.net.ID.Value);
            currentseat.Value += 3; // Start with driver seat
            for (int i = 0; i < 2; i++)
            {
                // Find copter and seats in storedData
                DoLog($"Is this our copter with ID {attack.net.ID.Value}?");
                if (storedData.playerattackID.ContainsValue(attack.net.ID))
                {
                    DoLog("    yes, it is...");
                    DoLog($"Removing {player.displayName}'s ID {player.userID} from currentMounts for seat {mountable.net.ID} on {currentseat.Value}");
                    currentMounts.Remove(mountable.net.ID.Value);
                    break;
                }
                currentseat.Value++;
            }
        }
        ulong myKey = currentMounts.FirstOrDefault(x => x.Value == player.userID).Key;
        currentMounts.Remove(myKey);
    }

    // On kill - tell owner
    private void OnEntityKill(AttackHelicopter entity)
    {
        if (entity == null) return;
        if (entity.net.ID.Value == 0) return;

        if (storedData == null) return;
        if (storedData.playerattackID == null) return;
        ulong todelete = new ulong();

        if (!storedData.playerattackID.ContainsValue(entity.net.ID))
        {
            DoLog("KILLED non-plugin attackcopter");
            return;
        }
        foreach (KeyValuePair<ulong, NetworkableId> item in storedData.playerattackID)
        {
            if (item.Value == entity.net.ID)
            {
                ChatPlayerOnline(item.Key, "killed");
                BasePlayer player = BasePlayer.FindByID(item.Key);
                todelete = item.Key;
            }
        }
        if (todelete != 0)
        {
            storedData.playerattackID.Remove(todelete);
            currentMounts.Remove(entity.net.ID.Value);
            currentMounts.Remove(entity.net.ID.Value + 1);
            currentMounts.Remove(entity.net.ID.Value + 2);
            //hovers.Remove(entity.GetInstanceID());
            SaveData();
        }
    }

    private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
    {
        if (entity?.net?.ID == null) return null;
        if (hitInfo?.damageTypes == null) return null;

        if (storedData?.playerattackID?.ContainsValue(entity.net.ID) == true)
        {
            if (hitInfo?.damageTypes?.GetMajorityDamageType().ToString() == "Decay")
            {
                if (configData.Global.copterDecay)
                {
                    DoLog($"Enabling standard decay for spawned attackcopter {entity.net.ID}.");
                }
                else
                {
                    DoLog($"Disabling decay for spawned attackcopter {entity.net.ID}.");
                    hitInfo.damageTypes.Scale(Rust.DamageType.Decay, 0);
                }
                return null;
            }
            else
            {
                if (!configData.Global.allowDamage) return true;

                foreach (KeyValuePair<string, VIPSettings> vip in configData.VIPSettings)
                {
                    string perm = vip.Key.StartsWith($"{Name.ToLower()}.") ? vip.Key : $"{Name.ToLower()}.{vip.Key}";
                    if (Permissions.UserHasPermission(perm, entity.OwnerID.ToString()) && vip.Value is VIPSettings && !vip.Value.allowDamage)
                    {
                        return true;
                    }
                }
            }

        }
        return null;
    }

    private void OnPlayerDisconnected(BasePlayer player, string reason)
    {
        if (!configData.Global.killOnSleep) return;
        if (player?.UserIDString.IsSteamId() != true) return;

        if (storedData.playerattackID.ContainsKey(player.userID))
        {
            NetworkableId findAttackId;
            storedData.playerattackID.TryGetValue(player.userID, out findAttackId);
            BaseNetworkable tokill = BaseNetworkable.serverEntities.Find(findAttackId);
            if (tokill == null) return; // Didn't find it

            // Check for mounted players
            BaseVehicle copter = tokill as BaseVehicle;
            for (int i = 0; i < copter?.mountPoints.Count; i++)
            {
                BaseVehicle.MountPointInfo mountPointInfo = copter.mountPoints[i];
                if (mountPointInfo.mountable != null)
                {
                    BasePlayer mounted = mountPointInfo.mountable.GetMounted();
                    if (mounted)
                    {
                        DoLog("Copter owner sleeping but another one is mounted - cannot destroy copter");
                        return;
                    }
                }
            }
            DoLog("Copter owner sleeping - destroying copter");
            tokill.Kill();
            storedData.playerattackID.Remove(player.userID);
            ulong myKey = currentMounts.FirstOrDefault(x => x.Value == player.userID).Key;
            currentMounts.Remove(myKey);

            if (storedData.playercounter.ContainsKey(player.userID) && !configData.Global.useCooldown)
            {
                storedData.playercounter.Remove(player.userID);
            }
            SaveData();
        }
    }
    #endregion

    private void GetVIPSettings(BasePlayer player, out VIPSettings vipsettings)
    {
        if (player?.UserIDString.IsSteamId() != true)
        {
            DoLog("User has no VIP settings");
            vipsettings = null;
            return;
        }
        foreach (KeyValuePair<string, VIPSettings> vip in configData.VIPSettings)
        {
            string perm = vip.Key.StartsWith($"{Name.ToLower()}.") ? vip.Key : $"{Name.ToLower()}.{vip.Key}";
            if (Permissions.UserHasPermission(perm, player.UserIDString) && vip.Value is VIPSettings)
            {
                DoLog($"User has VIP setting {perm}");
                vipsettings = vip.Value;
                return; // No need to keep trying
            }
        }
        vipsettings = null;
    }

    private static BasePlayer FindPlayerById(ulong userid)
    {
        foreach (BasePlayer current in BasePlayer.allPlayerList)
        {
            if (current.userID == userid)
            {
                return current;
            }
        }
        return null;
    }

    #region config
    public class Global
    {
        public bool allowWhenBlocked;
        public bool allowRespawnWhenActive;
        public bool useCooldown;
        public bool useNoEscape;
        public bool useFriends;
        public bool useClans;
        public bool useTeams;
        public bool copterDecay;
        public bool allowDamage;
        public bool killOnSleep;
        public bool allowFuelIfUnlimited;
        public bool allowDriverDismountWhileFlying;
        public bool allowPassengerDismountWhileFlying;
        public bool debug;
        public bool StopEngineOnGAttack;
        public bool FastStart;
        public float stdFuelConsumption;
        public float cooldownmin;
        public float mindistance;
        public float gattackdistance;
        public float minDismountHeight;
        public float startingFuel;
        public string Prefix; // Chat prefix
        public bool TimedHover;
        public bool DisableHoverOnDismount;
        public bool EnableRotationOnHover;
        public bool PassengerCanToggleHover;
        public bool HoverWithoutEngine;
        public bool UseFuelOnHover;
        public float HoverDuration;
        public bool UseKeystrokeForHover;
        public int HoverKey;
    }

    public class VIPSettings
    {
        public bool unlimited;
        public bool FastStart;
        public bool canloot;
        public bool allowDamage;
        public float stdFuelConsumption;
        public float startingFuel;
        public float cooldownmin;
        public float mindistance;
        public float gattackdistance;
    }

    public class ConfigData
    {
        public Global Global;
        public Dictionary<string, VIPSettings> VIPSettings { get; set; }
        public VersionNumber Version;
    }

    private void LoadConfigVariables()
    {
        configData = config.ReadObject<ConfigData>();

        if (configData.VIPSettings == null)
        {
            configData.VIPSettings = new Dictionary<string, VIPSettings>();
        }

        configData.Version = Version;
        SaveConfig(configData);
    }

    public void LoadDefaultConfig()
    {
        DoLog("Creating new config file.");
        ConfigData config = new ConfigData
        {
            Global = new Global()
            {
                allowWhenBlocked = false,
                allowRespawnWhenActive = false,
                useCooldown = true,
                copterDecay = false,
                allowDamage = true,
                killOnSleep = false,
                allowFuelIfUnlimited = false,
                allowDriverDismountWhileFlying = true,
                allowPassengerDismountWhileFlying = true,
                stdFuelConsumption = 0.25f,
                cooldownmin = 60f,
                mindistance = 0f,
                gattackdistance = 0f,
                minDismountHeight = 7f,
                startingFuel = 0f,
                debug = false,
                Prefix = "[My AttackCopter]: ",
                useNoEscape = false,
                EnableRotationOnHover = true,
                DisableHoverOnDismount = true,
                PassengerCanToggleHover = false,
                HoverWithoutEngine = false,
                UseFuelOnHover = true,
                TimedHover = false,
                HoverDuration = 60,
                UseKeystrokeForHover = false,
                HoverKey = 134217728
            },
            Version = Version
        };
        SaveConfig(config);
    }

    private void SaveConfig(ConfigData configuration)
    {
        config.WriteObject(configuration, true);
    }

    private void SaveData()
    {
        // Save the data file as we add/remove attackcopters.
        data.WriteObject(Name, storedData);
    }

    private void LoadData()
    {
        storedData = data.ReadObject<StoredData>(Name);
        if (storedData == null)
        {
            storedData = new StoredData();
            SaveData();
        }
    }
    #endregion

    #region Hover
    //private class Hovering : MonoBehaviour
    //{
    //    // Portions borrowed from HelicopterHover plugin but modified
    //    private PlayerHelicopter _helicopter;
    //    AttackHelicopter _attackcopter;
    //    Rigidbody _rb;

    //    Timer _timedHoverTimer;
    //    Timer _fuelUseTimer;

    //    Coroutine _hoverCoroutine;
    //    VehicleEngineController<AttackHelicopter> _engineController;

    //    public bool isHovering => _rb.constraints == RigidbodyConstraints.FreezePositionY;

    //    public void Awake()
    //    {
    //        if (!TryGetComponent(out _helicopter))
    //        {
    //            Instance.DoLog("Failed to get BHV component for MyAttackCopter");
    //            Instance.hovers.Remove(_helicopter.GetInstanceID());
    //            DestroyImmediate(this);
    //            return;
    //        }
    //        if (!TryGetComponent(out _rb))
    //        {
    //            Instance.DoLog("Failed to get RB component for MyAttackCopter");
    //            Instance.hovers.Remove(_helicopter.GetInstanceID());
    //            DestroyImmediate(this);
    //            return;
    //        }
    //        _attackcopter = GetComponent<AttackHelicopter>();
    //        _engineController = _attackcopter?.gameObject.GetComponent<VehicleEngineController<AttackHelicopter>>();
    //    }

    //    public void ToggleHover()
    //    {
    //        Instance.DoLog("ToggleHover");
    //        if (isHovering) StopHover();
    //        else StartHover();

    //        foreach (BaseVehicle.MountPointInfo info in _helicopter.mountPoints)
    //        {
    //            BasePlayer player = info.mountable.GetMounted();
    //            //if (player != null) Instance.ChatPlayerOnline(player, lang.GetMessage(isHovering ? "HoverEnabled" : "HoverDisabled", Instance, player.UserIDString));
    //        }
    //    }

    //    public void StartHover()
    //    {
    //        Instance.DoLog("StartHover");
    //        _rb.constraints = RigidbodyConstraints.FreezePositionY;
    //        Instance.DoLog("Setting Freeze Rotation");
    //        if (!Instance.configData.Global.EnableRotationOnHover) _rb.freezeRotation = true;

    //        Instance.DoLog("Finishing Engine Start");
    //        _engineController?.FinishStartingEngine();

    //        Instance.DoLog("Starting Hover Coroutine");
    //        if (_helicopter != null) _hoverCoroutine = ServerMgr.Instance.StartCoroutine(HoveringCoroutine());
    //    }

    //    public void StopHover()
    //    {
    //        Instance.DoLog("StopHover");
    //        _rb.constraints = RigidbodyConstraints.None;
    //        Instance.DoLog("Disabling Freeze Rotation");
    //        _rb.freezeRotation = false;

    //        Instance.DoLog("Stopping Hover Coroutine");
    //        if (_hoverCoroutine != null) ServerMgr.Instance.StopCoroutine(_hoverCoroutine);
    //        if (_timedHoverTimer != null) _timedHoverTimer.Destroy();
    //        if (_fuelUseTimer != null) _fuelUseTimer.Destroy();
    //    }

    //    IEnumerator HoveringCoroutine() //Keep engine running and manage fuel
    //    {
    //        if (Instance.configData.Global.TimedHover) _timedHoverTimer = Instance.timer.Once(Instance.configData.Global.HoverDuration, () => StopHover());

    //        EntityFuelSystem fuelSystem = _attackcopter?.GetFuelSystem();
    //        /* Using GetDriver, the engine will begin stalling and then die in a few seconds if the playerowner moves to the passenger seat.
    //         * - The engine stops mid-air, which is not realistic.
    //         * - The playerowner can move back and the engine should start again.
    //         * Using GetMounted, the engine also stops mid-air.
    //         * - The playerowner can move back and restart the engine.
    //         * Can optionally just kill the hover if the engine stops for any reason - see FixedUpdate.
    //         */
    //        BasePlayer player = _helicopter.GetDriver();

    //        if (fuelSystem != null && !Permissions.UserHasPermission("attackcopter.unlimited", player.UserIDString))
    //        {
    //            if (Instance.configData.Global.UseFuelOnHover) _fuelUseTimer = Instance.timer.Every(1f, () =>
    //            {
    //                if (fuelSystem.HasFuel() && _attackcopter.GetDriver() == null) fuelSystem.TryUseFuel(1f, _attackcopter.fuelPerSec);
    //                else if (!fuelSystem.HasFuel()) _fuelUseTimer.Destroy();
    //            });
    //        }

    //        //Keep engine on
    //        while (isHovering)
    //        {
    //            if (!(_engineController?.IsOn ?? false) && (_helicopter.AnyMounted() || !Instance.configData.Global.DisableHoverOnDismount)) _engineController?.FinishStartingEngine();

    //            if (fuelSystem != null)
    //            {
    //                if (!fuelSystem.HasFuel() && !Permissions.UserHasPermission("attackcopter.unlimited", player.UserIDString)) //If no fuel, stop hovering
    //                {
    //                    StopHover();
    //                    _engineController?.StopEngine();

    //                    yield break;
    //                }
    //            }

    //            yield return null;
    //        }
    //    }

    //    public void Stabilize()
    //    {
    //        if (!isHovering) return;
    //        Instance.DoLog("Fixing rotation to stabilize position");
    //        Quaternion q = Quaternion.FromToRotation(_attackcopter.transform.up, Vector3.up) * _attackcopter.transform.rotation;
    //        _attackcopter.transform.rotation = Quaternion.Slerp(_attackcopter.transform.rotation, q, Time.deltaTime * 3.5f);
    //    }

    //    private void FixedUpdate()
    //    {
    //        bool found = false;
    //        foreach (BaseVehicle.MountPointInfo info in _helicopter.mountPoints)
    //        {
    //            if (info.mountable.GetMounted())
    //            {
    //                found = true;
    //            }
    //        }

    //        if (!found && isHovering && Instance.configData.Global.DisableHoverOnDismount)
    //        {
    //            StopHover();
    //        }
    //        else if (_engineController.IsOff && isHovering && !Instance.configData.Global.HoverWithoutEngine)
    //        {
    //            StopHover();
    //        }
    //    }

    //    private void OnDestroy() //Stop any timers or coroutines persisting after destruction or plugin unload
    //    {
    //        if (_hoverCoroutine != null) ServerMgr.Instance.StopCoroutine(_hoverCoroutine);
    //        _timedHoverTimer?.Destroy();
    //        _fuelUseTimer?.Destroy();
    //    }
    //}
    #endregion
}
