using Auxide;
using ProtoBuf;
using Harmony;
using Rust;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System;
using System.IO;

public class HNoDecay : RustScript
{
    #region Harmony
    HarmonyInstance _harmony;
    #endregion

    private static ConfigData configData;

    public HNoDecay()
    {
        Author = "RFC1920";
        Version = new VersionNumber(1, 0, 3);
    }

    public class ConfigData
    {
        public bool debug;
        public bool disableTCWarning;
        public bool blockBuildingDecay;
        public bool blockDeployableDecay;
        public bool blockWoodFromTC;
        public bool blockStoneFromTC;
        public bool blockFragsFromTC;
        public bool blockMetalFromTC;
    }

    public override void Initialize()
    {
        LoadConfig();
        if (configData.disableTCWarning)
        {
            _harmony = HarmonyInstance.Create(Name + "PATCH");
            Type patchType = AccessTools.Inner(typeof(HNoDecay), "BNToStreamPatch");
            new PatchProcessor(_harmony, patchType, HarmonyMethod.Merge(patchType.GetHarmonyMethods())).Patch();
        }
    }

    public override void Dispose()
    {
        if (configData.disableTCWarning) _harmony.UnpatchAll(Name + "PATCH");
        base.Dispose();
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
            blockBuildingDecay = true,
            blockDeployableDecay = true
        };

        config.WriteObject(configData);
    }

    //private ItemContainer.CanAcceptResult? CanAcceptItem(ItemContainer container, Item item, int targetPos)
    //{
    //    BaseEntity cup = container?.entityOwner;
    //    if (cup == null) return null;
    //    if (!(cup is BuildingPrivlidge)) return null;

    //    if (!(configData.blockWoodFromTC || configData.blockStoneFromTC || configData.blockFragsFromTC || configData.blockMetalFromTC)) return null;

    //    string res = item?.info?.shortname;
    //    if (
    //        (res.Equals("wood") && configData.blockWoodFromTC)
    //        || (res.Equals("stones") && configData.blockStoneFromTC)
    //        || (res.Equals("metal.fragments") && configData.blockFragsFromTC)
    //        || (res.Equals("metal.refined") && configData.blockMetalFromTC))
    //    {
    //        if (configData.debug) DoLog($"Player blocked from adding {res} to a cupboard!");
    //        return ItemContainer.CanAcceptResult.CannotAcceptRightNow;
    //    }

    //    return null;
    //}

    public object OnTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
    {
        if (hitInfo == null) return null;
        if (entity != null && hitInfo.damageTypes.Has(DamageType.Decay) && (configData.blockBuildingDecay || configData.blockDeployableDecay))
        {
            if (configData.debug) DoLog($"Decay called for {entity.GetType().Name}({entity.ShortPrefabName})");
            {
                if ((entity is BuildingBlock) && entity?.OwnerID != 0 && configData.blockBuildingDecay)
                {
                    if (configData.debug) DoLog($"Blocking building block decay on {entity?.ShortPrefabName}");
                    // Scale amount
                    hitInfo.damageTypes.Scale(DamageType.Decay, 0);
                    //return true;
                }
                else if (entity?.OwnerID != 0 && configData.blockDeployableDecay)
                {
                    if (configData.debug) DoLog($"Blocking deployable decay on {entity?.ShortPrefabName}");
                    // Scale amount
                    hitInfo.damageTypes.Scale(DamageType.Decay, 0);
                    //return true;
                }
            }
        }
        return null;
    }

    [HarmonyPatch(typeof(BaseNetworkable), "ToStream", new Type[] { typeof(Stream), typeof(BaseNetworkable.SaveInfo) })]
    public static class BNToStreamPatch
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr, ILGenerator il)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instr);
            int startIndex = -1;
            int fixJump = -1;
            Label notBPLabel = new Label();
            Label newLabel = il.DefineLabel();

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Ldfld && codes[i + 1].opcode == OpCodes.Brtrue)
                {
                    fixJump = i + 1;
                }

                if (codes[i].opcode == OpCodes.Ldarg_2 && codes[i + 1].opcode == OpCodes.Ldfld && codes[i + 2].opcode == OpCodes.Ldarg_1)
                {
                    startIndex = i;
                    notBPLabel = codes[i].labels[0];
                    break;
                }
            }

            if (startIndex > -1)
            {
                List<CodeInstruction> instructionsToInsert = new List<CodeInstruction>()
            {
                // is type of BuildingPrivlidge?
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Isinst, typeof(BuildingPrivlidge)),
                new CodeInstruction(OpCodes.Ldnull),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(UnityEngine.Object), "op_Inequality")),
                new CodeInstruction(OpCodes.Brfalse_S, notBPLabel),

                // saveInfo.msg.buildingPrivilege.protectedMinutes = 4400;
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(BaseNetworkable.SaveInfo), "msg")),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Entity), "buildingPrivilege")),
                new CodeInstruction(OpCodes.Ldc_R4, 4400.1f),
                new CodeInstruction(OpCodes.Stfld, AccessTools.Field(typeof(BuildingPrivilege), "protectedMinutes")),
                // saveInfo.msg.buildingPrivilege.upkeepPeriodMinutes = 4400;
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(BaseNetworkable.SaveInfo), "msg")),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Entity), "buildingPrivilege")),
                new CodeInstruction(OpCodes.Ldc_R4, 4400.1f),
                new CodeInstruction(OpCodes.Stfld, AccessTools.Field(typeof(BuildingPrivilege), "upkeepPeriodMinutes"))
            };
                codes.InsertRange(startIndex, instructionsToInsert);
                codes[startIndex].labels.Add(newLabel);
            }

            if (fixJump > -1)
            {
                // Fix jump from saveInfo.msg.baseNetworkable == null to avoid skipping our new code
                List<CodeInstruction> instructionsToInsert = new List<CodeInstruction>()
            {
                new CodeInstruction(OpCodes.Brtrue_S, newLabel)
            };
                codes.RemoveRange(fixJump, 1);
                codes.InsertRange(fixJump, instructionsToInsert);
            }

            return codes.AsEnumerable();
        }
    }
}