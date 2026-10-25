using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RepoBot
{
    /// <summary>
    /// The "thinking" part: remember where awake enemies were for a while and never plan a trip through those
    /// places, rank targets by the real walking trip (to the object and then to where it is delivered) instead of
    /// straight-line distance, and fetch dead teammates' heads to the truck so they come back to life.
    /// </summary>
    public partial class Brain
    {
        sealed class Zone { public string name; public Vector3 pos; public float radius, until; }
        sealed class Cand { public Component c; public float value, rough; public bool isHead; }

        readonly List<Zone> zones = new List<Zone>();
        List<PlayerDeathHead> heads = new List<PlayerDeathHead>();
        Vector3[] lastCorners = new Vector3[0];     // corners of the last path computed by PathLength

        // ---- enemy memory ------------------------------------------------------------------------
        /// <summary>Keep a "no go" circle around every awake enemy for 20 s after we last saw it.</summary>
        void UpdateZones()
        {
            float now = Time.time;
            zones.RemoveAll(z => z.until < now);
            foreach (EnemyParent e in enemies)
            {
                if (e == null || !Dangerous(e)) continue;
                Vector3 pos = EnemyPos(e);
                string name = R.Get<string>(e, "enemyName", "?");
                Zone z = null;
                foreach (Zone q in zones) if (q.name == name && (q.pos - pos).sqrMagnitude < 36f) { z = q; break; }
                if (z == null) { z = new Zone(); z.name = name; zones.Add(z); }
                z.pos = pos; z.radius = Mathf.Min(FleeRadius * 1.3f + Boost(name), 18f); z.until = now + 20f;
            }
        }

        /// <summary>Is this spot inside (or within 'extra' metres of) a place where an awake enemy was seen lately?</summary>
        bool NearZone(Vector3 pos, float extra)
        {
            float k = ZoneScale();
            foreach (Zone z in zones) if ((pos - z.pos).magnitude < z.radius * k + extra) return true;
            return false;
        }

        // If everything has been behind an enemy for a while, accept to pass closer instead of waiting forever.
        float blockedSince;
        float ZoneScale()
        {
            if (blockedSince == 0f) return 1f;
            float waited = Time.time - blockedSince;
            return waited > 40f ? 0.35f : (waited > 20f ? 0.6f : 1f);
        }

        bool PathCrossesDanger(Vector3[] c)
        {
            if (zones.Count == 0 || c == null) return false;
            float scale = ZoneScale();
            for (int i = 1; i < c.Length; i++)
            {
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(c[i - 1], c[i]) / 2.5f));
                for (int k = 0; k <= n; k++)
                {
                    Vector3 q = Vector3.Lerp(c[i - 1], c[i], (float)k / n);
                    foreach (Zone z in zones) { float r = z.radius * scale; if ((q - z.pos).sqrMagnitude < r * r) return true; }
                }
            }
            return false;
        }

        // ---- dead teammates -----------------------------------------------------------------------
        void RefreshHeads()
        {
            PlayerAvatar me = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            heads = FindObjectsOfType<PlayerDeathHead>().Where(h =>
            {
                if (h == null || !h.gameObject.activeInHierarchy) return false;
                PlayerAvatar av = R.Get<PlayerAvatar>(h, "playerAvatar");
                return av != null && av != me && R.Get<bool>(av, "deadSet") && !R.Get<bool>(h, "inTruck");
            }).ToList();
        }

        static bool IsHead(Component c) { return c is PlayerDeathHead; }

        /// <summary>Carry a dead teammate's head to the truck; the game revives players whose head is in the truck.</summary>
        void DoCarryHead(Vector3 p, PhysGrabber pg)
        {
            bool inTruck = R.Get<bool>(target, "inTruck") || (haveHome && (target.transform.position - home).magnitude < 2.5f);
            if (!HoldingTarget(pg))
            {
                if (inTruck) { Go(S.Deposit, "head dropped in the truck"); return; }
                if (++drops > 4) { Blacklist(target, 180f); target = null; Go(S.Pick, "keeps dropping the head"); }
                else { hasViewpoint = false; Go(S.ToValuable, "dropped the head, regrab " + drops); }
                return;
            }
            Held.Add(InputKey.Grab);
            if (inTruck) { Held.Remove(InputKey.Grab); Go(S.Deposit, "the head is in the truck"); return; }
            if (haveHome && TeleportHeldObjectTo(home + Vector3.up * 0.8f)) return;   // superpower
            status = "carrying a dead teammate to the truck";
            Follow(home, 0.8f, false, 150f, 15f);
        }

        // ---- choosing a target ---------------------------------------------------------------------
        bool Eligible(Vector3 pos, ExtractionPoint depEp, float maxRange, float safeDist, bool ignoreRange)
        {
            Vector3 w; float d;
            if (NearestDanger(pos, out w, out d) && d < safeDist) return false;
            if (!ignoreRange && depEp != null && Vector3.Distance(pos, depEp.transform.position) > maxRange) return false;
            return true;
        }

        // ---- last resort when nothing has moved for a minute (singleplayer only) -------------------------------------
        float lastProgressT, lastHaulSum;
        readonly Dictionary<int, string> epPrevState = new Dictionary<int, string>();
        readonly Dictionary<int, int> epPrevHaul = new Dictionary<int, int>();

        /// <summary>Note when the haul or the delivery count moved, so we notice when nothing happens for a long time.</summary>
        void TrackProgress()
        {
            // an extraction that gets cancelled costs a lot of time: write down what the bot was doing when it happens
            foreach (ExtractionPoint ep in eps)
            {
                if (ep == null) continue;
                int id = ep.GetInstanceID();
                string st = EpState(ep);
                int haulNow = R.Get<int>(ep, "haulCurrent");
                string prev; int prevHaul;
                if (st == "Cancel" && epPrevState.TryGetValue(id, out prev) && prev != "Cancel")
                {
                    epPrevHaul.TryGetValue(id, out prevHaul);
                    Plugin.Log.LogWarning("EXTRACTION CANCELLED (was " + prev + ", haul " + prevHaul + " -> " + haulNow + ") at " + V(ep.transform.position) +
                                          "; bot " + state + " / " + status + " at " + V(Local() != null ? Local().transform.position : Vector3.zero));
                    foreach (string line in trace.Skip(Math.Max(0, trace.Count - 6))) Plugin.Log.LogWarning("   " + line);
                }
                epPrevState[id] = st; epPrevHaul[id] = haulNow;
            }
            float sum = 0f;
            foreach (ExtractionPoint ep in eps) if (ep != null) sum += R.Get<int>(ep, "haulCurrent");
            if (lastProgressT == 0f || sum > lastHaulSum + 1f) lastProgressT = Time.time;
            lastHaulSum = sum;
        }

        /// <summary>Alone in the game, stalled for 60 s and with a working object teleport: send things to the extraction without fetching them.</summary>
        bool remoteLatched;   // once triggered, stays on until the level changes
        bool RemoteMode
        {
            get
            {
                if (SemiFunc.IsMultiplayer() || !superpowers || !objectTpOk) return false;
                if (!remoteLatched && lastProgressT > 0f && Time.time - lastProgressT > 60f) remoteLatched = true;
                return remoteLatched;
            }
        }

        // when we cannot get to / grab a valuable, alone and with working powers, send it to the extraction instead of giving up on it
        readonly Dictionary<int, float> remoteRecent = new Dictionary<int, float>();

        bool SendRemote(ValuableObject v, ExtractionPoint depEp, string why)
        {
            PhysGrabObject pgo = R.Get<PhysGrabObject>(v, "physGrabObject");
            if (pgo == null) return false;
            pgo.Teleport(AreaDrop(depEp), v.transform.rotation);
            Rigidbody rrb = R.Get<Rigidbody>(pgo, "rb");
            if (rrb != null) { rrb.velocity = Vector3.zero; rrb.angularVelocity = Vector3.zero; }
            remoteRecent[v.GetInstanceID()] = Time.time + 25f;   // give it time to land and be counted
            delivered++;
            Plugin.Log.LogInfo("remote delivery (" + why + "): " + v.name + " worth " + R.Get<float>(v, "dollarValueCurrent").ToString("0"));
            return true;
        }

        /// <summary>Give up on the current target: send it straight to the extraction if we can, else just put it aside for a while.</summary>
        void GiveUp(float seconds)
        {
            ValuableObject v = target as ValuableObject;
            if (v != null && !SemiFunc.IsMultiplayer() && superpowers && objectTpOk)
            {
                ExtractionPoint depEp = DepositEp(Local() != null ? Local().transform.position : Vector3.zero);
                if (depEp != null && SendRemote(v, depEp, "could not reach it")) { target = null; nextPick = Time.time + 1.2f; return; }
            }
            Blacklist(target, seconds);
            target = null;
        }

        bool RemoteDeliver(Vector3 p)
        {
            ExtractionPoint depEp = DepositEp(p);
            if (depEp == null) return false;
            ValuableObject best = null; float bestValue = 0f;
            foreach (ValuableObject v in vals)
            {
                float until;
                if (v == null || (remoteRecent.TryGetValue(v.GetInstanceID(), out until) && Time.time < until)) continue;
                if (R.Get<bool>(R.Get<RoomVolumeCheck>(v, "roomVolumeCheck"), "inExtractionPoint")) continue;
                float value = Mathf.Max(R.Get<float>(v, "dollarValueCurrent"), 50f);
                if (value > bestValue) { bestValue = value; best = v; }
            }
            if (best == null) return false;
            if (!SendRemote(best, depEp, "stalled for " + (Time.time - lastProgressT).ToString("0") + " s")) { Blacklist(best, 300f); return true; }
            SayOnce("remote" + lastLevel, "Niente da fare con i nemici in giro: mando gli oggetti direttamente nell'estrazione!");
            nextPick = Time.time + 1.2f;
            status = "sending objects straight to the extraction";
            return true;
        }

        void ChooseTarget(Vector3 p)
        {
            if (RemoteMode && RemoteDeliver(p)) return;
            ExtractionPoint depEp = DepositEp(p);
            if (depEp == null && !ShopMode && eps.Any(e => e != null && EpState(e) == "Idle" && !R.Get<bool>(e, "isLocked")) && heads.Count == 0)
            {
                status = "no extraction point is active yet: not picking anything up";
                return;
            }
            float hpNow = Hp();
            // the weaker we are, the closer to the extraction point we stay and the more room we give awake enemies
            float maxRange = hpNow >= 80 ? float.MaxValue : (hpNow >= 50 ? 60f : 30f);
            float safeDist = hpNow < 50 ? FleeRadius * 2.2f : FleeRadius * 1.5f;
            var cands = new List<Cand>();

            foreach (PlayerDeathHead h in heads)
            {
                if (h == null || IsBlacklisted(h)) continue;
                Vector3 hpos = h.transform.position;
                if (!Eligible(hpos, depEp, maxRange, safeDist, true)) continue;
                cands.Add(new Cand { c = h, isHead = true, value = 100000f, rough = 100000f / (Vector3.Distance(p, hpos) + 10f) });
            }
            foreach (CosmeticWorldObject cw in cosmeticObjs)
            {
                // collectible cosmetics: delivered to an extraction point they become tokens for the cosmetic machine
                if (cw == null || IsBlacklisted(cw) || R.Get<bool>(cw, "inExtraction")) continue;
                Vector3 cpos = cw.transform.position;
                if (InAnyArea(cpos) || !Eligible(cpos, depEp, maxRange, safeDist, false)) continue;
                int rarity = Convert.ToInt32(R.Get<object>(cw, "rarity") ?? 0);
                float cvalue = 3000f + 2000f * rarity;
                cands.Add(new Cand { c = cw, value = cvalue, rough = cvalue / (Vector3.Distance(p, cpos) + 10f) });
            }
            foreach (ValuableObject v in vals)
            {
                if (v == null || IsBlacklisted(v)) continue;
                Rigidbody rb = R.Get<Rigidbody>(v, "rb");
                if ((rb != null ? rb.mass : 1f) > MaxMass) continue;
                Vector3 vpos = v.transform.position;
                // already counted by the game: leave it alone (the game's own flag beats my geometry at the volume's edge)
                if (R.Get<bool>(R.Get<RoomVolumeCheck>(v, "roomVolumeCheck"), "inExtractionPoint")) continue;
                if (InAnyArea(vpos) || !Eligible(vpos, depEp, maxRange, safeDist, false)) continue;
                float value = Mathf.Max(R.Get<float>(v, "dollarValueCurrent"), 50f);
                if (vpos.y > 1.9f) value *= 0.4f;   // high shelves are awkward to reach
                cands.Add(new Cand { c = v, value = value, rough = value / (Vector3.Distance(p, vpos) + 10f) });
            }
            cands.Sort((a, b) => b.rough.CompareTo(a.rough));

            // look at the real trip (to the object, then to where it is handed in) of the best few
            Cand best = null;
            float bestScore = -1f, bestLen = 0f;
            int tries = 0, blocked = 0;
            foreach (Cand k in cands)
            {
                if (++tries > 8) break;
                Vector3 pos = k.c.transform.position;
                float l1, l2 = 0f;
                if (!PathLength(p, pos, out l1)) { Blacklist(k.c, 120f); continue; }
                // with teleporting we do not walk the path, so only a walking bot has to avoid paths through enemy zones
                bool walking = !PlayerTp;
                if (walking && PathCrossesDanger(lastCorners)) { blocked++; continue; }
                if (k.isHead) PathLength(pos, home, out l2);
                else if (depEp != null && PathLength(pos, AreaDrop(depEp, false), out l2) && walking && PathCrossesDanger(lastCorners)) { blocked++; continue; }
                float score = k.value / ((l1 + l2) / 4f + 6f);   // value per second of work, walking at about 4 m/s
                if (score > bestScore) { bestScore = score; best = k; bestLen = l1; }
            }

            if (best != null)
            {
                blockedSince = 0f;
                target = best.c; grabFails = drops = depositTries = 0; hasViewpoint = false; triedViews.Clear();
                if (best.isHead) SayOnce("head" + best.c.GetInstanceID(), "Vado a prendere un compagno morto: lo porto nel camion per rianimarlo!");
                Go(S.ToValuable, "target " + target.name + " path " + bestLen.ToString("0") + "m, worth " + best.value.ToString("0"));
                return;
            }
            if (cands.Count > 0 && blocked > 0)
            {
                if (blockedSince == 0f) blockedSince = Time.time;
                status = "everything left is behind an enemy: waiting at the truck";
                readyToLeave = false;
                Go(S.Home, "all targets are behind awake enemies");
                return;
            }
            status = cands.Count == 0 ? "no valuables left (or all too heavy)" : "no reachable valuable";

            // nothing at all left outside the extraction: waiting would never help, move on after a moment
            if (cands.Count == 0 && NothingLeft())
            {
                if (noLeftSince == 0f) noLeftSince = Time.time;
                else if (Time.time - noLeftSince > 20f)
                {
                    readyToLeave = true;
                    SayOnce("nothingleft" + lastLevel, "Non ci sono più oggetti da portare: andiamo avanti!");
                    Go(S.Home, "no valuables left anywhere");
                }
            }
            else noLeftSince = 0f;
        }

        float noLeftSince;

        /// <summary>True when every valuable of the level is already counted in an extraction point (or gone).</summary>
        bool NothingLeft()
        {
            if (vals.Count == 0) return false;   // the level has not spawned its valuables yet: that is not "nothing left"
            foreach (ValuableObject v in vals)
                if (v != null && !R.Get<bool>(R.Get<RoomVolumeCheck>(v, "roomVolumeCheck"), "inExtractionPoint")) return false;
            return true;
        }
    }
}
