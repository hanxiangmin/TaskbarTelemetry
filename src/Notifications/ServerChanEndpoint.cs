using System;
using System.Text.RegularExpressions;

namespace TaskbarTelemetry
{
    // ServerChan's official SCT/SC3 routing rules. Only a numeric SC3 user ID
    // can enter the host; the credential never selects an arbitrary endpoint.
    internal static class ServerChanEndpoint
    {
        internal static bool TryCreate(string value, out Uri endpoint, out string channel)
        {
            endpoint = null;
            channel = "Server酱";
            string key = value == null ? string.Empty : value.Trim();
            if (key.Length < 12 || key.Length > 512)
                return false;

            Match sc3 = Regex.Match(key, @"\Asctp([0-9]{1,20})t[A-Za-z0-9_-]+\z");
            if (sc3.Success)
            {
                endpoint = new Uri("https://" + sc3.Groups[1].Value +
                    ".push.ft07.com/send/" + key + ".send");
                channel = "Server酱³（App）";
                return true;
            }
            if (Regex.IsMatch(key, @"\ASCT[A-Za-z0-9_-]+\z"))
            {
                endpoint = new Uri("https://sctapi.ftqq.com/" + key + ".send");
                channel = "Server酱 Turbo（微信）";
                return true;
            }
            return false;
        }

        internal static bool IsValid(string value)
        {
            Uri endpoint;
            string channel;
            return TryCreate(value, out endpoint, out channel);
        }

        internal static string GetChannel(string value)
        {
            Uri endpoint;
            string channel;
            TryCreate(value, out endpoint, out channel);
            return channel;
        }
    }
}
