using Auxide;

public class TestScript : RustScript
{
    private static ConfigData configData;
    public bool enableme = true;

	public TestScript()
	{
        Author = "RFC1920";
        Version = new VersionNumber(1, 0, 1);
    }

    public void OnPluginLoaded(IScriptReference script)
    {
        Utils.DoLog($"{script.Name} Loaded");
    }

    public void OnScriptUnLoaded(IScriptReference script)
    {
        Utils.DoLog($"{script.Name} Unloaded");
    }

    public void OnServerInitialized()
    {
        Utils.DoLog($"{Name} OSE");
    }

    public override void Initialize()
	{
        LoadConfig();
        enableme = configData.Enable;
        Utils.DoLog("Let's test some hooks!");
	}

    public override void Dispose()
    {
        base.Dispose();
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

    public object CanToggleSwitch(BaseOven oven, BasePlayer player)
    {
        if (!enableme) return null;
        if (oven.OwnerID != 0 && player.userID != oven.OwnerID && !Utils.IsFriend(player.userID, oven.OwnerID))
        {
            Utils.DoLog($"{player.userID} BLOCKED FROM toggling oven {oven.ShortPrefabName}");
            return true;
		}
        Utils.DoLog($"{player.userID} toggling oven {oven.ShortPrefabName}");
		return null;
    }

    public object CanToggleSwitch(ElectricSwitch sw, BasePlayer player)
    {
        if (!enableme) return null;
		if (sw.OwnerID != 0 && player.userID != sw.OwnerID && !Utils.IsFriend(player.userID, sw.OwnerID))
		{
            Utils.DoLog($"{player.userID} BLOCKED FROM toggling oven {sw.ShortPrefabName}");
            return true;
		}
        Utils.DoLog($"{player.userID} toggling switch {sw.ShortPrefabName}");
		return null;
    }

    // Not yet working - called but cannot cancel/block?
    public object CanMount(BaseMountable entity, BasePlayer player)
    {
        if (!enableme) return null;
        if (entity.OwnerID != 0 && player.userID != entity.OwnerID && !Utils.IsFriend(player.userID, entity.OwnerID))
		{
            Utils.DoLog($"{player.userID} BLOCKED FROM mounting {entity.ShortPrefabName}");
            return true;
		}
        Utils.DoLog($"{player.userID} mounting {entity.ShortPrefabName}");
		return null;
    }

    public void OnMounted(BaseMountable entity, BasePlayer player)
    {
        if (!enableme) return;
        if (entity == null) return;
        if (player == null) return;
        Utils.DoLog($"{player.userID} mounted {entity.ShortPrefabName}");
    }

    public object CanLoot(StorageContainer container, BasePlayer player, string panelName)
    {
        if (!enableme) return null;
        if (player == null || container == null) return null;
        BaseEntity ent = container?.GetComponentInParent<BaseEntity>();
        if (ent == null) return null;

        Utils.DoLog($"{player.userID} looting {ent.ShortPrefabName}");
        return null;
    }

    public void OnLooted(BaseEntity entity, BasePlayer player)
    {
        if (!enableme) return;
        if (entity == null) return;
        if (player == null) return;
        Utils.DoLog($"{player.userID} looted {entity.ShortPrefabName}");
    }

    public void OnEntitySaved(BaseNetworkable entity, BaseNetworkable.SaveInfo saveInfo)
    {
        if (!enableme) return;
        Utils.DoLog($"OnEntitySaved called for {entity?.ShortPrefabName}");
    }

    public void OnEntityDeath(BaseCombatEntity entity, HitInfo hitinfo)
    {
        if (!enableme) return;
        Utils.DoLog($"{entity?.ShortPrefabName} was killed by {hitinfo?.HitEntity.ShortPrefabName}.");
    }

    public void OnPlayerJoin(BasePlayer player)
    {
        if (!enableme) return;
        Utils.DoLog($"{player.userID} connected.");
    }

    public void OnPlayerLeave(BasePlayer player)
    {
        if (!enableme) return;
        Utils.DoLog($"{player.userID} disconnected.");
    }

    public object OnTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
    {
        if (!enableme) return null;
        Utils.DoLog($"OnTakeDamage called for {hitInfo.damageTypes.GetMajorityDamageType()} attacking {entity.ShortPrefabName}.");
        return null;
    }

    public object OnConsoleCommand(string command, bool isServer)
    {
        if (!enableme) return null;
        Utils.DoLog($"OnConsoleCommand called for '{command}' with isServer={isServer}");
        if (command == "testfail") return false;
        return null;
    }

    public ItemContainer.CanAcceptResult? CanAcceptItem(ItemContainer container, Item item, int targetPos)
    {
        if (!enableme) return null;
        BaseEntity ent = container?.entityOwner;
        Utils.DoLog($"CanAcceptItem called for '{ent?.ShortPrefabName}', item '{item?.info.displayName.english}', AND targetPos='{targetPos}'");
        return null;
    }

    public object OnConsoleCommand(ConsoleSystem.Arg arg)
    {
        if (!enableme) return null;
        string pname = arg.GetString(0);
        BasePlayer player = BasePlayer.Find(pname);
        string arginfo = string.Join(",", arg);
        Utils.DoLog($"OnConsoleCommand called by '{player.displayName}' for '{arg.cmd} with args='{arginfo}'");
        return null;
    }

    public void OnChatCommand(BasePlayer player, string command, string[] args = null)
    {
        if (!enableme) return;
        string arginfo = string.Join(",", args);
        Utils.DoLog($"OnChatCommand called for '{command}' with args='{arginfo}'");
        switch (command)
        {
            case "ttoggle":
                enableme = !enableme;
                configData.Enable = enableme;
                SaveConfig(configData);
                Message(player, enableme ? "TestScript enabled" : "TestScript disabled");
                break;
        }
    }
}
