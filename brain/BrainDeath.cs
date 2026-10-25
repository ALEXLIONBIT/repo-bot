using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace RepoBot
{
    /// <summary>
    /// What happens when the bot dies: record every hit it takes, write a death report (what it was doing,
    /// who was around), learn to keep more distance from whatever killed it, and - when this game lets us
    /// (we are the master client / singleplayer) - bring the player back to life.
    /// </summary>
    public partial class Brain
    {
        static Brain current;            // the live brain; the Harmony patches below call into it
        Harmony harmony;

        bool autoRevive = true;
        int manualRevive;               // which way the next manual 'revive' command tries
        float noLearnUntil;             // a 'kill' test must not teach the bot anything
        bool wasDead;
        float nextReviveTry;
        int reviveTries;
        bool reviveWarned;

        string lastHurtEnemy = "";
        float lastHurtTime = -99f;
        readonly List<string> recentHurts = new List<string>();
        readonly LinkedList<string> trace = new LinkedList<string>();   // ~12 s of what the bot was doing, 0.5 s steps
        readonly Dictionary<string, float> boost = new Dictionary<string, float>();   // enemy name -> extra metres to keep away

        void Awake()
        {
            current = this;
            LoadLearned();
            harmony = new Harmony("local.repobot.brain." + GetInstanceID());
            harmony.PatchAll(typeof(Brain).Assembly);
        }

        void OnDisable()
        {
            if (harmony != null) { harmony.UnpatchSelf(); harmony = null; }
            if (current == this) current = null;
        }

        // ---- learned avoidance ---------------------------------------------------------------
        float Boost(string enemyName)
        {
            float v;
            // capped: a few deaths must not make the bot too scared to do anything
            return !string.IsNullOrEmpty(enemyName) && boost.TryGetValue(enemyName, out v) ? Mathf.Min(v, 8f) : 0f;
        }

        void LoadLearned()
        {
            try
            {
                string f = Dir + "learned.txt";
                if (!File.Exists(f)) return;
                foreach (string line in File.ReadAllLines(f))
                {
                    int eq = line.LastIndexOf('=');
                    float v;
                    if (eq > 0 && float.TryParse(line.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) boost[line.Substring(0, eq)] = v;
                }
                Plugin.Log.LogInfo("learned avoidance loaded: " + string.Join(", ", boost.Select(kv => kv.Key + " +" + kv.Value.ToString("0") + "m").ToArray()));
            }
            catch (Exception e) { Plugin.Log.LogWarning("learned.txt: " + e.Message); }
        }

        void SaveLearned()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(Dir + "learned.txt", boost.Select(kv => kv.Key + "=" + kv.Value.ToString("0.##", CultureInfo.InvariantCulture)).ToArray());
            }
            catch (Exception e) { Plugin.Log.LogWarning("learned.txt: " + e.Message); }
        }

        // ---- recording hits --------------------------------------------------------------------
        bool NearestEnemy(Vector3 p, out EnemyParent enemy, out float dist)
        {
            enemy = null; dist = float.MaxValue;
            foreach (EnemyParent e in enemies)
            {
                if (e == null) continue;
                float d = Vector3.Distance(p, EnemyPos(e));
                if (d < dist) { dist = d; enemy = e; }
            }
            return enemy != null;
        }

        internal static void OnHurt(PlayerHealth h, int damage, int enemyIndex)
        {
            Brain b = current;
            if (b == null) return;
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            if (pa == null || R.Get<PlayerHealth>(pa, "playerHealth") != h) return;   // not us
            b.RecordHurt(damage, enemyIndex);
        }

        void RecordHurt(int damage, int enemyIndex)
        {
            PlayerController pc = Local();
            Vector3 p = pc != null ? pc.transform.position : Vector3.zero;
            EnemyParent e; float d;
            string who = "nobody close";
            lastHurtEnemy = "";
            if (NearestEnemy(p, out e, out d) && d < 15f)
            {
                lastHurtEnemy = R.Get<string>(e, "enemyName", "?");
                who = lastHurtEnemy + " (" + EnemyStateName(e) + ") " + d.ToString("0.0") + "m away";
            }
            if (who == "nobody close" && damage > 0 && SemiFunc.IsMultiplayer()) NoteAttacker(p, damage);
            lastHurtTime = Time.time;
            string line = Time.time.ToString("0.0", CultureInfo.InvariantCulture) + "s: hurt " + damage + " (enemy index " + enemyIndex + "), nearest: " + who +
                          " | hp now " + Hp() + " | bot " + state + " / " + status;
            recentHurts.Add(line);
            if (recentHurts.Count > 8) recentHurts.RemoveAt(0);
            Plugin.Log.LogWarning(line);
        }

        void PushTrace()
        {
            PlayerController pc = Local();
            string near = "";
            EnemyParent e; float d;
            if (pc != null && NearestEnemy(pc.transform.position, out e, out d)) near = R.Get<string>(e, "enemyName", "?") + "(" + EnemyStateName(e) + ") " + d.ToString("0") + "m";
            trace.AddLast(Time.time.ToString("0.0", CultureInfo.InvariantCulture) + "s hp " + Hp() + " " + state + " | " + status + " | nearest " + (near == "" ? "-" : near));
            while (trace.Count > 24) trace.RemoveFirst();
        }

        // ---- dying and coming back ------------------------------------------------------------
        void WatchDeath()
        {
            bool dead = Dead();
            if (dead && !wasDead)
            {
                OnDeath();
                reviveTries = 0; reviveWarned = false;
                nextReviveTry = Time.time + 0.8f;   // quickly, before the game starts its game-over flow
            }
            else if (!dead && wasDead)
            {
                Plugin.Log.LogInfo("alive again (hp " + Hp() + ")");
                ResetPlan();
                Say("Sono tornato in vita!");
                teleportAt = Time.time + 0.4f;   // let the game finish its own revive, then put us on the floor
                teleportWhy = "after being revived";
            }
            wasDead = dead;

            if (!dead || !autoRevive || !BotInput.Active || Time.time < nextReviveTry) return;
            if (!SemiFunc.IsMasterClientOrSingleplayer())
            {
                if (!reviveWarned)
                {
                    reviveWarned = true;
                    Plugin.Log.LogWarning("auto-revive: this game is hosted by someone else. The game only lets the host revive a player, so I cannot do it here (host the lobby yourself or play singleplayer).");
                }
                return;
            }
            if (reviveTries >= 9) return;
            nextReviveTry = Time.time + 1.5f;
            ReviveStep(reviveTries++);
        }

        // ---- never stay in the void / above the map -----------------------------------------------
        // The game parks a dead body far away (y -50 or +1400) and revives it wherever the death head ended up,
        // so after a revive we put the player back on the floor, and we do the same if the bot ever ends up off the map.
        Vector3 lastGood;
        bool haveLastGood;
        float nextPosCheck, offMeshSince, teleportAt;
        string teleportWhy = "";
        bool diedToEnemy;

        void WatchPosition()
        {
            PlayerController pc = Local();
            if (pc == null || Dead()) return;
            Vector3 p = pc.transform.position;

            if (teleportAt > 0f && Time.time >= teleportAt)
            {
                teleportAt = 0f;
                TeleportToSafe(teleportWhy);
                return;
            }
            if (Time.time < nextPosCheck) return;
            nextPosCheck = Time.time + 0.5f;

            NavMeshHit h;
            bool onMesh = (p.y > -15f && p.y < 80f) && NavMesh.SamplePosition(p, out h, 2.5f, NavMesh.AllAreas);
            if (onMesh) { NavMesh.SamplePosition(p, out h, 2.5f, NavMesh.AllAreas); lastGood = h.position; haveLastGood = true; offMeshSince = 0f; return; }
            if (!BotInput.Active || !InLevel()) { offMeshSince = 0f; return; }
            if (p.y < -15f || p.y > 80f) { TeleportToSafe("outside the map, y=" + p.y.ToString("0")); return; }
            if (offMeshSince == 0f) offMeshSince = Time.time;
            else if (Time.time - offMeshSince > 3f) TeleportToSafe("off the walkable floor for 3 s at " + V(p));
        }

        void TeleportToSafe(string why)
        {
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            PlayerController pc = Local();
            if (pa == null || pc == null) return;
            // after being killed by an enemy go to the truck, otherwise back to where we last stood
            Vector3 target = (diedToEnemy && haveHome) ? home : (haveLastGood ? lastGood : (haveHome ? home : pc.transform.position));
            NavMeshHit h;
            if (NavMesh.SamplePosition(target, out h, 6f, NavMesh.AllAreas)) target = h.position;
            Transform c = Cam();
            Quaternion rot = Quaternion.Euler(0f, c != null ? c.eulerAngles.y : 0f, 0f);
            Plugin.Log.LogInfo("teleport to a safe spot " + V(target) + " (" + why + ") from " + V(pc.transform.position));
            pa.Spawn(target + Vector3.up * 0.2f, rot);
            offMeshSince = 0f;
            nextPosCheck = Time.time + 1.0f;
            diedToEnemy = false;
        }

        // ---- keeping health up -----------------------------------------------------------------
        bool autoHeal = true;
        const int HealBelow = 40;           // heal to full whenever health drops under this
        float nextHeal, healCheckAt;
        int healFromHp;
        bool healWarned;

        void WatchHealth()
        {
            if (healCheckAt > 0f && Time.time >= healCheckAt)
            {
                healCheckAt = 0f;
                if (Hp() <= healFromHp && !healWarned)
                {
                    healWarned = true;
                    Plugin.Log.LogWarning("auto-heal: health did not change after Heal() (hp " + Hp() + "): the game may only accept it from the host");
                }
                else if (Hp() > healFromHp) Plugin.Log.LogInfo("auto-heal worked: hp " + healFromHp + " -> " + Hp());
            }
            if (!autoHeal || !BotInput.Active || Time.time < nextHeal) return;
            if (!SemiFunc.RunIsLevel() && !SemiFunc.RunIsShop()) return;
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            if (pa == null || Dead()) return;
            PlayerHealth ph = R.Get<PlayerHealth>(pa, "playerHealth");
            if (ph == null) return;
            int hp = R.Get<int>(ph, "health", -1), max = R.Get<int>(ph, "maxHealth", 100);
            if (hp <= 0 || max <= 0 || hp >= HealBelow) return;
            nextHeal = Time.time + 4f;
            healFromHp = hp;
            healCheckAt = Time.time + 1.5f;
            Plugin.Log.LogInfo("auto-heal: hp " + hp + "/" + max + " -> healing " + (max - hp));
            ph.Heal(max - hp, true);
        }

        /// <summary>One try at reviving, cycling through the game's own ways: death head, truck-style, in place.</summary>
        void ReviveStep(int attempt)
        {
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            if (pa == null) return;
            PlayerDeathHead head = R.Get<PlayerDeathHead>(pa, "playerDeathHead");
            // Measured in singleplayer: Revive(true) brings us back in place; the death head's Revive() does nothing.
            int way = new[] { 1, 2, 0 }[attempt % 3];
            Plugin.Log.LogInfo("revive try " + (attempt + 1) + ": " + (way == 0 ? "death head Revive()" : way == 1 ? "Revive(true)" : "Revive(false)") +
                               " (master " + SemiFunc.IsMasterClientOrSingleplayer() + ", head " + (head != null) + ")");
            try
            {
                if (way == 0 && head != null) head.Revive();
                else pa.Revive(way == 1);
            }
            catch (Exception e) { Plugin.Log.LogWarning("revive failed: " + e.Message); }
        }

        void OnDeath()
        {
            var sb = new StringBuilder();
            PlayerController pc = Local();
            Vector3 p = pc != null ? pc.transform.position : Vector3.zero;
            sb.AppendLine("=== DEATH " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " | level " + lastLevel + " ===");
            sb.AppendLine("bot was in state " + state + ": " + status + " at " + V(p));
            sb.AppendLine("hits in the last moments:");
            foreach (string h in recentHurts) sb.AppendLine("  " + h);
            sb.AppendLine("what the bot was doing (0.5 s steps, newest last):");
            foreach (string t in trace) sb.AppendLine("  " + t);

            // learn: whoever hurt us last, else whoever was closest
            string culprit = Time.time - lastHurtTime < 8f ? lastHurtEnemy : "";
            EnemyParent e; float d;
            if (culprit == "" && NearestEnemy(p, out e, out d) && d < 25f) culprit = R.Get<string>(e, "enemyName", "");
            diedToEnemy = culprit != "" && Time.time >= noLearnUntil;
            if (Time.time < noLearnUntil) sb.AppendLine("(test death asked by the user: nothing learned)");
            else if (culprit != "")
            {
                float old; boost.TryGetValue(culprit, out old);
                boost[culprit] = Mathf.Min(20f, old + 4f);
                SaveLearned();
                sb.AppendLine("LEARNED: '" + culprit + "' killed us -> keep " + boost[culprit].ToString("0") + " m extra away from it from now on");
            }
            else sb.AppendLine("no enemy could be blamed (fall, trap, or an enemy that was not on my list)");

            Say(culprit != "" && Time.time >= noLearnUntil ? "Sono morto... colpa di " + culprit + "! La prossima volta lo evito." : "Sono morto, accidenti.");
            string report = sb.ToString();
            foreach (string line in report.Split('\n')) if (line.Trim().Length > 0) Plugin.Log.LogWarning(line.TrimEnd());
            try { Directory.CreateDirectory(Dir); File.AppendAllText(Dir + "deaths.log", report + "\n"); }
            catch (Exception ex) { Plugin.Log.LogWarning("deaths.log: " + ex.Message); }
        }

        /// <summary>Test helper, only on the user's explicit command: take some health away without killing.</summary>
        void HurtSelf(int amount)
        {
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            PlayerHealth ph = R.Get<PlayerHealth>(pa, "playerHealth");
            if (ph == null) return;
            Plugin.Log.LogWarning("hurt test: user asked to take " + amount + " health");
            noLearnUntil = Time.time + 10f;
            ph.Hurt(amount, false, -1, false);
        }

        /// <summary>Test helper, only on the user's explicit command: take the local player's health to zero.</summary>
        void KillSelf()
        {
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            PlayerHealth ph = R.Get<PlayerHealth>(pa, "playerHealth");
            if (ph == null) return;
            Plugin.Log.LogWarning("kill: user asked for a test death");
            noLearnUntil = Time.time + 10f;
            ph.Hurt(9999, false, -1, false);
        }
    }

    [HarmonyPatch(typeof(PlayerHealth), "Hurt")]
    static class HurtPatch
    {
        static void Postfix(PlayerHealth __instance, int damage, int enemyIndex)
        {
            Brain.OnHurt(__instance, damage, enemyIndex);
        }
    }
}
