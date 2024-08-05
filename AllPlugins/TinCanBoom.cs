#region License (GPL v2)
/*
    TinCanBoom! Add Explosive to TinCanAlarm
    Copyright (c) 2024 RFC1920 <desolationoutpostpve@gmail.com>

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
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

[Info("TinCanBoom", "RFC1920", "1.0.2")]
[Description("Add explosives to TinCanAlarm")]
internal class TinCanBoom : RustScript
{
    ConfigData configData;
    private Dictionary<ulong, List<TinCanEnhanced>> playerAlarms = new Dictionary<ulong, List<TinCanEnhanced>>();
    public const string permUse = "tincanboom.use";

    private readonly List<string> orDefault = new List<string>();

    public class TinCanEnhanced
    {
        public string location;
        public NetworkableId alarm;
        public NetworkableId te;
    }

    public override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>()
            {
                { "off", "OFF" },
                { "on", "ON" },
                { "notauthorized", "You don't have permission to do that !!" },
                { "alarmtripped", "TinCanBoom tripped by {1} at {0}" },
                { "enabled", "TinCanBoom enabled" },
                { "disabled", "TinCanBoom disabled" }
            }, Name);
    }

    public void OnChatCommand(BasePlayer player, string command, string[] args = null)
    {
        if (player == null) return;
        switch (command)
        {
            case "ente":
                EnableDisable(player, command, args);
                break;
        }
    }

    private void EnableDisable(BasePlayer player, string command, string[] args)
    {
        if (!Permissions.UserHasPermission(permUse, player.UserIDString) && configData.Options.RequirePermission) { Message(player, "notauthorized"); return; }

        bool en = configData.Options.startEnabled;
        if (orDefault.Contains(player.UserIDString))
        {
            orDefault.Remove(player.UserIDString);
        }
        else
        {
            orDefault.Add(player.UserIDString);
            en = !en;
        }
        switch (en)
        {
            case true:
                Message(player, "enabled");
                break;
            case false:
                Message(player, "disabled");
                break;
        }
    }

    private void OnServerInitialized()
    {
        LoadConfigVariables();
        Permissions.RegisterPermission(Name, permUse);
        LoadData();
    }

    private void OnEntitySpawned(TinCanAlarm alarm)
    {
        BasePlayer player = FindPlayerByID(alarm.OwnerID);
        if (player == null) return;
        if (configData.Options.RequirePermission && !Permissions.UserHasPermission(permUse, player.UserIDString)) return;

        if (configData.Options.startEnabled && orDefault.Contains(player.UserIDString))
        {
            _DoLog("Plugin enabled by default, but player-disabled");
            return;
        }
        else if (!configData.Options.startEnabled && !orDefault.Contains(player.UserIDString))
        {
            _DoLog("Plugin disabled by default, and not player-enabled");
            return;
        }

        RFTimedExplosive exp = GameManager.server.CreateEntity("assets/prefabs/tools/c4/explosive.timed.deployed.prefab") as RFTimedExplosive;
        exp.enableSaving = false;
        exp.transform.localPosition = new Vector3(0f, 1f, 0f);
        exp.flags = 0;
        exp.SetFuse(float.PositiveInfinity);
        exp.timerAmountMin = float.PositiveInfinity;
        exp.timerAmountMax = float.PositiveInfinity;

        exp.SetParent(alarm);
        RemoveComps(exp);
        exp.Spawn();
        exp.stickEffect = null;
        Object.DestroyImmediate(exp.beepLoop);
        exp.beepLoop = null;
        exp.SetLimitedNetworking(true);
        exp.SendNetworkUpdateImmediate();

        SpawnRefresh(exp);
        if (!playerAlarms.ContainsKey(player.userID))
        {
            playerAlarms.Add(player.userID, new List<TinCanEnhanced>());
        }
        playerAlarms[player.userID].Add(new TinCanEnhanced()
        {
            location = alarm.transform.position.ToString(),
            alarm = alarm.net.ID,
            te = exp.net.ID
        });
        SaveData();
    }

    private void _DoLog(string message)
    {
        if (configData.Options.debug) DoLog(message);
    }

    private void OnTinCanAlarmTrigger(TinCanAlarm alarm)
    {
        if (alarm == null) return;
        RFTimedExplosive te = alarm.gameObject.GetComponentInChildren<RFTimedExplosive>();
        if (te != null)
        {
            te.SetLimitedNetworking(false);
            te.SetFuse(configData.Options.fireDelay);
            te.SetFlag(BaseEntity.Flags.On, true, false, false);

            FieldInfo lastTriggeredBy = typeof(TinCanAlarm).GetField("lastTriggerEntity", (BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
            object victimEnt = lastTriggeredBy.GetValue(alarm);
            string victim = "unknown";
            if (victimEnt is BasePlayer)
            {
                victim = (victimEnt as BasePlayer).displayName;
            }

            BasePlayer player = FindPlayerByID(alarm.OwnerID);
            if (configData.Options.NotifyOwner)
            {
                string pos = PositionToGrid(alarm.transform.position);
                Message(player, Lang("alarmtripped", null, pos, victim));
            }
            _DoLog($"Removing destroyed alarm from data for {player?.displayName}");

            playerAlarms[player.userID].RemoveAll(x => x.te == te.net.ID);
            SaveData();
        }
    }

    public void RemoveComps(BaseEntity obj)
    {
        UnityEngine.Object.DestroyImmediate(obj.GetComponent<DestroyOnGroundMissing>());
        UnityEngine.Object.DestroyImmediate(obj.GetComponent<GroundWatch>());
        foreach (MeshCollider mesh in obj.GetComponentsInChildren<MeshCollider>())
        {
            _DoLog($"Destroying MeshCollider for {obj.ShortPrefabName}");
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    private BasePlayer FindPlayerByID(ulong userid, bool includeSleepers = true)
    {
        foreach (BasePlayer activePlayer in BasePlayer.activePlayerList)
        {
            if (activePlayer.userID == userid)
            {
                return activePlayer;
            }
        }
        if (includeSleepers)
        {
            foreach (BasePlayer sleepingPlayer in BasePlayer.sleepingPlayerList)
            {
                if (sleepingPlayer.userID == userid)
                {
                    return sleepingPlayer;
                }
            }
        }
        return null;
    }

    private void SpawnRefresh(BaseEntity entity)
    {
        StabilityEntity hasstab = entity.GetComponent<StabilityEntity>();
        if (hasstab != null)
        {
            hasstab.grounded = true;
        }
        BaseMountable hasmount = entity.GetComponent<BaseMountable>();
        if (hasmount != null)
        {
            hasmount.isMobile = true;
        }
    }

    public string PositionToGrid(Vector3 position)
    {
        // From GrTeleport for display only
        Vector2 r = new Vector2((World.Size / 2) + position.x, (World.Size / 2) + position.z);
        float x = Mathf.Floor(r.x / 146.3f) % 26;
        float z = Mathf.Floor(World.Size / 146.3f) - Mathf.Floor(r.y / 146.3f);

        return $"{(char)('A' + x)}{z - 1}";
    }

    #region Data
    private void LoadData()
    {
        playerAlarms = data.ReadObject<Dictionary<ulong, List<TinCanEnhanced>>>(Name + "/playerAlarms");
    }

    private void SaveData()
    {
        data.WriteObject(Name + "/playerAlarms", playerAlarms);
    }
    #endregion Data

    #region config
    private void LoadConfigVariables()
    {
        configData = config.ReadObject<ConfigData>();

        configData.Version = Version;
        SaveConfig(configData);
    }

    public void LoadDefaultConfig()
    {
        DoLog("Creating new config file.");
        ConfigData config = new ConfigData
        {
            Options = new Options()
            {
                RequirePermission = true,
                startEnabled = false,
                NotifyOwner = false,
                fireDelay = 2f,
                debug = false
            },
            Version = Version
        };

        SaveConfig(config);
    }

    private void SaveConfig(ConfigData conf)
    {
        config.WriteObject(conf, true);
    }

    private class ConfigData
    {
        public Options Options;
        public VersionNumber Version;
    }

    public class Options
    {
        public bool RequirePermission;
        public bool startEnabled;
        public bool NotifyOwner;
        public float fireDelay;
        public bool debug;
    }
    #endregion
}
