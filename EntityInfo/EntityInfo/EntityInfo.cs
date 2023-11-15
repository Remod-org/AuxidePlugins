using Facepunch;
using System;
using System.Collections.Generic;
using UnityEngine;

[Info("EntityInfo", "RFC1920", "1.0.1")]
[Description("A bastardized version of EntityOwner by Calytic that we can use in Auxide (and therefore staging).")]
internal class EntityInfo : RustScript
{
    private static ConfigData configData;
    private readonly int layerMasks = LayerMask.GetMask("Construction", "Construction Trigger", "Trigger", "Deployed");

    public override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            ["Denied: Permission"] = "You are not allowed to use this command",
            ["Target: None"] = "No target found",
            ["Target: Owner"] = "Owner: {0}",
            ["Target: Limit"] = "Exceeded entity limit.",
            ["Syntax: Owner"] = "Invalid syntax: /owner",
            ["Syntax: Own"] = "Invalid Syntax. \n/own type player\nTypes: all/block/storage/cupboard/sign/sleepingbag/plant/oven/door/turret\n/own player",
            ["Syntax: Unown"] = "Invalid Syntax. \n/unown type player\nTypes: all/block/storage/cupboard/sign/sleepingbag/plant/oven/door/turret\n/unown player",
            ["Syntax: Prod2"] = "Invalid Syntax. \n/prod2 type \nTypes:\n all/block/entity/storage/cupboard/sign/sleepingbag/plant/oven/door/turret",
            ["Syntax: Auth"] = "Invalid Syntax. \n/auth turret player\n/auth cupboard player/auth player\n/auth",
            ["Syntax: Deauth"] = "Invalid Syntax. \n/deauth turret player\n/deauth cupboard player/deauth player\n/deauth",
            ["Ownership: Changing"] = "Changing ownership..",
            ["Ownership: Removing"] = "Removing ownership..",
            ["Ownership: New"] = "New owner of all around is: {0}",
            ["Ownership: New Self"] = "Owner: You were given ownership of this house and nearby deployables",
            ["Ownership: Count"] = "Count ({0})",
            ["Ownership: Removed"] = "Ownership removed",
            ["Ownership: Changed"] = "Ownership changed",
            ["Entities: None"] = "No entities found.",
            ["Entities: Authorized"] = "({0}) Authorized",
            ["Entities: Count"] = "Counted {0} entities ({1}/{2})",
            ["Structure: Prodding"] = "Prodding structure..",
            ["Structure: Condition Percent"] = "Condition: {0}%",
            ["Player: Unknown Percent"] = "Unknown: {0}%",
            ["Player: None"] = "Target player not found",
            ["Cupboards: Prodding"] = "Prodding cupboards..",
            ["Cupboards: Authorizing"] = "Authorizing cupboards..",
            ["Cupboards: Authorized"] = "Authorized {0} on {1} cupboards",
            ["Cupboards: Deauthorizing"] = "Deauthorizing cupboards..",
            ["Cupboard: Deauthorized"] = "Deauthorized {0} on {1} cupboards",
            ["Turrets: Authorized"] = "Authorized {0} on {1} turrets",
            ["Turrets: Authorizing"] = "Authorizing turrets..",
            ["Turrets: Prodding"] = "Prodding turrets..",
            ["Turrets: Deauthorized"] = "Deauthorized {0} on {1} turrets",
            ["Turrets: Deauthorizing"] = "Deauthorizing turrets..",
            ["Lock: Code"] = "Code: {0}",
            ["Lock: Codes"] = "Codes: {0}, guest {1}"
        }, Name);
    }

    public override void Initialize()
    {
        LoadConfig();
    }

    public class ConfigData
    {
        public bool Enable;
    }

    public void SaveConfig(ConfigData configuration)
    {
        config.WriteObject(configuration);
    }

    public void LoadConfig()
    {
        if (config.Exists())
        {
            configData = config.ReadObject<ConfigData>();
            return;
        }
        LoadDefaultConfig();
    }

    public void LoadDefaultConfig()
    {
        configData = new ConfigData()
        {
            Enable = false
        };
        SaveConfig(configData);
    }

    public object OnHammerHit(BasePlayer player, HitInfo hit)
    {
        if (!canCheckOwners(player))
        {
            Message(player, Lang("Denied: Permission"));
            return null;
        }
        player.SendConsoleCommand("prod");
        return null;
    }

    public void OnChatCommand(BasePlayer player, string command, string[] args = null)
    {
        switch (command)
        {
            case "prod":
                cmdProd(player, command, args);
                break;
        }
    }

    [Command("prod")]
    private void cmdProd(BasePlayer player, string command, string[] args)
    {
        if (!canCheckOwners(player))
        {
            Message(player, "Denied: Permission");
            return;
        }
        if (args == null || args.Length == 0)
        {
            object target = RaycastAll<BaseEntity>(player.eyes.HeadRay());
            if (target is bool)
            {
                Message(player, "Target: None");
                return;
            }
            if (target is BaseEntity)
            {
                BaseEntity targetEntity = target as BaseEntity;
                string owner = GetOwnerName((BaseEntity)target);
                if (string.IsNullOrEmpty(owner))
                {
                    owner = "N/A";
                }

                string msg = Lang("Target: Owner", player, owner);

                if (canSeeDetails(player))
                {
                    BasePlayer pl = targetEntity as BasePlayer;
                    if (pl != null)
                    {
                        msg += "\n<color=#D3D3D3>Name: " + pl.displayName + "</color>";
                        msg += "\n<color=#D3D3D3>UserID: " + pl.userID.ToString() + "</color>";
                        msg += "\n<color=#D3D3D3>Health: " + pl.health.ToString() + "</color>";
                    }
                    else
                    {
                        msg += "\n<color=#D3D3D3>Name: " + targetEntity.ShortPrefabName + "</color>";
                    }
                    if (targetEntity is RemoteControlEntity)
                    {
                        msg += "\n<color=#D3D3D3>RC Name: " + (targetEntity as RemoteControlEntity)?.rcIdentifier + "</color>";
                    }
                    msg += "\n<color=#D3D3D3> Net ID: " + targetEntity.net.ID.ToString() + "</color>";
                    if (targetEntity.skinID > 0)
                    {
                        msg += "\n<color=#D3D3D3>Skin: " + targetEntity.skinID + "</color>";
                    }

                    if (targetEntity.PrefabName != targetEntity.ShortPrefabName)
                    {
                        msg += "\n<color=#D3D3D3>Prefab: \"" + targetEntity.PrefabName + "\"</color>";
                        msg += "\n<color=#D3D3D3>ShortPrefab: \"" + targetEntity.ShortPrefabName + "\"</color>";
                    }
                    msg += "\n<color=#D3D3D3>Outside: " + (targetEntity.IsOutside() ? "Yes" : "No") + "</color>";

                    Component[] comps = targetEntity.GetComponents(typeof(Component));
                    BaseEntity parent = BaseNetworkable.serverEntities.Find(targetEntity.parentEntity.uid) as BaseEntity;
                    Component[] child = targetEntity.GetComponentsInChildren(typeof(Component));

                    if (parent != null)
                    {
                        msg += "\n<color=#D3D3D3>Parent:</color>";
                        msg += "\n\t" + parent.ShortPrefabName + "</color>";
                    }
                    if (comps.Length > 0)
                    {
                        msg += "\n<color=#D3D3D3>Components:</color>";
                        foreach (Component x in comps)
                        {
                            msg += "\n\t" + x.GetType().ToString();// + "</color>";
                            if (x.GetType().ToString().Contains("Rigidbody"))
                            {
                                var rb = x as Rigidbody;
                                if (rb != null)
                                {
                                    msg += "\n\t\tDC=" + rb.detectCollisions.ToString();
                                    msg += "\n\t\tCDmode=" + rb.collisionDetectionMode.ToString();
                                    msg += "\n\t\tgravity=" + rb.useGravity.ToString();
                                }
                            }
                            else if (x.GetType().ToString().Contains("BuildingBlock"))
                            {
                                var bb = x as BuildingBlock;
                                if (bb != null)
                                {
                                    var s = bb.transform.GetBounds().extents.x;
                                    msg += "\n\t\twidth=" + s.ToString();
                                    msg += "\n\t\thealth=" + bb.health.ToString();
                                }
                            }
                            msg += "</color>";
                        }
                    }
                    if (child.Length > 0)
                    {
                        msg += "\n<color=#D3D3D3>Children:</color>";
                        foreach (Component x in child)
                        {
                            msg += "\n\t" + x.GetType().ToString() + "</color>";
                        }
                    }
                }

                if (canCheckCodes(player))
                {
                    BaseEntity baseLock = targetEntity.GetSlot(BaseEntity.Slot.Lock);
                    if (baseLock is CodeLock)
                    {
                        CodeLockExtension codeLock = baseLock as CodeLockExtension;
                        string keyCode = codeLock.code;
                        string guestCode = codeLock.guestCode;
                        msg += "\n" + string.Format("Lock: Codes", keyCode, guestCode);
                    }
                }

                Message(player, msg);
            }
        }
        else
        {
            Message(player, "Syntax: Owner");
        }
    }

    #region Permission Checks
    private bool canCheckOwners(BasePlayer player)
    {
        if (player == null)
        {
            return false;
        }

        if (player.net.connection.authLevel > 0)
        {
            return true;
        }

        return Permissions.UserHasPermission("entityowner.cancheckowners", player.UserIDString);
    }

    private bool canCheckCodes(BasePlayer player)
    {
        if (player == null)
        {
            return false;
        }

        if (player.net.connection.authLevel > 0)
        {
            return true;
        }

        return Permissions.UserHasPermission("entityowner.cancheckcodes", player.UserIDString);
    }

    private bool canSeeDetails(BasePlayer player)
    {
        if (player == null)
        {
            return false;
        }

        if (player.net.connection.authLevel > 0)
        {
            return true;
        }

        return Permissions.UserHasPermission("entityowner.seedetails", player.UserIDString);
    }
    private bool canChangeOwners(BasePlayer player)
    {
        if (player == null)
        {
            return false;
        }

        if (player.net.connection.authLevel > 0)
        {
            return true;
        }

        return Permissions.UserHasPermission("entityowner.canchangeowners", player.UserIDString);
    }
    #endregion

    #region Utility Methods
    private object RaycastAll<T>(Vector3 position, Vector3 aim) where T : BaseEntity
    {
        RaycastHit[] hits = Physics.RaycastAll(position, aim);
        GamePhysics.Sort(hits);
        const float distance = 100f;
        object target = false;
        foreach (RaycastHit hit in hits)
        {
            BaseEntity ent = hit.GetEntity();
            if (ent is T && hit.distance < distance)
            {
                target = ent;
                break;
            }
        }

        return target;
    }

    private object RaycastAll<T>(Ray ray) where T : BaseEntity
    {
        RaycastHit[] hits = Physics.RaycastAll(ray);
        GamePhysics.Sort(hits);
        const float distance = 100f;
        object target = false;
        foreach (RaycastHit hit in hits)
        {
            BaseEntity ent = hit.GetEntity();
            if (ent is T && hit.distance < distance)
            {
                target = ent;
                break;
            }
        }

        return target;
    }

    private object FindBuilding(Vector3 position, float distance = 3f)
    {
        BuildingBlock hit = FindEntity<BuildingBlock>(position, distance);

        return hit ?? (object)false;
    }

    private object FindEntity(Vector3 position, float distance = 3f, params string[] filter)
    {
        BaseEntity hit = FindEntity<BaseEntity>(position, distance, filter);

        return hit ?? (object)false;
    }

    private T FindEntity<T>(Vector3 position, float distance = 3f, params string[] filter) where T : BaseEntity
    {
        List<T> list = Pool.GetList<T>();
        Vis.Entities(position, distance, list, layerMasks);

        if (list.Count > 0)
        {
            foreach (T e in list)
            {
                if (filter.Length > 0)
                {
                    foreach (string f in filter)
                    {
                        if (e.name.Contains(f))
                        {
                            return e;
                        }
                    }
                }
                else
                {
                    return e;
                }
            }
            Pool.FreeList(ref list);
        }

        return null;
    }

    private List<T> FindEntities<T>(Vector3 position, float distance = 3f) where T : BaseEntity
    {
        List<T> list = Pool.GetList<T>();
        Vis.Entities(position, distance, list, layerMasks);
        return list;
    }

    private List<BuildingBlock> GetProfileConstructions(BasePlayer player)
    {
        List<BuildingBlock> result = new List<BuildingBlock>();
        foreach (BuildingBlock block in UnityEngine.Object.FindObjectsOfType<BuildingBlock>())
        {
            if (block.OwnerID == player.userID)
            {
                result.Add(block);
            }
        }

        return result;
    }

    private List<BaseEntity> GetProfileDeployables(BasePlayer player)
    {
        List<BaseEntity> result = new List<BaseEntity>();
        foreach (BaseEntity entity in UnityEngine.Object.FindObjectsOfType<BaseEntity>())
        {
            if (entity.OwnerID == player.userID && !(entity is BuildingBlock))
            {
                result.Add(entity);
            }
        }

        return result;
    }

    private void ClearProfile(BasePlayer player)
    {
        foreach (BaseEntity entity in UnityEngine.Object.FindObjectsOfType<BaseEntity>())
        {
            if (entity.OwnerID == player.userID && !(entity is BuildingBlock))
            {
                RemoveOwner(entity);
            }
        }
    }

    private string FindPlayerName(ulong playerID)
    {
        if (playerID > 76560000000000000L)
        {
            BasePlayer player = FindPlayerByPartialName(playerID.ToString());
            if (player)
            {
                if (player.IsSleeping())
                {
                    return $"{player.displayName} [<color=#ADD8E6>Sleeping</color>]";
                }

                return $"{player.displayName} [<color=#32CD32>Online</color>]";
            }

            BasePlayer p = BasePlayer.Find(playerID.ToString());
            if (p != null)
            {
                return $"{p.displayName} [<color=#FF0000>Offline</color>]";
            }
        }

        return $"Unknown: {playerID}";
    }

    private ulong FindUserIDByPartialName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return 0;
        }

        ulong userID;
        if (ulong.TryParse(name, out userID))
        {
            return userID;
        }

        BasePlayer player = BasePlayer.Find(name);

        if (player != null)
        {
            return Convert.ToUInt64(player.userID);
        }

        return 0;
    }

    private BasePlayer FindPlayerByPartialName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        BasePlayer player = BasePlayer.Find(name);

        if (player != null)
        {
            return player;
        }

        return null;
    }

    private bool HasCupboardAccess(BuildingPrivlidge cupboard, BasePlayer player)
    {
        return cupboard.IsAuthed(player);
    }

    private bool HasTurretAccess(AutoTurret turret, BasePlayer player)
    {
        return turret.IsAuthed(player);
    }

    private string GetOwnerName(BaseEntity entity)
    {
        return FindPlayerName(entity.OwnerID);
    }

    private BasePlayer GetOwnerPlayer(BaseEntity entity)
    {
        if (entity.OwnerID > 76560000000000000L)
        {
            return BasePlayer.FindByID(entity.OwnerID);
        }

        return null;
    }

    private BasePlayer GetOwnerBasePlayer(BaseEntity entity)
    {
        if (entity.OwnerID > 76560000000000000L)
        {
            return BasePlayer.Find(entity.OwnerID.ToString());
        }

        return null;
    }

    private void RemoveOwner(BaseEntity entity)
    {
        entity.OwnerID = 0;
    }

    #endregion
}