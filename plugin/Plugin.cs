using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;
using UnityEngine;

namespace RepoBot
{
    /// <summary>
    /// Fixed half of the bot: input patches + a loader that hot-reloads run/Brain.dll
    /// (the actual decision logic) whenever it is rebuilt, so the game does not need a restart.
    /// </summary>
    [BepInPlugin("local.repobot", "RepoBot", "0.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        void Awake()
        {
            Log = Logger;
            new Harmony("local.repobot").PatchAll(typeof(Plugin).Assembly);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                new AssemblyName(e.Name).Name == "RepoBot" ? typeof(Plugin).Assembly : null;
            GameObject go = new GameObject("RepoBotLoader");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<Loader>();
            Log.LogInfo("RepoBot 0.2.0 loaded - brain is hot-reloaded from run/Brain.dll");
        }
    }

    class Loader : MonoBehaviour
    {
        const string BrainPath = @"Z:\home\ale\repo-bot\run\Brain.dll";   // Wine path of /home/ale/repo-bot/run
        DateTime loadedStamp;
        Component brain;
        int generation;
        float next;

        void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            try
            {
                if (!File.Exists(BrainPath)) return;
                DateTime stamp = File.GetLastWriteTimeUtc(BrainPath);
                if (stamp == loadedStamp) return;
                loadedStamp = stamp;   // set first: a broken build is not retried in a loop
                Reload();
            }
            catch (Exception e) { Plugin.Log.LogError("brain reload failed: " + e); }
        }

        void Reload()
        {
            byte[] raw = File.ReadAllBytes(BrainPath);
            // Mono returns the already-loaded assembly for a repeated name, so give every build its own.
            string name = "RepoBotBrain" + (++generation);
            AssemblyDefinition def = AssemblyDefinition.ReadAssembly(new MemoryStream(raw));
            def.Name.Name = name;
            def.MainModule.Name = name + ".dll";
            byte[] patched;
            using (MemoryStream o = new MemoryStream()) { def.Write(o); patched = o.ToArray(); }

            Type type = Assembly.Load(patched).GetType("RepoBot.Brain", true);
            if (brain != null) DestroyImmediate(brain);
            BotInput.Reset();
            brain = gameObject.AddComponent(type);
            Plugin.Log.LogInfo("brain #" + generation + " loaded (" + raw.Length + " bytes)");
        }
    }

    // The bot "presses" keys by changing what the game's own InputManager reports.
    // The human's real input still passes through, so taking over the controls is always possible.
    static class Active
    {
        public static bool On { get { return BotInput.Active; } }
    }

    [HarmonyPatch(typeof(InputManager), "KeyHold")]
    static class KeyHoldPatch
    {
        static void Postfix(InputKey key, ref bool __result)
        {
            if (Active.On && BotInput.Held.Contains(key)) __result = true;
        }
    }

    [HarmonyPatch(typeof(InputManager), "KeyDown")]
    static class KeyDownPatch
    {
        static void Postfix(InputKey key, ref bool __result)
        {
            if (Active.On && BotInput.JustPressed(key)) __result = true;
        }
    }

    [HarmonyPatch(typeof(InputManager), "KeyUp")]
    static class KeyUpPatch
    {
        static void Postfix(InputKey key, ref bool __result)
        {
            if (Active.On && BotInput.JustReleased(key)) __result = true;
        }
    }

    [HarmonyPatch(typeof(InputManager), "GetMovement")]
    static class GetMovementPatch
    {
        static void Postfix(ref Vector2 __result)
        {
            if (Active.On) __result = Vector2.ClampMagnitude(__result + BotInput.Move, 1f);
        }
    }

    [HarmonyPatch(typeof(InputManager), "GetMovementX")]
    static class GetMovementXPatch
    {
        static void Postfix(ref float __result)
        {
            if (Active.On) __result = Mathf.Clamp(__result + BotInput.Move.x, -1f, 1f);
        }
    }

    [HarmonyPatch(typeof(InputManager), "GetMovementY")]
    static class GetMovementYPatch
    {
        static void Postfix(ref float __result)
        {
            if (Active.On) __result = Mathf.Clamp(__result + BotInput.Move.y, -1f, 1f);
        }
    }
}
