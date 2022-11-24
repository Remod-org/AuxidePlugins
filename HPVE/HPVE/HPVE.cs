using Auxide;
using Rust;
using System.Collections.Generic;

public class HPVE : RustScript
{
    // Work in progress, and very early...
    // May replace with a large portion of NextGenPVE here
    private static ConfigData configData;

    public HPVE()
    {
        Author = "RFC1920";
        Version = new VersionNumber(1, 0, 1);
    }

    public class ConfigData
    {
        public bool debug;
        public bool allowAdminPVP;
        public bool allowFriendlyFire;
        public bool allowDamageToNPC;
        public Options Options;
    }

    public class Options
    {
        public float protectedDays;
        //public bool purgeEnabled;
        //public string purgeStart;
        //public string purgeEnd;
        //public string purgeStartMessage;
        //public string purgeEndMessage;
        //public bool autoCalcPurge;
        //public int autoCalcPurgeDays;
        //public bool useSchedule;
        public bool useMessageBroadcast;
        public bool useRealTime;
        public bool useTeams;
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

    public override void Initialize()
    {
        Utils.DoLog("HPVE loading...");
        if (ConVar.Server.pve) ConsoleSystem.Run(ConsoleSystem.Option.Server.FromServer(), "server.pve 0");
        LoadConfig();
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
            debug = false,
            allowAdminPVP = false,
            allowFriendlyFire = false,
            allowDamageToNPC = true,
            Options = new Options()
            {
                useTeams = true,
                AllowDropDatabase = false,
                AllowSuicide = true,
                AutoTurretTargetsNPCs = true,
                AutoTurretTargetsPlayers = true,
                NPCAutoTurretTargetsNPCs = true,
                NPCAutoTurretTargetsPlayers = true,
                BlockScrapHeliFallDamage = true,
                HonorBuildingPrivilege = true,
                AllowFriendlyFire = false,
                HeliTurretTargetsPlayers = true,
                HonorRelationships = true,
                NPCSamSitesIgnorePlayers = false,
                SamSitesIgnorePlayers = false,
                TrapsIgnorePlayers = false,
                UnprotectedBuildingDamage = true,
                UnprotectedDeployableDamage = true,
                useMessageBroadcast = false
            }
        };

        config.WriteObject(configData);
    }

    public object OnTakeDamage(BaseCombatEntity entity, HitInfo info)
    {
        if (info == null) return null;
        if (entity == null) return null;
        string majority = info.damageTypes.GetMajorityDamageType().ToString();
        if (majority == "Decay") return null;

        if (configData.debug) Utils.DoLog("ENTRY:");
        if (configData.debug) Utils.DoLog("Checking for fall damage");
        if (majority == "Fall" && info.Initiator == null)
        {
            if (configData.debug) Utils.DoLog($"Null initiator for attack on {entity.ShortPrefabName} by Fall");
            if (BlockFallDamage(entity))
            {
                if (configData.debug) Utils.DoLog(":EXIT");
                return true;
            }
        }

        if (info?.WeaponPrefab != null)// && info?.WeaponPrefab is TimedExplosive)
        {
            BaseEntity te = info?.WeaponPrefab;// as TimedExplosive;
                                               //BasePlayer attacker = info?.InitiatorPlayer;
            BasePlayer attacker = info?.InitiatorPlayer;// as BasePlayer;
            BasePlayer attacked = info?.HitEntity as BasePlayer;
            //DamageCheck(__instance, info, attacker, attacked, te);
            DamageCheck(entity, info, attacker, attacked, te);
        }
        else if (info?.Initiator is FireBall || info?.Initiator is FlameTurret || info?.Initiator is FlameThrower)
        {
            BasePlayer attacked = info?.HitEntity as BasePlayer;
            // Shortcut for post-bradley fire
            try
            {
                if (info.HitEntity.ShortPrefabName.Equals("servergibs_bradley") || info.HitEntity.ShortPrefabName.Equals("bradley_crate")) return null;
            }
            catch { }
            DamageCheck(entity, info, null, attacked, null);
        }
        else if (info?.InitiatorPlayer != null)
        {
            //BasePlayer attacker = info?.Initiator as BasePlayer;
            BasePlayer attacker = info?.InitiatorPlayer;// as BasePlayer;
            BasePlayer attacked = info?.HitEntity as BasePlayer;
            DamageCheck(entity, info, attacker, attacked);
        }
        return null;
    }

    static void DamageCheck(BaseCombatEntity entity, HitInfo info, BasePlayer attacker, BasePlayer attacked, BaseEntity te = null)
    {
        bool isFriend = false;
        bool isFriend2 = false;
        if (attacker != null && entity != null && info?.Initiator != null)
        {
            isFriend = Utils.IsFriend(attacker.userID, entity.OwnerID);
            isFriend2 = Utils.IsFriend(info.Initiator.OwnerID, entity.OwnerID);
        }
        string weapon = te != null ? " using " + te?.GetType() : "";
        //if (configData.debug) Utils.DoLog($"DamageCheck for {info?.Initiator?.GetType()}({attacker?.displayName}) to {entity?.ShortPrefabName}({attacked?.displayName}){weapon}");
        //if (configData.debug) Utils.DoLog($"DamageCheck for {info?.Initiator?.GetType()}({(info?.Initiator as BasePlayer)?.displayName}) to {entity?.GetType()}({attacked?.displayName}){weapon}");
        if (configData.debug) Utils.DoLog($"DamageCheck for {info?.Initiator?.GetType()}({attacker?.displayName}) to {entity?.GetType()}({attacked?.displayName}){weapon}");
        if (attacked != null)
        {
            // Attacked is a player, but are they a real player and a friend, etc.
            if (attacker != null && attacked?.userID != attacker?.userID && !isFriend)// && attacked?.userID > 76560000000000000L)
            {
                if (attacked?.userID < 76560000000000000L)
                {
                    if (configData.allowDamageToNPC)
                    {
                        Utils.DoLog($"Allowing PVP damage by {attacker?.displayName}{weapon} to NPC");
                        return;
                    }
                    Utils.DoLog($"Blocking PVP damage by {attacker?.displayName}{weapon} to NPC");
                    info.damageTypes.ScaleAll(0);
                    return;
                }

                // Attacker is a player
                if (attacker.IsAdmin && configData.allowAdminPVP)
                {
                    if (configData.debug) Utils.DoLog($"Allowing admin damage by {attacker?.displayName}{weapon} to '{attacked?.displayName}'");
                    return;
                }
                if (attacker.userID > 76560000000000000L)
                {
                    Utils.DoLog($"Blocking PVP damage by {attacker?.displayName}{weapon} to '{attacked?.displayName}'");
                    if (te is TimedExplosive) te?.Kill();
                    info.damageTypes.ScaleAll(0);
                }
            }
            else if (!(info?.Initiator is BasePlayer) && !isFriend && attacker.userID > 76560000000000000L)
            {
                // Attacker is not a player
                Utils.DoLog($"Blocking PVP damage by {info?.Initiator?.GetType()}{weapon} to '{attacked?.displayName}'");
                if (te is TimedExplosive) te?.Kill();
                info.damageTypes.ScaleAll(0);
            }
        }
        else if (attacker != null && entity?.OwnerID != attacker?.userID && entity?.OwnerID != 0 && !isFriend)
        {
            // Attacker is a player, attacked is null, but victim is entity
            BasePlayer owner = BasePlayer.Find(entity?.OwnerID.ToString());
            if (attacker.IsAdmin && configData.allowAdminPVP)
            {
                if (configData.debug) Utils.DoLog($"Allowing admin damage by {attacker?.displayName}{weapon} to '{entity?.ShortPrefabName}' owned by {owner?.displayName}");
                return;
            }
            if (attacker.userID > 76560000000000000L)
            {
                Utils.DoLog($"Blocking PVP damage by {attacker?.displayName}{weapon} to '{entity?.ShortPrefabName}' owned by {owner?.displayName}");
                if (te is TimedExplosive) te?.Kill();
                info.damageTypes.ScaleAll(0);
            }
        }
        else if (entity?.OwnerID != info?.Initiator?.OwnerID && entity?.OwnerID != 0 && info?.Initiator?.OwnerID != 0 && !isFriend2)
        {
            // Attacker is an owned entity and victim is an owned entity
            BasePlayer owner = BasePlayer.Find(entity?.OwnerID.ToString());
            BasePlayer attackr = BasePlayer.Find(info?.Initiator?.OwnerID.ToString());
            if (attackr != null && owner != null && attackr.IsAdmin && configData.allowAdminPVP)
            {
                if (configData.debug) Utils.DoLog($"Allowing admin damage by {attackr?.displayName}{weapon} to '{entity?.ShortPrefabName}' owned by {owner?.displayName}");
                return;
            }
            if (attacker.userID > 76560000000000000L)
            {
                Utils.DoLog($"Blocking PVP damage from {info?.Initiator?.ShortPrefabName} owned by {attackr?.displayName}{weapon} to '{entity?.ShortPrefabName}'");// owned by {owner?.displayName}");
                if (te is TimedExplosive) te?.Kill();
                info.damageTypes.ScaleAll(0);
            }
        }
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
}
//if (entity != null && hitInfo.damageTypes.Has(DamageType.Decay) && (configData.blockBuildingDecay || configData.blockDeployableDecay))
//{
//    if (configData.debug) Utils.DoLog($"Decay called for {entity.GetType().Name}({entity.ShortPrefabName})");
//    {
//        if ((entity is BuildingBlock) && entity?.OwnerID != 0 && configData.blockBuildingDecay)
//        {
//            if (configData.debug) Utils.DoLog($"Blocking building block decay on {entity?.ShortPrefabName}");
//            // Scale amount
//            hitInfo.damageTypes.Scale(DamageType.Decay, 0);
//            //return true;
//        }
//        else if (entity?.OwnerID != 0 && configData.blockDeployableDecay)
//        {
//            if (configData.debug) Utils.DoLog($"Blocking deployable decay on {entity?.ShortPrefabName}");
//            // Scale amount
//            hitInfo.damageTypes.Scale(DamageType.Decay, 0);
//            //return true;
//        }
//    }
//}
