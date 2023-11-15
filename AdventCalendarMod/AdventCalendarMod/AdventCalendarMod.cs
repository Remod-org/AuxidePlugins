using Auxide;
using Newtonsoft.Json;
using System.Collections.Generic;
using UnityEngine;

[Info("AdventCalendarMod", "RFC1920", "1.0.1")]
[Description("Modify active dates for AdvenCalendar - WIP")]
public class AdventCalendarMod : RustScript
{
    #region vars
    private ConfigData configData;
    #endregion

    public override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            ["notauthorized"] = "You don't have permission to use this command."
        }, Name);
    }

    public override void Initialize()
    {
        LoadConfig();
    }

    public void OnChatCommand(BasePlayer player, string command, string[] args = null)
    {
        if (player == null) return;
        if (!player.IsAdmin) { Message(player, "notauthorized"); return; }

        switch (command)
        {
            case "acinfo":
                CmdACInfo(player, command, args);
                break;
        }
    }

    private void CmdACInfo(BasePlayer player, string command, string[] args)
    {
        RaycastHit hit;
        if (Physics.Raycast(player.eyes.HeadRay(), out hit, 3f, LayerMask.GetMask("Deployed")))
        {
            AdventCalendar cal = hit.GetEntity() as AdventCalendar;
            if (cal != null)
            {
                foreach (AdventCalendar.DayReward day in cal.days)
                {
                    foreach (ItemAmount ia in day.rewards)
                    {
                        Message(player, ia.amount.ToString());
                    }
                }
            }
        }
    }

    private void DoLog(string message)
    {
        if (configData.Options.debug) Utils.DoLog(message);
    }

    #region config
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
        Utils.DoLog("Creating new config file.");
        ConfigData cfg = new ConfigData
        {
            Options = new Options()
            {
                debug = false,
                activeDays = new List<string>()
            },
            Version = Version
        };

        SaveConfig(cfg);
    }

    private void SaveConfig(ConfigData cfg)
    {
        config.WriteObject(cfg);
    }

    public class ConfigData
    {
        public Options Options;
        public VersionNumber Version;
    }

    public class Options
    {
        [JsonProperty(PropertyName = "Enable debugging")]
        public bool debug;

        [JsonProperty(PropertyName = "Active days")]
        public List<string> activeDays;
    }
    #endregion
}
