using System;
using System.Collections.Generic;
using UnityEngine;

namespace RepoBot
{
    /// <summary>
    /// The bot can talk in the in-game chat (on the user's request). Every message starts with "[bot]" so
    /// nobody mistakes it for the human, announcements are rate limited, and the bot never obeys what others
    /// type in chat - only the 'say' command from run/cmd.txt (the user, through Claude) makes it talk on demand.
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
            string text = "[bot] " + msg.Trim();
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
