#region License (GPL v2)
/*
    DESCRIPTION
    Copyright (c) 2024 RFC1920 <desolationoutpostpve@gmail.com>

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
using Auxide;
using UnityEngine;
using System.Collections.Generic;
using System;
using System.Linq;

internal class MyBicycle : RustScript
{
    public MyBicycle()
    {
        Author = "RFC1920";
        Description = "Spawn a bicycle for Auxide";
        Version = new VersionNumber(0, 1, 0);
    }
    public static MyBicycle Instance;

    private const string prefab = "assets/content/vehicles/bikes/pedalbike.prefab";
    private ConfigData configData;

    private const string bicycleSpawn = "mybicycle.spawn";
    private const string bicycleFetch = "mybicycle.fetch";
    private const string bicycleWhere = "mybicycle.where";
    private const string bicycleAdmin = "mybicycle.admin";
    private const string bicycleCooldown = "mybicycle.cooldown";
    private const string bicycleUnlimited = "mybicycle.unlimited";

    private static LayerMask layerMask = LayerMask.GetMask("Terrain", "World", "Construction");

    private Dictionary<ulong, ulong> currentMounts = new Dictionary<ulong, ulong>();
    private static readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0);

    private class StoredData
    {
        public Dictionary<ulong, NetworkableId> bikeID = new Dictionary<ulong, NetworkableId>();
        public Dictionary<ulong, double> playercounter = new Dictionary<ulong, double>();
    }
    private StoredData storedData;

    private bool HasPermission(ConsoleSystem.Arg arg, string permname)
    {
        return !(arg.Connection.player is BasePlayer) || Permissions.UserHasPermission(permname, (arg.Connection.player as BasePlayer)?.UserIDString);
    }

    #region loadunload
    public override void Initialize()
    {
        Instance = this;

    }

    private void OnNewSave()
    {
        storedData = new StoredData();
        SaveData();
    }

    public void OnScriptLoaded()
    {
        Permissions.RegisterPermission(Name, bicycleSpawn);
        Permissions.RegisterPermission(Name, bicycleFetch);
        Permissions.RegisterPermission(Name, bicycleWhere);
        Permissions.RegisterPermission(Name, bicycleAdmin);
        Permissions.RegisterPermission(Name, bicycleCooldown);
        Permissions.RegisterPermission(Name, bicycleUnlimited);

        LoadConfig();

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
            DoLog("Please set a longer cooldown time. minimum is 2 min.");
            configData.Global.cooldownmin = 2;
            SaveConfig(configData);
        }

        LoadData();
    }

    private void Unload()
    {
        SaveData();
    }
    #endregion

    #region Messages
    public override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
       {
           {"MyHelp", "Spawn bicycle in front of you." },
           {"NoHelp", "Destroy your bicycle if in range ({0} meters)." },
           {"WAHelp", "Find your bicycle." },
           {"GetHelp", "Retrieve your bicycle." },
           {"AlreadyMsg", "You already have a bike.\nUse command '/nobike' to remove it."},
           {"SpawnedMsg", "Your bike has spawned !\nUse command '/nobike' to remove it."},
           {"KilledMsg", "Your bike has been removed/killed."},
           {"NoPermMsg", "You are not allowed to do this."},
           {"RaidBlockMsg", "You are not allowed to do this while raid blocked!"},
           {"SpawnUsage", "You need to supply a valid SteamId."},
           {"NoFoundMsg", "You do not have an active bike."},
           {"FoundMsg", "Your bike is located at {0}."},
           {"CooldownMsg", "You must wait {0} seconds before spawning a new bike."},
           {"DistanceMsg", "You must be within {0} meters of your bike."},
           {"FlyingMsg", "Your bike is currently flying and cannot be fetched."},
           {"RunningMsg2", "Your bike is currently running and cannot be fetched."},
           {"BlockedMsg", "You cannot spawn or fetch your bike while building blocked."},
           {"NotFlying", "The bike is not flying" },
           {"NoAccess", "You do not have permission to access this bicycle" },
           {"NotInbikebike", "You are not in a bicycle" }
       }, Name);
    }

    private object OnEngineStart(Bike bike)
    {
        if (storedData.bikeID.ContainsValue(bike.net.ID))
        {
            BasePlayer player = BasePlayer.Find(bike.OwnerID.ToString());
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
    public void OnChatCommand(BasePlayer player, string command, string[] args = null)
    {
        if (player == null) return;
        switch (command)
        {
            case "mybike":
                SpawnMyBikeCommand(player, command, args);
                break;
            case "gbike":
                GetMyBikeCommand(player, command, args);
                break;
            case "wbike":
                WhereisMyBikeCommand(player, command, args);
                break;
            case "rebike":
                ReSpawnMyBikeCommand(player, command, args);
                break;
            case "nobike":
                KillMyBikeCommand(player, command, args);
                break;
        }
    }

    // Chat spawn
    [Command("mybike")]
    private void SpawnMyBikeCommand(BasePlayer player, string command, string[] args)
    {
        double secondsSinceEpoch = DateTime.UtcNow.Subtract(epoch).TotalSeconds;

        if (!Permissions.UserHasPermission(bicycleSpawn, player.UserIDString))
        {
            Message(player, "NoPermMsg");
            return;
        }
        if (IsRaidBlocked(player))
        {
            Message(player, "RaidBlockMsg");
            return;
        }

        if (storedData.bikeID.ContainsKey(player.userID))
        {
            if (!configData.Global.allowRespawnWhenActive)
            {
                Message(player, "AlreadyMsg");
                return;
            }
            KillMyBikePlease(player, true);
        }

        if (player.IsBuildingBlocked() && !configData.Global.allowWhenBlocked)
        {
            Message(player, "BlockedMsg");
            return;
        }

        bool hascooldown = Permissions.UserHasPermission(bicycleCooldown, player.UserIDString);
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
        SpawnMyBike(player);
    }

    // Fetch bike
    [Command("gbike")]
    private void GetMyBikeCommand(BasePlayer player, string command, string[] args)
    {
        if (player.IsBuildingBlocked() && !configData.Global.allowWhenBlocked)
        {
            Message(player, "BlockedMsg");
            return;
        }

        bool canspawn = Permissions.UserHasPermission(bicycleSpawn, player.UserIDString);
        bool canfetch = Permissions.UserHasPermission(bicycleFetch, player.UserIDString);
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

        if (storedData.bikeID.ContainsKey(player.userID))
        {
            NetworkableId findme;
            storedData.bikeID.TryGetValue(player.userID, out findme);
            BaseNetworkable foundent = BaseNetworkable.serverEntities.Find(findme);
            if (foundent != null)
            {
                // Distance check - need a Y check as well... maybe.
                float getDistance = vip ? vipsettings.getdistance : configData.Global.getdistance;
                if (getDistance > 0f && Vector3.Distance(player.transform.position, foundent.transform.position) > getDistance)
                {
                    Message(player, "DistanceMsg", getDistance);
                    return;
                }

                Bike bike = foundent as Bike;
                float terrainHeight = TerrainMeta.HeightMap.GetHeight(foundent.transform.position);
                if (bike.CurEngineState == VehicleEngineController<GroundVehicle>.EngineState.On)
                {
                    if (!configData.Global.StopEngineOnAttack)
                    {
                        Message(player, "RunningMsg2");
                        return;
                    }
                    VehicleEngineController<Bike> EngineController = bike.gameObject.GetComponent<VehicleEngineController<Bike>>();
                    EngineController?.StopEngine();
                }

                // Check for and dismount all players before moving the bike
                foreach (BaseVehicle.MountPointInfo mountPointInfo in bike.mountPoints)
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
                            mounted.ClientRPC(RpcTarget.Player("ForcePositionTo", player), player_pos);
                            //mountPointInfo.mountable._mounted = null;
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

    // Find bike
    private void WhereisMyBikeCommand(BasePlayer player, string command, string[] args)
    {
        if (!Permissions.UserHasPermission(bicycleWhere, player.UserIDString))
        {
            Message(player, "NoPermMsg");
            return;
        }
        if (storedData.bikeID.ContainsKey(player.userID))
        {
            NetworkableId findme;
            storedData.bikeID.TryGetValue(player.userID, out findme);
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
    private void ReSpawnMyBikeCommand(BasePlayer player, string command, string[] args)
    {
        if (IsRaidBlocked(player))
        {
            Message(player, "RaidBlockMsg");
            return;
        }
        KillMyBikeCommand(player, "nobike", new string[0]);
        SpawnMyBikeCommand(player, "mybike", new string[0]);
    }

    private void KillMyBikeCommand(BasePlayer player, string command, string[] args)
    {
        if (!Permissions.UserHasPermission(bicycleSpawn, player.UserIDString))
        {
            Message(player, "NoPermMsg");
            return;
        }
        if (IsRaidBlocked(player))
        {
            Message(player, "RaidBlockMsg");
            return;
        }
        KillMyBikePlease(player);
    }
    #endregion

    #region ourhooks
    // Spawn hook
    private void SpawnMyBike(BasePlayer player)
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
        BaseVehicle bicycle = (BaseVehicle)GameManager.server.CreateEntity(prefab, position, new Quaternion());
        if (bicycle == null) return;
        bicycle.OwnerID = player.userID;

        bicycle.Spawn();

        Message(player, Lang("SpawnedMsg"));
        NetworkableId bicycleuint = bicycle.net.ID;
        DoLog($"SPAWNED bicycle {bicycleuint} for player {player?.displayName} OWNER {bicycle?.OwnerID}");
        storedData.bikeID.Remove(player.userID);
        ulong myKey = currentMounts.FirstOrDefault(x => x.Value == player.userID).Key;
        currentMounts.Remove(myKey);
        storedData.bikeID.Add(player.userID, bicycleuint);
        SaveData();

        bicycle = null;
    }

    // Kill bicycle hook
    private void KillMyBikePlease(BasePlayer player, bool killalways = false)
    {
        bool foundbike = false;
        VIPSettings vipsettings;
        GetVIPSettings(player, out vipsettings);
        float minDistance = vipsettings != null ? vipsettings.mindistance : configData.Global.mindistance;

        if (minDistance == 0f || killalways)
        {
            foundbike = true;
        }
        else
        {
            List<BaseEntity> bikelist = new List<BaseEntity>();
            Vis.Entities(player.transform.position, minDistance, bikelist);

            foreach (BaseEntity p in bikelist)
            {
                Bike foundent = p.GetComponentInParent<Bike>();
                if (foundent != null)
                {
                    foundbike = true;
                }
            }
        }

        if (storedData.bikeID.ContainsKey(player.userID) && foundbike)
        {
            NetworkableId findPlayerId;
            storedData.bikeID.TryGetValue(player.userID, out findPlayerId);
            BaseNetworkable tokill = BaseNetworkable.serverEntities.Find(findPlayerId);
            tokill?.Kill(BaseNetworkable.DestroyMode.Gib);
            storedData.bikeID.Remove(player.userID);
            ulong myKey = currentMounts.FirstOrDefault(x => x.Value == player.userID).Key;
            currentMounts.Remove(myKey);

            if (storedData.playercounter.ContainsKey(player.userID) && !configData.Global.useCooldown)
            {
                storedData.playercounter.Remove(player.userID);
            }
            SaveData();
            LoadData();
        }
        else if (!foundbike)
        {
            DoLog("Player too far from bike to destroy.");
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
        Bike attack = mountable?.GetComponentInParent<Bike>();
        if (attack == null) return null;

        DoLog($"CanMountEntity: Player {player?.userID} wants to mount seat id {mountable?.net.ID}");
        NetworkableId currentseat = new NetworkableId(attack.net.ID.Value);
        currentseat.Value += 3; // Start with driver seat
        for (int i = 0; i < 2; i++)
        {
            // Find bike and seats in storedData
            DoLog($"  Is this our bike with ID {attack.net.ID.Value}?");
            if (storedData.bikeID.ContainsValue(attack.net.ID))
            {
                DoLog("    yes, it is...");
                if (player?.userID.IsSteamId() != true) return true; // Block mounting by NPCs
                BaseVehicle attackmount = BaseNetworkable.serverEntities.Find(attack.net.ID) as BaseVehicle;
                DoLog($"Does {player.userID} match {attackmount?.OwnerID}, or are they a friend?");
                if (!Utils.IsFriend(player.userID, attackmount.OwnerID))
                {
                    DoLog("Player does not own bicycle, and is not a friend of the owner.");
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
        Bike attack = mountable.GetComponentInParent<Bike>();
        if (attack != null)
        {
            DoLog($"OnEntityMounted: Player {player.userID} mounted seat id {mountable.net.ID}");
            // Check this seat's ID to see if the bike is one of ours
            NetworkableId currentseat = new NetworkableId(attack.net.ID.Value);
            currentseat.Value += 3; // Start with driver seat
            for (int i = 0; i < 2; i++)
            {
                // Find bike in storedData
                DoLog($"Is this our bike with ID {attack.net.ID.Value}?");
                if (storedData.bikeID.ContainsValue(attack.net.ID))
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

    private void OnEntityDismounted(BaseMountable mountable, BasePlayer player)
    {
        Bike attack = mountable.GetComponentInParent<Bike>();
        if (attack != null)
        {
            DoLog($"OnEntityDismounted: Player {player.userID} dismounted seat id {mountable.net.ID}");
            NetworkableId currentseat = new NetworkableId(attack.net.ID.Value);
            currentseat.Value += 3; // Start with driver seat
            for (int i = 0; i < 2; i++)
            {
                // Find bike and seats in storedData
                DoLog($"Is this our bike with ID {attack.net.ID.Value}?");
                if (storedData.bikeID.ContainsValue(attack.net.ID))
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
    private void OnEntityKill(Bike entity)
    {
        if (entity == null) return;
        if (entity.net.ID.Value == 0) return;

        if (storedData == null) return;
        if (storedData.bikeID == null) return;
        ulong todelete = new ulong();

        if (!storedData.bikeID.ContainsValue(entity.net.ID))
        {
            DoLog("KILLED non-plugin bicycle");
            return;
        }
        foreach (KeyValuePair<ulong, NetworkableId> item in storedData.bikeID)
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
            storedData.bikeID.Remove(todelete);
            currentMounts.Remove(entity.net.ID.Value);
            currentMounts.Remove(entity.net.ID.Value + 1);
            currentMounts.Remove(entity.net.ID.Value + 2);
            SaveData();
        }
    }

    private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
    {
        if (entity?.net?.ID == null) return null;
        if (hitInfo?.damageTypes == null) return null;

        if (storedData?.bikeID?.ContainsValue(entity.net.ID) == true)
        {
            if (hitInfo?.damageTypes?.GetMajorityDamageType().ToString() == "Decay")
            {
                if (configData.Global.bikeDecay)
                {
                    DoLog($"Enabling standard decay for spawned bicycle {entity.net.ID}.");
                }
                else
                {
                    DoLog($"Disabling decay for spawned bicycle {entity.net.ID}.");
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
        if (player?.userID.IsSteamId() != true) return;

        if (storedData.bikeID.ContainsKey(player.userID))
        {
            NetworkableId findBikeId;
            storedData.bikeID.TryGetValue(player.userID, out findBikeId);
            BaseNetworkable tokill = BaseNetworkable.serverEntities.Find(findBikeId);
            if (tokill == null) return; // Didn't find it

            // Check for mounted players
            BaseVehicle bike = tokill as BaseVehicle;
            for (int i = 0; i < bike?.mountPoints.Count; i++)
            {
                BaseVehicle.MountPointInfo mountPointInfo = bike.mountPoints[i];
                if (mountPointInfo.mountable != null)
                {
                    BasePlayer mounted = mountPointInfo.mountable.GetMounted();
                    if (mounted)
                    {
                        DoLog("bike owner sleeping but another one is mounted - cannot destroy bike");
                        return;
                    }
                }
            }
            DoLog("bike owner sleeping - destroying bike");
            tokill.Kill();
            storedData.bikeID.Remove(player.userID);
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
        if (player?.userID.IsSteamId() != true)
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
        public bool useFriends;
        public bool useClans;
        public bool useTeams;
        public bool bikeDecay;
        public bool allowDamage;
        public bool killOnSleep;
        public bool allowFuelIfUnlimited;
        public bool allowDriverDismountWhileFlying;
        public bool allowPassengerDismountWhileFlying;
        public bool debug;
        public bool StopEngineOnAttack;
        public bool FastStart;
        public float stdFuelConsumption;
        public float cooldownmin;
        public float mindistance;
        public float getdistance;
        public float startingFuel;
        public string Prefix; // Chat prefix
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
        public float getdistance;
    }

    public class ConfigData
    {
        public Global Global;
        public Dictionary<string, VIPSettings> VIPSettings { get; set; }
        public VersionNumber Version;
    }

    public void LoadConfig()
    {
        if (config.Exists())
        {
            configData = config.ReadObject<ConfigData>();
            if (configData.VIPSettings == null)
            {
                configData.VIPSettings = new Dictionary<string, VIPSettings>();
            }

            configData.Version = Version;
            return;
        }
        LoadDefaultConfig();
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
                bikeDecay = false,
                allowDamage = true,
                killOnSleep = false,
                allowFuelIfUnlimited = false,
                allowDriverDismountWhileFlying = true,
                allowPassengerDismountWhileFlying = true,
                stdFuelConsumption = 0.25f,
                cooldownmin = 60f,
                mindistance = 0f,
                getdistance = 0f,
                startingFuel = 0f,
                debug = false,
                Prefix = "[My Bicycle]: "
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
        // Save the data file as we add/remove bicycles.
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
}
