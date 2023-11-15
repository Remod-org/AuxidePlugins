#region License (GPL v2)
/*
    DESCRIPTION
    Copyright (c) 2022 RFC1920 <desolationoutpostpve@gmail.com>

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License v2.

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
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class HNoDecay : RustScript
{
    public HNoDecay()
    {
        Author = "RFC1920";
        Description = "NoDecay for Auxide";
        Version = new VersionNumber(1, 0, 88);
    }
    private static ConfigData configData;
    private bool isenabled = true;

    #region main
    private Dictionary<string, long> lastConnected = new Dictionary<string, long>();
    private List<ulong> disabled = new List<ulong>();
    private Dictionary<string, List<string>> entityinfo = new Dictionary<string, List<string>>();

    private int targetLayer = LayerMask.GetMask("Construction", "Construction Trigger", "Trigger", "Deployed");
    private const string permNoDecayUse = "nodecay.use";
    private const string permNoDecayAdmin = "nodecay.admin";
    private const string TCOVR = "nodecay.overlay";

    //private readonly Plugin ZoneManager, GridAPI, JPipes;

    #region Message
//    private void LMessage(BasePlayer player, string key, params object[] args) => player.Reply(Lang(key, player.Id, args));

    public override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            ["ndisoff"] = "NoDecay has been turned OFF for your owned entities and buildings",
            ["ndison"] = "NoDecay has been turned ON for your owned entities and buildings",
            ["ndstatus"] = "NoDecay enabled set to {0}",
            ["nddebug"] = "Debug logging set to {0}",
            ["perm"] = "You have permission to use NoDecay.",
            ["noperm"] = "You DO NOT have permission to use NoDecay.",
            ["protby"] = "Protected by NoDecay",
            ["ndsettings"] = "NoDecay current settings:\n  Multipliers:"
        }, Name);
    }
    #endregion

    public void OnScriptUnloaded()
    {
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            CuiHelper.DestroyUi(player, TCOVR);
        }
    }

    public override void Initialize()
    {
        Permissions.RegisterPermission(Name, permNoDecayUse);
        Permissions.RegisterPermission(Name, permNoDecayAdmin);

        LoadConfig();
        LoadData();

        if (entityinfo.Count == 0) UpdateEnts();
        // Workaround for no decay on horses, even if set to decay here
        if (configData.multipliers["horse"] > 0)
        {
            float newdecaytime = (180f / configData.multipliers["horse"]) - 180f;

            foreach (RidableHorse horse in Resources.FindObjectsOfTypeAll<RidableHorse>())
            {
                if (horse.net == null) continue;
                if (horse.IsHitched() || horse.IsDestroyed) continue;

                if (newdecaytime > 0)
                {
                    DoLog($"Adding {Math.Floor(newdecaytime)} minutes of decay time to horse {horse.net.ID}, now {Math.Floor(180f + newdecaytime)} minutes", true);
                    horse.AddDecayDelay(newdecaytime);
                }
                else
                {
                    DoLog($"Subtracting {Math.Abs(Math.Floor(newdecaytime))} minutes of decay time from horse {horse.net.ID}, now {Math.Floor(180f + newdecaytime)} minutes", true);
                    //horse.nextDecayTime = Time.time + newdecaytime;
                    horse.AddDecayDelay(newdecaytime);
                }

                horse.SetDecayActive(true);
            }
        }
        isenabled = true;
    }

    public object CanLootEntity(BasePlayer player, StorageContainer container)
    {
        if (!configData.Global.disableLootWarning) return null;
        if (!Permissions.UserHasPermission(permNoDecayUse, player.UserIDString) && configData.Global.usePermission) return null;
        if (container == null) return null;
        var privs = container.GetComponentInParent<BuildingPrivlidge>();
        if (privs == null) return null;

        TcOverlay(player);//, privs);
        return null;
    }

    public void OnLootEntityEnd(BasePlayer player, BaseCombatEntity entity)
    {
        if (!configData.Global.disableLootWarning) return;
        if (!Permissions.UserHasPermission(permNoDecayUse, player.UserIDString) && configData.Global.usePermission) return;
        if (entity == null) return;
        if (entity.GetComponentInParent<BuildingPrivlidge>() == null) return;

        CuiHelper.DestroyUi(player, TCOVR);
    }

    private void TcOverlay(BasePlayer player)//, BaseEntity entity)
    {
        CuiHelper.DestroyUi(player, TCOVR);

        CuiElementContainer container = UI.Container(TCOVR, UI.Color("3E3C37", 1f), "0.651 0.5", "0.946 0.532", true, "Overlay");
        UI.Label(ref container, TCOVR, UI.Color("#cacaca", 1f), Lang("protby"), 14, "0 0", "1 1");

        CuiHelper.AddUi(player, container);
    }

    public object OnEntitySaved(BaseNetworkable entity, BaseNetworkable.SaveInfo saveInfo)
    {
        DoLog("ONENTITYSAVED CALLED");
        if (configData.Global.disableWarning)
        {
            if (!(entity is BuildingPrivlidge buildingPrivilege)) return null;
            if (configData.Global.usePermission)
            {
                string owner = buildingPrivilege.OwnerID.ToString();
                if (!Permissions.UserHasPermission(permNoDecayUse, owner) || owner == "0")
                {
                    if (owner != "0")
                    {
                        DoLog($"TC owner {owner} has NoDecay permission!");
                    }
                }
                else
                {
                    return null;
                }
            }
            if (disabled.Contains(buildingPrivilege.OwnerID))
            {
                DoLog($"TC owner {buildingPrivilege.OwnerID} has disabled NoDecay.");
                return null;
            }

            saveInfo.msg.buildingPrivilege.protectedMinutes = configData.Global.protectedDisplayTime;
            saveInfo.msg.buildingPrivilege.upkeepPeriodMinutes = configData.Global.protectedDisplayTime;
        }
        return null;
    }

    public void OnUserConnected(BasePlayer player) => OnUserDisconnected(player);

    public void OnUserDisconnected(BasePlayer player)
    {
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

    private void LoadData()
    {
        entityinfo = data.ReadObject<Dictionary<string, List<string>>>("entityinfo");
        lastConnected = data.ReadObject<Dictionary<string, long>>("lastconnected");
        disabled = data.ReadObject<List<ulong>>("disabled");
    }

    private void SaveData()
    {
        data.WriteObject("entityinfo", entityinfo);
        data.WriteObject("lastconnected", lastConnected);
        data.WriteObject("disabled", disabled);
    }

    public object OnTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
    {
        //DoLog(entity.name);
        if (!isenabled) return null;
        if (entity == null) return null;
        if (hitInfo == null) return null;
        if (hitInfo?.HitEntity == null) return null;
        if (hitInfo?.damageTypes?.GetMajorityDamageType() != Rust.DamageType.Decay) return null;

        float damageAmount = 0f;
        DateTime tick = DateTime.Now;
        string entity_name = entity?.LookupPrefab()?.name?.ToLower();
        //DoLog($"Decay Entity: {entity_name}");
        string owner = entity?.OwnerID.ToString();
        bool mundane = false;
        bool isBlock = false;

        if (configData.Global.usePermission)
        {
            if (Permissions.UserHasPermission(permNoDecayUse, owner) || owner == "0")
            {
                if (owner != "0")
                {
                    DoLog($"{entity_name} owner {owner} has NoDecay permission!");
                }
            }
            else
            {
                DoLog($"{entity_name} owner {owner} does NOT have NoDecay permission.  Standard decay in effect.");
                return null;
            }
            if (disabled.Contains(entity.OwnerID))
            {
                DoLog($"Entity owner {entity.OwnerID} has disabled NoDecay.");
                return null;
            }
        }
        if (configData.Global.protectedDays > 0 && entity.OwnerID > 0)
        {
            long lc;
            lastConnected.TryGetValue(entity.OwnerID.ToString(), out lc);
            if (lc > 0)
            {
                long now = ToEpochTime(DateTime.UtcNow);
                float days = Math.Abs((now - lc) / 86400);
                if (days > configData.Global.protectedDays)
                {
                    DoLog($"Allowing decay for owner offline for {configData.Global.protectedDays} days");
                    return null;
                }
                else
                {
                    DoLog($"Owner was last connected {days} days ago and is still protected...");
                }
            }
        }

        try
        {
            float before = hitInfo.damageTypes.Get(Rust.DamageType.Decay);

            if (entity is BuildingBlock)
            {
                damageAmount = ProcessBuildingDamage(entity, before);
                isBlock = true;
            }
            else if (entity is ModularCar && configData.Global.protectVehicleOnLift)
            {
                DoLog("Checking if car is on a lift.");
                ModularCarGarage garage = entity.GetComponentInParent<ModularCarGarage>();
                if (garage != null)
                {
                    DoLog("It is! Blocking damage.");
                    return null;
                }
            }
            else
            {
                // Main check for non-building entities/deployables
                foreach (KeyValuePair<string, List<string>> entity_type in entityinfo.Where(x => x.Value.Contains(entity_name)))
                {
                    if (entity_type.Key.Equals("vehicle") || entity_type.Key.Equals("boat") || entity_type.Key.Equals("balloon") || entity_type.Key.Equals("horse"))
                    {
                        mundane = true;
                    }
                    DoLog($"Found {entity_name} listed in {entity_type.Key}", mundane);
                    if (configData.multipliers.ContainsKey(entity_type.Key))
                    {
                        damageAmount = before * configData.multipliers[entity_type.Key];
                        break;
                    }
                }
            }

            // Check non-building entities for cupboard in range
            if (configData.Global.requireCupboard && configData.Global.cupboardCheckEntity && !isBlock)
            {
                // Verify that we should check for a cupboard and ensure that one exists.
                // If so, multiplier will be set to configData.multipliers['entityCupboard'].
                DoLog("NoDecay checking for local cupboard.", mundane);

                if (CheckCupboardEntity(entity, mundane))
                {
                    DoLog("Entity in range of cupboard.");
                    damageAmount = before * configData.multipliers["entityCupboard"];
                }
            }

            string pos = configData.Debug.logPosition ? $" ({entity.transform?.position.ToString()})" : "";
            bool destroy = configData.Global.DestroyOnZero;

            NextTick(() =>
            {
                DoLog($"Decay [{entity_name}{pos} - {entity?.net.ID}] before: {before} after: {damageAmount}, item health {entity?.health}", mundane);

                entity.health -= damageAmount;
                if (entity?.health == 0 && destroy)
                {
                    DoLog($"Entity {entity_name}{pos} completely decayed - destroying!", mundane);
                    if (entity != null && !entity.IsDestroyed)
                    {
                        UnityEngine.Object.Destroy(entity);
                        entity?.Kill(BaseNetworkable.DestroyMode.Gib);
                    }
                }
            });
            return true;
        }
        finally
        {
            double ms = (DateTime.Now - tick).TotalMilliseconds;
            if (ms > configData.Global.warningTime || configData.Debug.outputMundane) DoLog($"NoDecay.OnEntityTakeDamage on {entity_name} took {ms} ms to execute.");
        }
    }

    public void OnEntitySpawned(RidableHorse horse)
    {
        // Workaround for no decay on horses, even if set to decay here
        if (horse == null) return;
        if (horse.net == null) return;

        if (configData.multipliers["horse"] > 0)
        {
            float newdecaytime = (180f / configData.multipliers["horse"]) - 180f;
            if (newdecaytime > 0)
            {
                DoLog($"Adding {Math.Floor(newdecaytime)} minutes of decay time to horse {horse.net.ID}, now {Math.Floor(180f + newdecaytime)} minutes", true);
                horse.AddDecayDelay(newdecaytime);
            }
            else
            {
                DoLog($"Subtracting {Math.Abs(Math.Floor(newdecaytime))} minutes of decay time from horse {horse.net.ID}, now {Math.Floor(180f + newdecaytime)} minutes", true);
                horse.AddDecayDelay(newdecaytime);
            }
            horse.SetDecayActive(true);
        }
    }

    // Workaround for car chassis that won't die
    public void OnEntityDeath(ModularCar car, HitInfo hitinfo)
    {
        DoLog("Car died!  Checking for associated parts...");
        List<BaseEntity> ents = new List<BaseEntity>();
        Vis.Entities(car.transform.position, 1f, ents);
        foreach (BaseEntity ent in ents)
        {
            if (ent.name.Contains("module_car_spawned") && !ent.IsDestroyed)
            {
                DoLog($"Killing {ent.ShortPrefabName}");
                ent.Kill(BaseNetworkable.DestroyMode.Gib);
            }
        }
    }

    public void OnNewSave()
    {
        UpdateEnts();
    }

    private void UpdateEnts()
    {
        entityinfo["balloon"] = new List<string>();
        entityinfo["barricade"] = new List<string>();
        entityinfo["bbq"] = new List<string>();
        entityinfo["boat"] = new List<string>();
        entityinfo["box"] = new List<string>();
        entityinfo["building"] = new List<string>();
        entityinfo["campfire"] = new List<string>();
        entityinfo["deployables"] = new List<string>();
        entityinfo["furnace"] = new List<string>();
        entityinfo["horse"] = new List<string>();
        entityinfo["minicopter"] = new List<string>();
        entityinfo["sam"] = new List<string>();
        entityinfo["scrapcopter"] = new List<string>();
        entityinfo["sedan"] = new List<string>();
        entityinfo["trap"] = new List<string>();
        entityinfo["vehicle"] = new List<string>();
        entityinfo["watchtower"] = new List<string>();
        entityinfo["water"] = new List<string>();
        entityinfo["stonewall"] = new List<string>();
        entityinfo["woodwall"] = new List<string>();
        entityinfo["mining"] = new List<string>();

        List<string> names = new List<string>();
        foreach (BaseCombatEntity ent in Resources.FindObjectsOfTypeAll<BaseCombatEntity>())
        {
            string entity_name = ent.ShortPrefabName.ToLower();
            if (entity_name == "cupboard.tool.deployed") continue;
            if (entity_name == null) continue;
            if (names.Contains(entity_name)) continue; // Saves 20-30 seconds of processing time.
            names.Add(entity_name);
            //DoLog($"Checking {entity_name}");

            if (entity_name.Contains("campfire") || entity_name.Contains("skull_fire_pit"))
            {
                entityinfo["campfire"].Add(entity_name);
            }
            else if (entity_name.Contains("box") || entity_name.Contains("coffin"))
            {
                entityinfo["box"].Add(entity_name);
            }
            else if (entity_name.Contains("shutter") ||
                 (entity_name.Contains("door") && !entity_name.Contains("doorway")) ||
                 entity_name.Contains("hatch") || entity_name.Contains("garagedoor") ||
                 entity_name.Contains("bars") || entity_name.Contains("netting") ||
                 entity_name.Contains("cell") || entity_name.Contains("fence") ||
                 entity_name.Contains("reinforced") || entity_name.Contains("composter") ||
                 entity_name.Contains("workbench") || entity_name.Contains("shopfront") ||
                 entity_name.Contains("grill") || entity_name.Contains("wall.window.bars"))
            {
                entityinfo["building"].Add(entity_name);
            }
            else if (entity_name.Contains("deployed") || entity_name.Contains("speaker") ||
                entity_name.Contains("strobe") || entity_name.Contains("fog") ||
                entity_name.Contains("graveyard") || entity_name.Contains("candle") ||
                entity_name.Contains("hatchet") || entity_name.Contains("jackolantern"))
            {
                entityinfo["deployables"].Add(entity_name);
            }
            else if (entity_name.Contains("furnace"))
            {
                entityinfo["furnace"].Add(entity_name);
            }
            else if (entity_name.Contains("sedan"))
            {
                entityinfo["sedan"].Add(entity_name);
            }
            else if (entity_name.Contains("sam_static"))
            {
                entityinfo["sam"].Add(entity_name);
            }
            else if (entity_name.Contains("balloon"))
            {
                entityinfo["balloon"].Add(entity_name);
            }
            else if (entity_name.Contains("bbq"))
            {
                entityinfo["bbq"].Add(entity_name);
            }
            else if (entity_name.Contains("watchtower"))
            {
                entityinfo["watchtower"].Add(entity_name);
            }
            else if (entity_name.Contains("water_catcher") || entity_name.Equals("waterbarrel"))
            {
                entityinfo["water"].Add(entity_name);
            }
            else if (entity_name.Contains("beartrap") || entity_name.Contains("landmine") || entity_name.Contains("spikes.floor"))
            {
                entityinfo["trap"].Add(entity_name);
            }
            else if (entity_name.Contains("barricade"))
            {
                entityinfo["barricade"].Add(entity_name);
            }
            else if (entity_name.Contains("external.high.stone"))
            {
                entityinfo["stonewall"].Add(entity_name);
            }
            else if (entity_name.Contains("external.high.wood") || entity_name.Contains("external.high.ice") || entity_name.Contains("icewall"))
            {
                entityinfo["woodwall"].Add(entity_name);
            }
            else if (entity_name.Contains("mining"))
            {
                entityinfo["mining"].Add(entity_name);
            }
            else if (entity_name.Contains("rowboat") || entity_name.Contains("rhib") || entity_name.Contains("kayak") || entity_name.Contains("submarine"))
            {
                entityinfo["boat"].Add(entity_name);
            }
            else if (entity_name.Contains("minicopter"))
            {
                entityinfo["minicopter"].Add(entity_name);
            }
            else if (entity_name.Contains("horse"))
            {
                entityinfo["horse"].Add(entity_name);
            }
            else if (entity_name.Contains("scraptransport"))
            {
                entityinfo["scrapcopter"].Add(entity_name);
            }
            else if (entity_name.Contains("vehicle") ||
                    entity_name.Contains("chassis_") ||
                    entity_name.Contains("1module_") ||
                    entity_name.Contains("2module_") ||
                    entity_name.Contains("3module_") ||
                    entity_name.Contains("snowmobile") ||
                    entity_name.Contains("4module_"))
            {
                entityinfo["vehicle"].Add(entity_name);
            }
        }
        SaveData();
    }

    private float ProcessBuildingDamage(BaseEntity entity, float before)
    {
        BuildingBlock block = entity as BuildingBlock;
        float multiplier = 1.0f;
        float damageAmount = 1.0f;
        bool isHighWall = block.LookupPrefab().name.Contains("wall.external");
        bool isHighGate = block.LookupPrefab().name.Contains("gates.external");

        string type = null;
        bool hascup = true; // Assume true (has cupboard or we don't require one)

        DoLog($"NoDecay checking for block damage to {block.LookupPrefab().name}");

        // Verify that we should check for a cupboard and ensure that one exists.
        // If not, multiplier will be standard of 1.0f (hascup true).
        if (configData.Global.requireCupboard)
        {
            DoLog("NoDecay checking for local cupboard.");
            hascup = CheckCupboardBlock(block, entity.LookupPrefab().name, block.grade.ToString().ToLower());
        }
        else
        {
            DoLog("NoDecay not checking for local cupboard.");
        }

        switch (block.grade)
        {
            case BuildingGrade.Enum.Twigs:
                if (hascup) multiplier = configData.multipliers["twig"];
                type = "twig";
                break;
            case BuildingGrade.Enum.Wood:
                if (isHighWall)
                {
                    if (hascup) multiplier = configData.multipliers["highWoodWall"];
                    type = "high wood wall";
                }
                else if (isHighGate)
                {
                    if (hascup) multiplier = configData.multipliers["highWoodWall"];
                    type = "high wood gate";
                }
                else
                {
                    if (hascup) multiplier = configData.multipliers["wood"];
                    type = "wood";
                }
                break;
            case BuildingGrade.Enum.Stone:
                if (isHighWall)
                {
                    if (hascup) multiplier = configData.multipliers["highStoneWall"];
                    type = "high stone wall";
                }
                else if (isHighGate)
                {
                    if (hascup) multiplier = configData.multipliers["highStoneWall"];
                    type = "high stone gate";
                }
                else
                {
                    if (hascup) multiplier = configData.multipliers["stone"];
                    type = "stone";
                }
                break;
            case BuildingGrade.Enum.Metal:
                if (hascup) multiplier = configData.multipliers["sheet"];
                type = "sheet";
                break;
            case BuildingGrade.Enum.TopTier:
                if (hascup) multiplier = configData.multipliers["armored"];
                type = "armored";
                break;
            default:
                DoLog($"Decay ({type}) has unknown grade type.");
                type = "unknown";
                break;
        }

        damageAmount = before * multiplier;

        DoLog($"Decay ({type}) before: {before} after: {damageAmount}");
        return damageAmount;
    }

    public BuildingPrivlidge GetBuildingPrivilege(BuildingManager.Building building, BuildingBlock block = null)
    {
        BuildingPrivlidge buildingPrivlidge = null;
        if (building.HasBuildingPrivileges())
        {
            for (int i = 0; i < building.buildingPrivileges.Count; i++)
            {
                BuildingPrivlidge item = building.buildingPrivileges[i];
                if (!(item == null) && item.IsOlderThan(buildingPrivlidge))
                {
                    DoLog("CheckCupboardBlock:     Found block connected to cupboard!");
                    buildingPrivlidge = item;
                }
            }
        }
        else if (configData.Global.cupboardRange > 0)
        {
            // Disconnected building with no TC, but possibly in cupboard range
            List<BuildingPrivlidge> cups = new List<BuildingPrivlidge>();
            Vis.Entities(block.transform.position, configData.Global.cupboardRange, cups, targetLayer);
            foreach (BuildingPrivlidge cup in cups)
            {
                foreach (ProtoBuf.PlayerNameID p in cup.authorizedPlayers.ToArray())
                {
                    if (p.userid == block.OwnerID)
                    {
                        DoLog("CheckCupboardBlock:     Found block in range of cupboard!");
                        return cup;
                    }
                }
            }
        }
        return buildingPrivlidge;
    }

    // Check that a building block is owned by/attached to a cupboard
    private bool CheckCupboardBlock(BuildingBlock block, string ename = "unknown", string grade = "")
    {
        BuildingManager.Building building = block.GetBuilding();
        DoLog($"CheckCupboardBlock:   Checking for cupboard connected to {grade} {ename}.");

        if (building != null)
        {
            // cupboard overlap.  Block safe from decay.
            //if (building.GetDominatingBuildingPrivilege() == null)
            if (GetBuildingPrivilege(building, block) == null)
            {
                DoLog("CheckCupboardBlock:     Block NOT owned by cupboard!");
                return false;
            }

            DoLog("CheckCupboardBlock:     Block owned by cupboard!");
            return true;
        }
        else
        {
            DoLog("CheckCupboardBlock:     Unable to find cupboard.");
            return false;
        }
    }

    // Non-block entity check
    private bool CheckCupboardEntity(BaseEntity entity, bool mundane = false)
    {
        if (configData.Global.useCupboardRange)
        {
            // This is the old way using cupboard distance instead of BP.  It's less efficient but some may have made use of this range concept, so here it is.
            List<BuildingPrivlidge> cups = new List<BuildingPrivlidge>();
            Vis.Entities(entity.transform.position, configData.Global.cupboardRange, cups, targetLayer);

            DoLog($"CheckCupboardEntity:   Checking for cupboard within {configData.Global.cupboardRange}m of {entity.ShortPrefabName}.", mundane);

            if (cups.Count > 0)
            {
                // cupboard overlap.  Entity safe from decay.
                DoLog("CheckCupboardEntity:     Found entity layer in range of cupboard!", mundane);
                return true;
            }

            DoLog("CheckCupboardEntity:     Unable to find entity layer in range of cupboard.", mundane);
            return false;
        }

        // New method of simply checking for the entity's building privilege.
        DoLog($"CheckCupboardEntity:   Checking for building privilege for {entity.ShortPrefabName}.", mundane);
        BuildingPrivlidge tc = entity.GetBuildingPrivilege();

        if (tc != null)
        {
            // cupboard overlap.  Entity safe from decay.
            DoLog("CheckCupboardEntity:     Found entity layer in range of cupboard!", mundane);
            return true;
        }

        DoLog("CheckCupboardEntity:     Unable to find entity layer in range of cupboard.", mundane);
        return false;
    }

    // Prevent players from adding building resources to cupboard if so configured
    //private object CanMoveItem(Item item, PlayerInventory inventory, uint targetContainer, int targetSlot)
    public ItemContainer.CanAcceptResult? CanAcceptItem(ItemContainer container, Item item, int targetPos)
    {
        BaseEntity cup = container?.entityOwner;
        if (cup == null) return null;
        if (!(cup is BuildingPrivlidge)) return null;

        if (!(configData.Global.blockCupboardResources || configData.Global.blockCupboardWood)) return null;

        string res = item?.info?.shortname;
        DoLog($"Player trying to add {res} to a cupboard!");
        if (res.Equals("wood") && configData.Global.blockCupboardWood)
        {
            DoLog($"Player blocked from adding {res} to a cupboard!");
            return ItemContainer.CanAcceptResult.CannotAcceptRightNow;
        }
        else if (configData.Global.blockCupboardResources)
        {
            if (
                (res.Equals("stones") && configData.Global.blockCupboardStone)
                || (res.Equals("metal.fragments") && configData.Global.blockCupboardMetal)
                || (res.Equals("metal.refined") && configData.Global.blockCupboardArmor))
            {
                DoLog($"Player blocked from adding {res} to a cupboard!");
                return ItemContainer.CanAcceptResult.CannotAcceptRightNow;
            }
        }

        return null;
    }
    #endregion

    #region command
    public void OnChatCommand(BasePlayer player, string command, string[] args = null)
    {
        switch (command)
        {
            case "nodecay":
                CmdInfo(player, command, args);
                break;
        }
    }

    private void CmdInfo(BasePlayer player, string command, string[] args)
    {
        if (Permissions.UserHasPermission(permNoDecayAdmin, player.UserIDString) && args.Length > 0)
        {
            switch (args[0])
            {
                case "enable":
                    isenabled = !isenabled;
                    Message(player, "ndstatus", isenabled.ToString());
                    SaveConfig(configData);
                    return;
                case "log":
                    configData.Debug.outputToRcon = !configData.Debug.outputToRcon;
                    Message(player, "nddebug", configData.Debug.outputToRcon.ToString());
                    return;
                case "update":
                    UpdateEnts();
                    return;
                case "info":
                    string info = Lang("ndsettings");
                    info += "\n\tarmored: " + configData.multipliers["armored"]; ToString();
                    info += "\n\tballoon: " + configData.multipliers["balloon"]; ToString();
                    info += "\n\tbarricade: " + configData.multipliers["barricade"]; ToString();
                    info += "\n\tbbq: " + configData.multipliers["bbq"]; ToString();
                    info += "\n\tboat: " + configData.multipliers["boat"]; ToString();
                    info += "\n\tbox: " + configData.multipliers["box"]; ToString();
                    info += "\n\tcampfire" + configData.multipliers["campfire"]; ToString();
                    info += "\n\tdeployables: " + configData.multipliers["deployables"]; ToString();
                    info += "\n\tentityCupboard: " + configData.multipliers["entityCupboard"]; ToString();
                    info += "\n\tfurnace: " + configData.multipliers["furnace"]; ToString();
                    info += "\n\thighWoodWall: " + configData.multipliers["highWoodWall"]; ToString();
                    info += "\n\thighStoneWall: " + configData.multipliers["highStoneWall"]; ToString();
                    info += "\n\thorse: " + configData.multipliers["horse"]; ToString();
                    info += "\n\tminicopter: " + configData.multipliers["minicopter"]; ToString();
                    info += "\n\tsam: " + configData.multipliers["sam"]; ToString();
                    info += "\n\tscrapcopter: " + configData.multipliers["scrapcopter"]; ToString();
                    info += "\n\tsedan: " + configData.multipliers["sedan"]; ToString();
                    info += "\n\tsheet: " + configData.multipliers["sheet"]; ToString();
                    info += "\n\tstone: " + configData.multipliers["stone"]; ToString();
                    info += "\n\ttrap: " + configData.multipliers["trap"]; ToString();
                    info += "\n\ttwig: " + configData.multipliers["twig"]; ToString();
                    info += "\n\tvehicle: " + configData.multipliers["vehicle"]; ToString();
                    info += "\n\twatchtower: " + configData.multipliers["watchtower"]; ToString();
                    info += "\n\twater: " + configData.multipliers["water"]; ToString();
                    info += "\n\twood: " + configData.multipliers["wood"]; ToString();

                    info += "\n\n\tEnabled: " + isenabled.ToString();
                    info += "\n\tdisableWarning: " + configData.Global.disableWarning.ToString();
                    info += "\n\tprotectedDays: " + configData.Global.protectedDays.ToString();
                    info += "\n\tprotectVehicleOnLift: " + configData.Global.protectVehicleOnLift.ToString();
                    info += "\n\tusePermission: " + configData.Global.usePermission.ToString();
                    info += "\n\trequireCupboard: " + configData.Global.requireCupboard.ToString();
                    info += "\n\tCupboardEntity: " + configData.Global.cupboardCheckEntity.ToString();
                    info += "\n\tcupboardRange: " + configData.Global.cupboardRange.ToString();
                    info += "\n\tblockCupboardResources: " + configData.Global.blockCupboardResources.ToString();
                    info += "\n\tblockCupboardWood: " + configData.Global.blockCupboardWood.ToString();
                    info += "\n\tblockCupboardStone: " + configData.Global.blockCupboardStone.ToString();
                    info += "\n\tblockCupboardMetal: " + configData.Global.blockCupboardMetal.ToString();
                    info += "\n\tblockCupboardArmor: " + configData.Global.blockCupboardArmor.ToString();

                    Message(player, info);
                    info = null;
                    return;
            }
        }
        if (player.UserIDString == "server_console") return;
        if (args.Length > 0)
        {
            bool save = false;
            ulong id = ulong.Parse(player.UserIDString);
            switch (args[0])
            {
                case "off":
                    if (!disabled.Contains(id))
                    {
                        save = true;
                        disabled.Add(id);
                    }
                    Message(player, "ndstatus", isenabled.ToString());
                    if (Permissions.UserHasPermission(permNoDecayUse, player.UserIDString))
                    {
                        Message(player, "perm");
                    }
                    else
                    {
                        Message(player, "noperm");
                    }
                    Message(player, "ndisoff");
                    break;
                case "on":
                    if (disabled.Contains(id))
                    {
                        save = true;
                        disabled.Remove(id);
                    }
                    Message(player, "ndstatus", isenabled.ToString());
                    if (Permissions.UserHasPermission(permNoDecayUse, player.UserIDString))
                    {
                        Message(player, "perm");
                    }
                    else
                    {
                        Message(player, "noperm");
                    }
                    Message(player, "ndison");
                    break;
                case "?":
                    Message(player, "ndstatus", isenabled.ToString());
                    if (Permissions.UserHasPermission(permNoDecayUse, player.UserIDString))
                    {
                        Message(player, "perm");
                    }
                    else
                    {
                        Message(player, "noperm");
                    }
                    if (disabled.Contains(id))
                    {
                        Message(player, "ndisoff");
                    }
                    else
                    {
                        Message(player, "ndison");
                    }
                    break;
            }
            if (save) SaveData();
        }
    }
    #endregion

    #region inbound_hooks
    // Returns player status if playerid > 0
    // Returns global enabled status if playerid == 0
    private bool NoDecayGet(ulong playerid = 0)
    {
        if (playerid > 0)
        {
            return !disabled.Contains(playerid);
        }

        return isenabled;
    }

    // Sets player status if playerid > 0
    // Sets global status if playerid == 0
    private object NoDecaySet(ulong playerid = 0, bool status = true)
    {
        if (playerid > 0)
        {
            if (status)
            {
                if (disabled.Contains(playerid))
                {
                    disabled.Remove(playerid);
                }
            }
            else
            {
                if (!disabled.Contains(playerid))
                {
                    disabled.Add(playerid);
                }
            }
            SaveData();
            return null;
        }
        else
        {
            isenabled = status;
            SaveConfig(configData);
        }

        return null;
    }

    private void DisableMe()
    {
        if (!configData.Global.respondToActivationHooks) return;
        isenabled = false;
        DoLog($"{Name} disabled");
    }
    private void EnableMe()
    {
        if (!configData.Global.respondToActivationHooks) return;
        isenabled = true;
        DoLog($"{Name} enabled");
    }
    #endregion

    #region helpers
    // From PlayerDatabase
    private long ToEpochTime(DateTime dateTime)
    {
        DateTime date = dateTime.ToUniversalTime();
        long ticks = date.Ticks - new DateTime(1970, 1, 1, 0, 0, 0, 0).Ticks;
        return ticks / TimeSpan.TicksPerSecond;
    }

    public string PositionToGrid(Vector3 position)
    {
        // From GrTeleport for display only
        Vector2 r = new Vector2((World.Size / 2) + position.x, (World.Size / 2) + position.z);
        float x = Mathf.Floor(r.x / 146.3f) % 26;
        float z = Mathf.Floor(World.Size / 146.3f) - Mathf.Floor(r.y / 146.3f);

        return $"{(char)('A' + x)}{z - 1}";
    }

    // Just here to cleanup the code a bit
    private void DoLog(string message, bool mundane = false)
    {
        if (configData.Debug.outputToRcon)
        {
            if (!mundane) DoLog($"{message}");
            else if (mundane && configData.Debug.outputMundane) DoLog($"{message}");
        }
    }
    #endregion

    #region config
    public class ConfigData
    {
        public Debug Debug;
        public Global Global;
        public SortedDictionary<string, float> multipliers;
        public VersionNumber Version;
    }

    public class Debug
    {
        public bool outputToRcon;
        public bool outputMundane;
        public bool logPosition;
    }

    public class Global
    {
        public bool usePermission;
        public bool requireCupboard;
        public bool cupboardCheckEntity;
        public float protectedDays;
        public float cupboardRange;
        public bool useCupboardRange;
        public bool DestroyOnZero;
        public bool useJPipes;
        public bool honorZoneManagerFlag;
        public bool blockCupboardResources;
        public bool blockCupboardWood;
        public bool blockCupboardStone;
        public bool blockCupboardMetal;
        public bool blockCupboardArmor;
        public bool disableWarning;
        public bool disableLootWarning;
        public bool protectVehicleOnLift;
        public float protectedDisplayTime;
        public double warningTime;
        public List<string> overrideZoneManager = new List<string>();
        public bool respondToActivationHooks;
    }

    public void LoadDefaultConfig()
    {
        configData = new ConfigData
        {
            Debug = new Debug(),
            Global = new Global()
            {
                protectedDays = 0,
                cupboardRange = 30f,
                DestroyOnZero = true,
                disableWarning = true,
                protectVehicleOnLift = true,
                protectedDisplayTime = 44000,
                warningTime = 10,
                overrideZoneManager = new List<string>() { "vehicle", "balloon" },
                respondToActivationHooks = false
            },
            multipliers = new SortedDictionary<string, float>()
            {
                { "armored", 0f },
                { "balloon", 0f },
                { "barricade", 0f },
                { "bbq", 0f },
                { "boat", 0f },
                { "box", 0f },
                { "building", 0f },
                { "campfire", 0f },
                { "entityCupboard", 0f },
                { "furnace", 0f },
                { "highWoodWall", 0f },
                { "highStoneWall", 0f },
                { "horse", 0f },
                { "minicopter", 0f },
                { "mining", 0f },
                { "sam", 0f },
                { "scrapcopter", 0f },
                { "sedan", 0f },
                { "sheet", 0f },
                { "stone", 0f },
                { "twig", 1.0f },
                { "trap", 0f },
                { "vehicle", 0f },
                { "watchtower", 0f },
                { "water", 0f },
                { "wood", 0f },
                { "deployables", 0.1f } // For all others not listed
            },
            Version = Version
        };
        SaveConfig(configData);
    }

    private void LoadConfig()
    {
        if (config.Exists())
        {
            configData = config.ReadObject<ConfigData>();
            return;
        }
        LoadDefaultConfig();
    }

    private void SaveConfig(ConfigData configuration)
    {
        config.WriteObject(configuration, true);
    }
    #endregion
}
