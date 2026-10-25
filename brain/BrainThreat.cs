using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace RepoBot
{
    /// <summary>
    /// Enemy awareness: where every awake enemy is heading (velocity from successive positions), whether it has
    /// noticed us, and a safety score for any spot that looks at all enemies at once, not only the nearest.
    /// </summary>
    public partial class Brain
    {
        class Track { public Vector3 pos, vel; public float t; }
        readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        float nextTrack;

        // Players who hurt us: we only ever run from them, never hit back.
        readonly Dictionary<int, float> hostile = new Dictionary<int, float>();
        List<PlayerAvatar> others = new List<PlayerAvatar>();

        void RefreshOthers()
        {
            PlayerAvatar me = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            others = FindObjectsOfType<PlayerAvatar>().Where(a => a != null && a != me && a.gameObject.activeInHierarchy && !R.Get<bool>(a, "deadSet")).ToList();
        }

        readonly List<float> hitTimes = new List<float>();

        /// <summary>
        /// Blame a teammate only for real attacks: two clear hits within 8 s while one stands right next to us and
        /// no monster was around. One scratch (a thrown crate, a fall) must never turn friends into enemies.
        /// </summary>
        void NoteAttacker(Vector3 p, int damage)
        {
            if (damage < 8) return;
            hitTimes.RemoveAll(t => Time.time - t > 8f);
            hitTimes.Add(Time.time);
            if (hitTimes.Count < 2) return;
            foreach (PlayerAvatar a in others)
            {
                if (a == null || Vector3.Distance(a.transform.position, p) > 4f) continue;
                if (!hostile.ContainsKey(a.GetInstanceID()) || Time.time > hostile[a.GetInstanceID()])
                    Plugin.Log.LogInfo("a player keeps hurting us: running away from " + R.Get<string>(a, "playerName", "?") + " for a while");
                hostile[a.GetInstanceID()] = Time.time + 20f;
            }
        }

        IEnumerable<Vector3> HostilePlayers()
        {
            foreach (PlayerAvatar a in others)
            {
                float until;
                if (a != null && hostile.TryGetValue(a.GetInstanceID(), out until) && Time.time < until) yield return a.transform.position;
            }
        }
        bool forceSprint, calmMove, sprintLatch;

        /// <summary>
        /// Sprint with a reserve: start only with a good tank, stop before it runs dry so the energy is there
        /// when an enemy shows up. While fleeing the reserve is spent more freely.
        /// </summary>
        bool SprintWise(PlayerController pc, bool want)
        {
            float max = Mathf.Max(R.Get<float>(pc, "EnergyStart", 40f), 1f);
            float en = R.Get<float>(pc, "EnergyCurrent");
            if (sprintLatch) { if (en < max * (forceSprint ? 0.05f : 0.3f)) sprintLatch = false; }
            else if (en > max * (forceSprint ? 0.25f : 0.7f)) sprintLatch = true;
            return want && sprintLatch;
        }
        float fleeCheckT, fleeCheckMoved;
        Vector3 fleeCheckPos;

        /// <summary>Update every enemy's velocity (called each frame, work is done a few times per second).</summary>
        void UpdateTracks()
        {
            if (Time.time < nextTrack) return;
            nextTrack = Time.time + 0.15f;
            foreach (EnemyParent e in enemies)
            {
                if (e == null) continue;
                int id = e.GetInstanceID();
                Vector3 pos = EnemyPos(e);
                Track tr;
                if (!tracks.TryGetValue(id, out tr)) { tracks[id] = new Track { pos = pos, t = Time.time }; continue; }
                float dt = Time.time - tr.t;
                if (dt < 0.05f) continue;
                Vector3 v = (pos - tr.pos) / dt;
                if (v.magnitude > 30f) v = Vector3.zero;   // it teleported or spawned somewhere else
                tr.vel = Vector3.Lerp(tr.vel, v, 0.5f);
                tr.pos = pos; tr.t = Time.time;
            }
        }

        Vector3 EnemyVel(EnemyParent e)
        {
            Track tr;
            return e != null && tracks.TryGetValue(e.GetInstanceID(), out tr) && Time.time - tr.t < 1f ? tr.vel : Vector3.zero;
        }

        /// <summary>The enemy has noticed us (or is looking for us) - it is not just wandering.</summary>
        static bool Hunting(EnemyParent e)
        {
            string s = EnemyStateName(e);
            return s == "Chase" || s == "ChaseBegin" || s == "ChaseSlow" || s == "Investigate" || s == "LookUnder" || s == "Sneak";
        }

        static bool Ranged(string name) { return name == "Huntsman"; }

        static bool CanSee(Vector3 from, Vector3 to)
        {
            return !Physics.Linecast(from + Vector3.up * 0.5f, to + Vector3.up * 1.2f, 1 << LayerMask.NameToLayer("Default"));
        }

        /// <summary>Distance to an enemy made smaller by what makes it more dangerous: it hunts us, it comes our way, it killed us before.</summary>
        float EffectiveDistance(EnemyParent e, Vector3 p)
        {
            Vector3 ep = EnemyPos(e);
            float d = Vector3.Distance(p, ep) - Boost(R.Get<string>(e, "enemyName"));
            if (Hunting(e)) d -= 5f;
            // a shooter kills from across the room: whenever it can see us it counts as much closer
            if (Ranged(R.Get<string>(e, "enemyName")) && d < 40f && CanSee(ep, p)) d -= 14f;
            Vector3 to = p - ep; to.y = 0f;
            if (to.sqrMagnitude > 0.01f)
            {
                float closing = Vector3.Dot(EnemyVel(e), to.normalized);   // m/s toward us
                if (closing > 0.5f) d -= Mathf.Min(closing, 8f) * 1.5f;
            }
            return d;
        }

        /// <summary>How safe a spot is: distance to the closest awake enemy, looking 1.5 s ahead along its motion.</summary>
        float SafetyAt(Vector3 c)
        {
            float best = float.MaxValue;
            foreach (EnemyParent e in enemies)
            {
                if (e == null || !Dangerous(e)) continue;
                Vector3 ahead = EnemyPos(e) + EnemyVel(e) * 1.5f;
                float d = Mathf.Min(Vector3.Distance(c, EnemyPos(e)), Vector3.Distance(c, ahead)) - Boost(R.Get<string>(e, "enemyName"));
                if (d < best) best = d;
            }
            foreach (Vector3 hp in HostilePlayers()) best = Mathf.Min(best, Vector3.Distance(c, hp) - 4f);
            return best;
        }

        /// <summary>True if the walking path comes within `margin` of any awake enemy.</summary>
        bool PathNearEnemy(Vector3[] c, float margin)
        {
            if (c == null) return false;
            for (int i = 1; i < c.Length; i++)
            {
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(c[i - 1], c[i]) / 1.5f));
                for (int k = 1; k <= n; k++)
                {
                    Vector3 q = Vector3.Lerp(c[i - 1], c[i], (float)k / n);
                    foreach (EnemyParent e in enemies)
                    {
                        if (e == null || !Dangerous(e)) continue;
                        if ((q - EnemyPos(e)).sqrMagnitude < margin * margin) return true;
                    }
                    foreach (Vector3 hp in HostilePlayers()) if ((q - hp).sqrMagnitude < margin * margin) return true;
                }
            }
            return false;
        }

        /// <summary>No awake enemy within `r` of this spot.</summary>
        bool SpotClear(Vector3 spot, float r)
        {
            foreach (EnemyParent e in enemies)
                if (e != null && Dangerous(e) && Vector3.Distance(spot, EnemyPos(e)) < r) return false;
            foreach (Vector3 hp in HostilePlayers()) if (Vector3.Distance(spot, hp) < r) return false;
            return true;
        }

        /// <summary>A spot where the closest enemies cannot see us (something solid in between).</summary>
        bool HiddenFromEnemies(Vector3 spot)
        {
            foreach (EnemyParent e in enemies)
            {
                if (e == null || !Dangerous(e)) continue;
                Vector3 ep = EnemyPos(e);
                if (Vector3.Distance(spot, ep) > 25f) continue;
                if (!Physics.Linecast(ep, spot + Vector3.up * 1.2f, 1 << LayerMask.NameToLayer("Default"))) return false;
            }
            return true;
        }

        /// <summary>
        /// Pick the best place to run to: clearly safer, or at least out of sight of the enemies. The second pass
        /// takes anything that does not run through an enemy, so a dead end never leaves us standing still.
        /// </summary>
        bool PickFleeGoal(Vector3 p, out Vector3 goal)
        {
            goal = p;
            float cur = SafetyAt(p);
            bool hiddenNow = HiddenFromEnemies(p);
            Vector3 where; float d0;
            NearestDanger(p, out where, out d0);
            Vector3 away = p - where; away.y = 0f;
            away = away.sqrMagnitude < 0.01f ? Vector3.forward : away.normalized;
            for (int pass = 0; pass < 2; pass++)
            {
                float bestScore = float.MinValue;
                bool found = false;
                foreach (float reach in new[] { 25f, 16f, 9f, 5f })
                    for (int a = -170; a <= 180; a += 20)
                    {
                        Vector3 cand = p + Quaternion.AngleAxis(a, Vector3.up) * away * reach;
                        NavMeshHit h; float len;
                        if (!NavMesh.SamplePosition(cand, out h, 5f, NavMesh.AllAreas) || !PathLength(p, h.position, out len)) continue;
                        if (PathNearEnemy(lastCorners, 3.0f)) continue;    // never run through an enemy
                        if (len > reach * 2.5f) continue;                  // a long detour is a trap
                        float safety = SafetyAt(h.position);
                        bool hidden = HiddenFromEnemies(h.position);
                        if (pass == 0 && safety < cur + 2f && !(hidden && !hiddenNow)) continue;
                        float score = Mathf.Min(safety, 30f) - len * 0.2f + (hidden ? 12f : 0f);
                        if (haveHome && Vector3.Distance(h.position, home) < 12f) score += 2f;
                        if (score > bestScore) { bestScore = score; goal = h.position; found = true; }
                    }
                if (found) return true;
            }
            return false;
        }

        /// <summary>Our moves while running: no progress for a second means something is blocking us.</summary>
        bool FleeStuck(Vector3 p)
        {
            if (Time.time - fleeCheckT < 1.0f) return false;
            bool stuck = (p - fleeCheckPos).magnitude < 0.6f;
            fleeCheckT = Time.time; fleeCheckPos = p;
            return stuck;
        }
    }
}
