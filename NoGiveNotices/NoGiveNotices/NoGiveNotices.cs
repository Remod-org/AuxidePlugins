[Info("NoGiveNotices", "RFC1920", "1.0.1")]
public class NoGiveNotices : RustScript
{
    public object OnServerMessage(string message, string username, string color, ulong userid)
    {
        if (message.Contains("gave") && username == "SERVER")
        {
            return true;
        }

        return null;
    }
}
