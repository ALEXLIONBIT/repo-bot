using System.Collections.Generic;
using UnityEngine;

namespace RepoBot
{
    /// <summary>
    /// What the bot "presses". The Harmony patches in Plugin.cs add this on top of the real input,
    /// and the (hot-reloadable) Brain assembly writes it once per frame.
    /// </summary>
    public static class BotInput
    {
        public static bool Active;
        public static Vector2 Move;
        public static readonly HashSet<InputKey> Held = new HashSet<InputKey>();

        static readonly HashSet<InputKey> prev = new HashSet<InputKey>();
        static readonly Dictionary<InputKey, int> pressed = new Dictionary<InputKey, int>();
        static readonly Dictionary<InputKey, int> released = new Dictionary<InputKey, int>();

        public static bool JustPressed(InputKey k) { int f; return pressed.TryGetValue(k, out f) && Time.frameCount - f <= 1; }
        public static bool JustReleased(InputKey k) { int f; return released.TryGetValue(k, out f) && Time.frameCount - f <= 1; }

        /// <summary>Start of a frame: remember last frame's keys and clear the slate.</summary>
        public static void BeginFrame()
        {
            prev.Clear();
            foreach (InputKey k in Held) prev.Add(k);
            Held.Clear();
            Move = Vector2.zero;
        }

        /// <summary>End of the bot's frame: turn changes of Held into press/release events.</summary>
        public static void EndFrame()
        {
            int f = Time.frameCount;
            foreach (InputKey k in prev) if (!Held.Contains(k)) released[k] = f;
            foreach (InputKey k in Held) if (!prev.Contains(k)) pressed[k] = f;
        }

        public static void Reset()
        {
            Held.Clear();
            prev.Clear();
            Move = Vector2.zero;
        }
    }
}
