using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RepoBot
{
    /// <summary>
    /// The player can talk in the in-game chat (on the user's request). The word "bot" is never written in chat
    /// (no prefix, and it is stripped from any text), announcements are rate limited, and nobody can order the
    /// player around through chat - only the 'say' command from run/cmd.txt makes it talk on demand.
    /// </summary>
    public partial class Brain
    {
        bool chatOn = true;
        float nextSay;
        readonly HashSet<string> said = new HashSet<string>();

        /// <summary>Send one chat line. Announcements are limited to one every 8 s; 'say' commands (force) to one per second.</summary>
        bool Say(string msg, bool force = false)
        {
            if (string.IsNullOrEmpty(msg) || msg.Trim().Length == 0) return false;
            if (!force && (!chatOn || Time.time < nextSay)) return false;
            ChatManager cm = R.GetStatic<ChatManager>(typeof(ChatManager), "instance");
            if (cm == null) { Plugin.Log.LogWarning("chat: no chat manager available here"); return false; }
            nextSay = Time.time + (force ? 1f : 8f);
            string text = Regex.Replace(msg, @"\[?\bbots?\b\]?:?", "", RegexOptions.IgnoreCase).Trim();
            if (text.Length == 0) return false;
            Plugin.Log.LogInfo("chat: " + text);
            try { cm.ForceSendMessage(text); return true; }
            catch (Exception e) { Plugin.Log.LogWarning("chat failed: " + e.Message); return false; }
        }

        /// <summary>Announce something once per level (the key is forgotten when the level changes).</summary>
        void SayOnce(string key, string msg)
        {
            if (said.Contains(key)) return;
            if (Say(msg)) said.Add(key);
        }
    }
}
