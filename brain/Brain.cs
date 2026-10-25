using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace RepoBot
{
    /// <summary>
    /// Plays a level on its own: pick a valuable, walk to a spot with a clear view of it over the NavMesh,
    /// grab it, carry it to the extraction point, drop it in, repeat; press the extraction button once the
    /// haul goal is met. Reads the game's real state (no screenshots) and writes it to run/state.json.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public partial class Brain : MonoBehaviour
    {
        const string Dir = @"Z:\home\ale\repo-bot\run\";   // Wine path of /home/ale/repo-bot/run
        const float MaxMass = 12f;       // heavier valuables are skipped (a lone player cannot carry them)
        const float GrabDistance = 2.3f; // start grabbing when the camera is this close to the object

        const float FleeRadius = 9f;     // an awake enemy closer than this makes us run (x1.4 when health is low)

        enum S { Pick, ToValuable, Grab, Carry, Deposit, PressButton, Flee, Home, Machine, PayShop, Advance }

        static HashSet<InputKey> Held { get { return BotInput.Held; } }

        // ---- plan state ----
        S state = S.Pick;
        string status = "off";
        Component target;                // a ValuableObject, a CosmeticWorldObject or (in the shop) an ItemAttributes
        ExtractionPoint pressEp;
        string pressFrom = "";           // extraction state we are trying to change by pressing its button
        Vector3 home, fleeGoal;          // home = where the level started (the truck)
        bool haveHome;
        float nextFleePick, noPressUntil;
        float stateT, nextPick, nextRefresh, nextState, lookT, logT;
        int delivered, grabFails, drops;
        string lastLevel = "";
        Vector3 viewpoint;
        bool hasViewpoint;
        readonly List<Vector3> triedViews = new List<Vector3>();
        readonly Dictionary<int, float> blacklist = new Dictionary<int, float>();
        List<ValuableObject> vals = new List<ValuableObject>();
        List<CosmeticWorldObject> cosmeticObjs = new List<CosmeticWorldObject>();
        List<ExtractionPoint> eps = new List<ExtractionPoint>();
        List<EnemyParent> enemies = new List<EnemyParent>();

        // ---- navigation ----
        readonly NavMeshPath navPath = new NavMeshPath();
        readonly NavMeshPath navTmp = new NavMeshPath();
        Vector3[] corners = new Vector3[0];
        int ci;
        Vector3 pathGoal = new Vector3(1e9f, 0, 0);
        float nextRepath, stuckT, jumpUntil, strafeUntil, strafeDir;
        Vector3 stuckPos;
        int stuckCount;
        Transform cam;

        // =====================================================================================
        void Update()
        {
            PollCommands();
            if (Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame) SetActive(!BotInput.Active);
            // C switches the cheats (teleporting) on and off; they start off so the bot plays like a person
            if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame)
            {
                superpowers = !superpowers;
                Plugin.Log.LogInfo("cheats (teleport) " + (superpowers ? "ON" : "OFF"));
            }

            BotInput.BeginFrame();
            bool inLevel = InLevel();
            TrackLevel();
            WatchDeath();
            WatchHealth();
            WatchPosition();
            VerifyTeleports();
            WatchLobby();
            UseOwnedUpgrades();
            if (BotInput.Active && inLevel)
            {
                try { Tick(); }
                catch (Exception e) { status = "error: " + e.GetType().Name; LogEvery(3f, "tick: " + e); }
            }
            else status = !BotInput.Active ? "off" : "waiting for a level";

            if (Time.time < jumpUntil) Held.Add(InputKey.Jump);
            if (Time.time < strafeUntil) BotInput.Move.x = strafeDir;
            BotInput.EndFrame();

            if (Time.time >= nextState) { nextState = Time.time + 0.5f; WriteState(inLevel); }
        }

        void OnDestroy() { BotInput.Reset(); }

        void SetActive(bool on)
        {
            BotInput.Active = on;
            if (on) ResetPlan();
            Plugin.Log.LogInfo("bot " + (on ? "ON" : "OFF"));
        }

        void ResetPlan()
        {
            state = S.Pick; target = null; pressEp = null; haveHome = false;
            ResetShop();
            zones.Clear(); readyToLeave = false; allInTruckSince = 0f; homeFail = 0;
            grabFails = drops = 0; hasViewpoint = false;
            triedViews.Clear(); blacklist.Clear();
        }

        void LogEvery(float seconds, string msg)
        {
            if (Time.time - logT < seconds) return;
            logT = Time.time;
            Plugin.Log.LogWarning(msg.Length > 600 ? msg.Substring(0, 600) : msg);
        }

        static bool InLevel()
        {
            if (!SemiFunc.RunIsLevel() && !SemiFunc.RunIsShop()) return false;
            LevelGenerator lg = R.GetStatic<LevelGenerator>(typeof(LevelGenerator), "Instance");
            return lg != null && R.Get<bool>(lg, "Generated") && Local() != null;
        }

        void TrackLevel()
        {
            RunManager rm = R.GetStatic<RunManager>(typeof(RunManager), "instance");
            Level lv = R.Get<Level>(rm, "levelCurrent");
            string name = lv != null ? lv.name : "?";
            if (name == lastLevel) return;
            Plugin.Log.LogInfo("level: '" + lastLevel + "' -> '" + name + "' (levelsCompleted " + R.Get<int>(rm, "levelsCompleted") + ")");
            lastLevel = name;
            shopDelivered = 0;
            lastProgressT = 0f; lastHaulSum = 0f; remoteLatched = false; noLeftSince = 0f;
            playerTpOk = objectTpOk = true; playerTpFails = objectTpFails = 0;   // test the powers again in every new level
            said.Clear();
            ResetPlan();
            if (BotInput.Active && name.StartsWith("Level - ") && !name.Contains("Menu") && !name.Contains("Splash") && !name.Contains("Lobby"))
                Say(name.Contains("Shop") ? "Eccomi al negozio!" : "Si parte! Cerco gli oggetti di valore.");
        }

        static PlayerController Local() { return R.GetStatic<PlayerController>(typeof(PlayerController), "instance"); }
        static PhysGrabber Grabber() { return R.GetStatic<PhysGrabber>(typeof(PhysGrabber), "instance"); }

        static int Hp()
        {
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            return R.Get<int>(R.Get<PlayerHealth>(pa, "playerHealth"), "health", -1);
        }

        /// <summary>ExtractionPoint has two State fields (stateSetTo, currentState); we want the current one.</summary>
        static string EpState(ExtractionPoint ep)
        {
            object s = R.Get<object>(ep, "currentState");
            return s == null ? "?" : s.ToString();
        }

        static bool Dead()
        {
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            return R.Get<bool>(pa, "deadSet") || R.Get<bool>(pa, "spectating");
        }

        Transform Cam()
        {
            if (cam == null) { Camera c = Camera.main; if (c != null) cam = c.transform; }
            return cam;
        }

        // =====================================================================================
        void Tick()
        {
            PlayerController pc = Local();
            Transform c = Cam();
            PhysGrabber pg = Grabber();
            if (pc == null || c == null || pg == null) { status = "no player/camera"; return; }
            if (Dead()) { status = "dead / spectating"; HeadStep(); return; }
            Vector3 p = pc.transform.position;
            if (Time.time >= nextRefresh) { Refresh(); TrackProgress(); }
            UpdateTracks();
            if (!haveHome)
            {
                // The truck. Remembered per level in run/homes.txt so a brain reload in mid-level does not take
                // wherever we happen to stand for "home". Else the truck's safe spot, else where we are at the start.
                RunManager hrm = R.GetStatic<RunManager>(typeof(RunManager), "instance");
                string hk = SessionId() + "|" + lastLevel + "|" + R.Get<int>(hrm, "levelsCompleted");
                Vector3 saved;
                TruckSafetySpawnPoint sp = R.GetStatic<TruckSafetySpawnPoint>(typeof(TruckSafetySpawnPoint), "instance");
                if (TryLoadHome(hk, out saved)) home = saved;
                else
                {
                    home = sp != null ? sp.transform.position : p;
                    if (sp != null || Time.timeSinceLevelLoad < 40f) SaveHome(hk, home);
                }
                haveHome = true;
            }

            Vector3 threat; float threatDist;
            float radius = Hp() <= 30 ? FleeRadius * 1.4f : FleeRadius;
            if (state != S.Flee && NearestDanger(p, out threat, out threatDist) && threatDist < radius)
            {
                fleeGoal = p; nextFleePick = 0f;
                Go(S.Flee, EnemyLabel(threat) + " " + threatDist.ToString("0") + "m away");
            }

            switch (state)
            {
                case S.Flee: DoFlee(p); break;
                case S.Home: DoHome(); break;
                case S.Machine: DoMachine(p); break;
                case S.PayShop: DoPayShop(p); break;
                case S.Advance: DoAdvance(p); break;
                case S.Pick: DoPick(p); break;
                case S.ToValuable: DoToValuable(p, pg); break;
                case S.Grab: DoGrab(p, pg); break;
                case S.Carry: DoCarry(p, pg); break;
                case S.Deposit: DoDeposit(); break;
                case S.PressButton: DoPressButton(p); break;
            }
        }

        void Go(S s, string why)
        {
            if (s != state) Plugin.Log.LogInfo("state " + state + " -> " + s + " (" + why + ")");
            state = s;
            stateT = Time.time;
            lookT = 0f;
        }

        void Refresh()
        {
            nextRefresh = Time.time + 0.5f;
            vals = FindObjectsOfType<ValuableObject>().Where(v => v != null && v.gameObject.activeInHierarchy).ToList();
            cosmeticObjs = FindObjectsOfType<CosmeticWorldObject>().Where(v => v != null && v.gameObject.activeInHierarchy).ToList();
            eps = FindObjectsOfType<ExtractionPoint>().Where(e => e != null && !R.Get<bool>(e, "isShop")).ToList();
            RefreshOthers();
            enemies = FindObjectsOfType<EnemyParent>().Where(e => e != null && R.Get<bool>(e, "Spawned") && EnemyOf(e) != null).ToList();
            UpdateZones();
            UpdateHostileZones();
            RefreshHeads();
        }

        // ---- enemies ---------------------------------------------------------------------------
        static Enemy EnemyOf(EnemyParent e) { return R.Get<Enemy>(e, "Enemy"); }

        static Vector3 EnemyPos(EnemyParent e)
        {
            Enemy en = EnemyOf(e);
            if (en == null) return e.transform.position;
            Transform ct = R.Get<Transform>(en, "CenterTransform");
            return ct != null ? ct.position : en.transform.position;
        }

        static string EnemyStateName(EnemyParent e)
        {
            object s = R.Get<object>(EnemyOf(e), "CurrentState");
            return s == null ? "?" : s.ToString();
        }

        /// <summary>An enemy that is awake and could hurt us (not despawned, still spawning or stunned).</summary>
        static bool Dangerous(EnemyParent e)
        {
            string s = EnemyStateName(e);
            return s != "Despawn" && s != "None" && s != "Spawn" && s != "Stunned" && s != "?";
        }

        bool NearestDanger(Vector3 p, out Vector3 where, out float dist)
        {
            where = Vector3.zero; dist = float.MaxValue;
            foreach (EnemyParent e in enemies)
            {
                if (e == null || !Dangerous(e)) continue;
                Vector3 ep = EnemyPos(e);
                // enemies that already killed us, hunt us or run at us count as closer than they are
                float d = EffectiveDistance(e, p);
                if (d < dist) { dist = d; where = ep; }
            }
            foreach (Vector3 hp in HostilePlayers())
            {
                float hd = Vector3.Distance(p, hp) - 1f;
                if (hd < dist) { dist = hd; where = hp; }
            }
            // an extraction point that is warning/extracting slams its tube down on whoever stands under it: treat it as a hazard
            foreach (ExtractionPoint xp in eps)
            {
                if (xp == null) continue;
                string xs = EpState(xp);
                if (xs != "Warning" && xs != "Extracting") continue;
                Vector3 xpos = xp.transform.position;
                float xd = Vector3.Distance(p, xpos) - 4f;   // the tube's hurt zone is wide: count it as closer
                if (xd < dist) { dist = xd; where = xpos; }
            }
            return dist < float.MaxValue;
        }

        string EnemyLabel(Vector3 pos)
        {
            foreach (EnemyParent e in enemies)
                if (e != null && (EnemyPos(e) - pos).sqrMagnitude < 0.01f) return R.Get<string>(e, "enemyName", "enemy") + " (" + EnemyStateName(e) + ")";
            return "enemy";
        }

        void DoFlee(Vector3 p)
        {
            Held.Remove(InputKey.Grab);
            Vector3 where; float dist;
            bool danger = NearestDanger(p, out where, out dist);
            float radius = (Hp() <= 30 ? FleeRadius * 1.4f : FleeRadius) * 1.4f;
            if (!danger || dist > radius)
            {
                status = "enemy far, catching breath";
                if (Time.time - stateT > 2.5f) { target = null; hasViewpoint = false; noPressUntil = Time.time + 20f; Go(S.Pick, "safe again"); }   // do not walk straight back to the button we ran from
                return;
            }
            // superpower: vanish to the truck when it is clear of every enemy
            if (haveHome && SpotClear(home, 15f) && TeleportPlayer(home, "escape from " + EnemyLabel(where))) return;
            forceSprint = true;
            if (FleeStuck(p))
            {
                // blocked by a wall or a door: change the plan right away
                nextFleePick = 0f; jumpUntil = Time.time + 0.3f; strafeUntil = Time.time + 0.5f;
                strafeDir = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            }
            if (Time.time >= nextFleePick)
            {
                nextFleePick = Time.time + 0.6f;
                Vector3 g;
                if (PickFleeGoal(p, out g)) fleeGoal = g;
                else
                {
                    Vector3 away = p - where; away.y = 0f;
                    fleeGoal = p + (away.sqrMagnitude < 0.01f ? Vector3.forward : away.normalized) * 10f;   // cornered: straight away
                }
            }
            status = "fleeing (" + dist.ToString("0") + "m) from " + EnemyLabel(where) + " me " + p.x.ToString("0") + "," + p.z.ToString("0") + " it " + where.x.ToString("0") + "," + where.z.ToString("0");
            bool arrived = Follow(fleeGoal, 1.0f, true, 540f, 45f);
            forceSprint = false;
            if (arrived) nextFleePick = 0f;   // arrived but the enemy is still near: pick a new spot at once
        }

        void DoHome()
        {
            Held.Remove(InputKey.Grab);
            Vector3 p = Local().transform.position;
            status = readyToLeave ? "all done: back to the truck" : "waiting at the truck for the enemies to move away";
            // superpower: the truck's spawn point is where the game itself puts players; it is not on the NavMesh, so go there exactly
            if (readyToLeave && Vector3.Distance(p, home) > 3f && TeleportExact(home, "into the truck")) return;
            bool arrived = Vector3.Distance(p, home) < (ShopMode || readyToLeave ? 4f : 2.5f);
            calmMove = readyToLeave;   // inside the truck: walk slowly, no sprint, no jumping
            if (!arrived && homeFail >= 2)
            {
                // the way into the truck is blocked and teleporting is not allowed here: do not jump around forever
                status = "cannot get into the truck: waiting for help";
                SayOnce("cantenter" + lastLevel, "Non riesco a entrare nel camion, mi serve una mano!");
                return;
            }
            if (!arrived) arrived = Follow(home, 1.5f, true, 540f, 35f);
            calmMove = false;
            if (!readyToLeave)
            {
                if (Time.time - stateT > 8f) Go(S.Pick, "try again");   // waiting out an enemy, not leaving
                return;
            }
            if (arrived) status = "waiting in the truck";
            if (arrived || !SemiFunc.IsMultiplayer()) CheckAdvance();   // BrainTruck.cs; alone there is nobody to wait for
        }

        // ---- choosing what to do -------------------------------------------------------------
        void DoPick(Vector3 p)
        {
            if (ShopMode) { DoShopPick(p); return; }

            // Extraction points move Idle -> Active -> (goal met) Surplus -> Extracting -> Complete.
            // We press the button to activate an Idle one (when nothing is accepting valuables) and to extract a full one.
            int depositable = 0, complete = 0;
            ExtractionPoint extract = null, activate = null;
            foreach (ExtractionPoint ep in eps)
            {
                string st = EpState(ep);
                int goal = R.Get<int>(ep, "haulGoal"), cur = R.Get<int>(ep, "haulCurrent");
                if (st == "Complete") complete++;
                else if (st == "Surplus" || (st == "Active" && goal > 0 && cur >= goal)) { if (extract == null) extract = ep; }
                else if (st == "Active") depositable++;
                else if (st == "Idle" && activate == null && !R.Get<bool>(ep, "isLocked")) activate = ep;
            }
            if (Time.time >= noPressUntil)
            {
                if (extract != null) { pressEp = extract; pressFrom = EpState(extract); Go(S.PressButton, "haul goal reached"); return; }
                if (depositable == 0 && activate != null) { pressEp = activate; pressFrom = "Idle"; Go(S.PressButton, "activate extraction point"); return; }
            }
            if (eps.Count > 0 && complete == eps.Count)
            {
                readyToLeave = true;
                SayOnce("done" + lastLevel, "Tutte le estrazioni sono complete! Torno al camion.");
                Go(S.Home, "all extraction points complete");
                return;
            }
            if (Time.time < nextPick) { status = "looking for a valuable"; return; }
            nextPick = Time.time + 1.0f;

            ChooseTarget(p);   // BrainSmart.cs
        }

        void DoToValuable(Vector3 p, PhysGrabber pg)
        {
            if (target == null) { Go(S.Pick, "target gone"); return; }
            if (Time.time - stateT > (PlayerTp ? 12f : 120f)) { GiveUp(180f); Go(S.Pick, "took too long"); return; }
            if (!IsHead(target) && TakenByOthers(target)) { GiveUp(25f); target = null; Go(S.Pick, "a teammate has it (holding it or in the cart): leaving it"); return; }
            Transform c = Cam();
            Vector3 tc = Center(target);
            float reach = Mathf.Min(GrabDistance, R.Get<float>(pg, "grabRange", 4f) * 0.7f);
            if (Vector3.Distance(c.position, tc) < reach && ClearLine(c.position, tc)) { Go(S.Grab, "in reach, clear view"); return; }

            if (!hasViewpoint)
            {
                Vector3 vp;
                viewpoint = ChooseViewpoint(p, tc, out vp) ? vp : tc;
                hasViewpoint = true;
            }
            status = "walking to " + target.name;
            if (Follow(viewpoint, 0.5f, true, 540f, 35f)) Go(S.Grab, "at viewpoint");
        }

        void DoGrab(Vector3 p, PhysGrabber pg)
        {
            if (target == null) { Go(S.Pick, "target gone"); return; }
            if (!IsHead(target) && !HoldingTarget(pg) && TakenByOthers(target)) { GiveUp(25f); target = null; Go(S.Pick, "a teammate has it: leaving it"); return; }
            Transform c = Cam();
            Vector3 tc = Center(target);
            float gr = R.Get<float>(pg, "grabRange", 4f);
            float dist = Vector3.Distance(c.position, tc);
            if (dist > gr * 0.85f)
            {
                // arrived at the viewpoint but still too far (e.g. object on a high shelf): close in on it
                LogEvery(1f, "too far to grab: dist " + dist.ToString("0.0") + " grabRange " + gr.ToString("0.0") + " obj " + V(tc) + " cam " + V(c.position));
                status = "closing in on " + target.name;
                Follow(tc, 0.4f, false, 540f, 35f);
                if (Time.time - stateT > 6f)
                {
                    triedViews.Add(p);
                    hasViewpoint = false;
                    if (++grabFails >= 4) { GiveUp(180f); Go(S.Pick, "cannot reach"); }
                    else Go(S.ToValuable, "cannot reach, retry " + grabFails);
                }
                return;
            }
            AimAt(tc, 540f);
            Held.Add(InputKey.Grab);
            status = "grabbing " + target.name;
            if (HoldingTarget(pg)) { Go(S.Carry, "grabbed"); return; }

            PhysGrabObject want = R.Get<PhysGrabObject>(target, "physGrabObject");
            bool looking = want != null && R.Get<PhysGrabObject>(pg, "currentlyLookingAtPhysGrabObject") == want;
            lookT = looking ? 0f : lookT + Time.deltaTime;
            if (lookT > 1.2f || Time.time - stateT > 4f)
            {
                // the crosshair never lands on it from here: remember this spot and try another one
                triedViews.Add(p);
                hasViewpoint = false;
                if (++grabFails >= 4) { GiveUp(180f); Go(S.Pick, "cannot grab"); }
                else Go(S.ToValuable, "no grab from here, retry " + grabFails);
            }
        }

        void DoCarry(Vector3 p, PhysGrabber pg)
        {
            if (target == null) { Go(S.Pick, "target gone"); return; }
            if (IsHead(target)) { DoCarryHead(p, pg); return; }   // a dead teammate: to the truck, not to the extraction point
            ExtractionPoint ep = DepositEp(p);
            Vector3 tp = target.transform.position;
            if (!HoldingTarget(pg))
            {
                Plugin.Log.LogInfo("lost the grip: object is " + Vector3.Distance(Cam().position, tp).ToString("0.0") + "m from the camera");
                if (ep != null && Delivered(ep, target)) { Go(S.Deposit, "dropped in area"); return; }
                if (++drops > 4) { GiveUp(180f); Go(S.Pick, "keeps dropping"); }
                else { hasViewpoint = false; Go(S.ToValuable, "dropped, regrab " + drops); }
                return;
            }
            Held.Add(InputKey.Grab);
            if (ep == null)
            {
                if (!ShopMode && eps.Any(e => e != null && EpState(e) == "Idle" && !R.Get<bool>(e, "isLocked")))
                {
                    Held.Remove(InputKey.Grab);
                    target = null;
                    Go(S.Pick, "no extraction point is active: put the object down and activate one first");
                    return;
                }
                if (!ShopMode && eps.Count > 0 && eps.All(e => e == null || EpState(e) == "Complete"))
                {
                    // every extraction point is done: nothing to deliver to, put it down and head for the truck
                    Held.Remove(InputKey.Grab);
                    target = null;
                    Go(S.Pick, "all extraction points are complete: dropping the object");
                    return;
                }
                status = "carrying, no extraction point is accepting valuables";
                return;
            }
            if (Delivered(ep, target)) { Held.Remove(InputKey.Grab); Go(S.Deposit, ShopMode ? "in the shop cart area" : "inside extraction area"); return; }
            if (TeleportHeldObject(ep)) return;   // superpower: straight into the extraction point
            status = "carrying " + target.name + (ShopMode ? " to the checkout" : " to extraction");
            // turn slowly and only walk once aligned, so the object does not swing out of our hands
            // gently: slower the closer we get, no jumping, no sprint, turns only when nearly aligned
            Vector3 dropAt = ShopMode ? ShopDrop(ep) : AreaDrop(ep, false);
            float left = Vector3.Distance(p, dropAt);
            moveScale = left < 4f ? 0.45f : 0.7f;
            calmMove = true;
            Follow(dropAt, 0.3f, false, 110f, 12f);
            calmMove = false; moveScale = 1f;
        }

        void DoDeposit()
        {
            Held.Remove(InputKey.Grab);
            status = "dropped in extraction area";
            if (Time.time - stateT < 1.5f) return;
            if (ShopMode && target != null && !InShoppingList(target) && ++depositTries <= 2)
            {
                Plugin.Log.LogInfo("shop: the item is not in the cart (cart cost " + CartCost() + "), picking it up again");
                hasViewpoint = false;
                Go(S.ToValuable, "not in the cart yet, retry " + depositTries);
                return;
            }
            // outside the shop the game's own flag says whether the thing really counts as being in the extraction point
            if (target != null && IsHead(target) && !R.Get<bool>(target, "inTruck") && ++depositTries <= 2)
            {
                Plugin.Log.LogInfo("the head is not in the truck yet, picking it up again");
                hasViewpoint = false;
                Go(S.ToValuable, "head not in the truck, retry " + depositTries);
                return;
            }
            if (target != null && IsHead(target)) SayOnce("revived" + target.GetInstanceID(), "Compagno riportato nel camion: dovrebbe tornare in vita!");
            if (!ShopMode && target != null && !IsHead(target) && !R.Get<bool>(R.Get<RoomVolumeCheck>(target, "roomVolumeCheck"), "inExtractionPoint") && ++depositTries <= 2)
            {
                Plugin.Log.LogInfo("extraction: the game does not count " + target.name + " as inside yet, picking it up again");
                hasViewpoint = false;
                Go(S.ToValuable, "not counted yet, retry " + depositTries);
                return;
            }
            delivered++;
            if (ShopMode) { shopDelivered++; if (target != null) NoteBought(target); }
            target = null;
            Go(S.Pick, "delivered #" + delivered);
        }

        void DoPressButton(Vector3 p)
        {
            if (pressEp == null) { Go(S.Pick, "no extraction point"); return; }
            string st = EpState(pressEp);
            if (st != pressFrom) { Go(S.Pick, "extraction " + pressFrom + " -> " + st); return; }
            MeshRenderer btn = R.Get<MeshRenderer>(pressEp, "button");
            Vector3 bp = btn != null ? btn.bounds.center : pressEp.transform.position;
            if ((NearZone(bp, 4f) || !SpotClear(bp, FleeRadius + 8f)) && Vector3.Distance(Cam().position, bp) > 6f)
            {
                // an awake enemy was seen around the button: do not walk into it, try again in a while
                noPressUntil = Time.time + 20f;
                Go(S.Pick, "an enemy is near the button, waiting");
                return;
            }
            if (Vector3.Distance(Cam().position, bp) > 2.2f)
            {
                status = "walking to button";
                if (!Follow(bp, 1.2f, true, 540f, 35f)) return;   // true = close enough on the floor plan: go on and press
            }
            AimAt(bp, 540f);
            // the button reacts to grab or to interact (we do not know which): alternate every 3 s
            Held.Add(((Time.time - stateT) % 6f) < 3f ? InputKey.Grab : InputKey.Interact);
            status = "pressing the extraction button (" + pressFrom + ")";
            if (Time.time - stateT > 25f) { noPressUntil = Time.time + 30f; Go(S.Pick, "button press timed out"); }
        }

        // ---- helpers about the world ---------------------------------------------------------
        bool HoldingTarget(PhysGrabber pg)
        {
            PhysGrabObject want = R.Get<PhysGrabObject>(target, "physGrabObject");
            return R.Get<bool>(pg, "grabbed") && want != null && R.Get<PhysGrabObject>(pg, "grabbedPhysGrabObject") == want;
        }

        static Vector3 Center(Component c)
        {
            Collider col = c.GetComponentInChildren<Collider>();
            return col != null ? col.bounds.center : c.transform.position;
        }

        static Bounds? AreaBounds(ExtractionPoint ep)
        {
            GameObject a = R.Get<GameObject>(ep, "extractionArea");
            Collider c = a != null ? a.GetComponentInChildren<Collider>() : null;
            float margin = 0.3f;
            if (c == null)
            {
                // extractionArea has no collider: what the game really counts is the roomVolume box
                GameObject rv = R.Get<GameObject>(ep, "roomVolume");
                c = rv != null ? rv.GetComponentInChildren<Collider>() : null;
                margin = -0.3f;   // stay clearly inside it
            }
            if (c == null) return null;
            Bounds b = c.bounds;
            b.Expand(margin);
            return b;
        }

        /// <summary>A spot just above the floor of the counting volume, where a dropped object lands inside it.</summary>
        Vector3 AreaDrop(ExtractionPoint ep, bool jitter = true)
        {
            Bounds? b = AreaBounds(ep);
            if (!b.HasValue) return ep.transform.position + Vector3.up * 0.8f;
            if (!jitter) return new Vector3(b.Value.center.x, b.Value.min.y + 0.8f, b.Value.center.z);
            // The counting volume is only about 3 x 3 m, so items have to pile up. Valuables lose value when they fall, so find
            // the top of what is already there and drop the new item from just above it, not from a fixed height.
            Vector2 j = UnityEngine.Random.insideUnitCircle * 0.8f;
            float x = b.Value.center.x + j.x, z = b.Value.center.z + j.y;
            float top = b.Value.min.y;
            RaycastHit hit;
            if (Physics.Raycast(new Vector3(x, b.Value.min.y + 6f, z), Vector3.down, out hit, 6f, ~0, QueryTriggerInteraction.Ignore)) top = hit.point.y;
            return new Vector3(x, Mathf.Min(Mathf.Max(top + 0.4f, b.Value.min.y + 0.6f), b.Value.min.y + 5f), z);
        }

        static bool InArea(ExtractionPoint ep, Vector3 p)
        {
            Bounds? b = AreaBounds(ep);
            return b.HasValue ? b.Value.Contains(p) : (p - ep.transform.position).sqrMagnitude < 9f;
        }

        static Vector3 AreaCenter(ExtractionPoint ep)
        {
            Bounds? b = AreaBounds(ep);
            return b.HasValue ? b.Value.center : ep.transform.position;
        }

        /// <summary>Nearest extraction point that is currently accepting valuables.</summary>
        ExtractionPoint DepositEp(Vector3 p)
        {
            if (ShopMode) return ShopEp();   // in the shop everything goes to the checkout area
            ExtractionPoint best = null; float bd = float.MaxValue;
            foreach (ExtractionPoint ep in eps)
            {
                if (EpState(ep) != "Active") continue;
                float d = (ep.transform.position - p).sqrMagnitude;
                if (d < bd) { bd = d; best = ep; }
            }
            return best;
        }

        bool InAnyArea(Vector3 p) { foreach (ExtractionPoint ep in eps) if (InArea(ep, p)) return true; return false; }

        ExtractionPoint NearestEp(Vector3 p)
        {
            ExtractionPoint best = null; float bd = float.MaxValue;
            foreach (ExtractionPoint ep in eps)
            {
                float d = (ep.transform.position - p).sqrMagnitude;
                if (d < bd) { bd = d; best = ep; }
            }
            return best;
        }

        bool IsBlacklisted(Component v)
        {
            float until;
            return blacklist.TryGetValue(v.GetInstanceID(), out until) && Time.time < until;
        }

        void Blacklist(Component v, float seconds)
        {
            if (v != null) blacklist[v.GetInstanceID()] = Time.time + seconds;
        }

        // ---- line of sight / viewpoints -------------------------------------------------------
        /// <summary>True if nothing solid (other than the target, movable objects and ourselves) is between the two points.</summary>
        bool ClearLine(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            float dist = d.magnitude;
            if (dist < 0.05f) return true;
            foreach (RaycastHit h in Physics.RaycastAll(from, d / dist, dist, ~0, QueryTriggerInteraction.Ignore))
            {
                Collider col = h.collider;
                if (target != null && col.transform.IsChildOf(target.transform)) continue;
                if (col.GetComponentInParent<PhysGrabObject>() != null) continue;
                if (col.GetComponentInParent<PlayerController>() != null || col.GetComponentInParent<PlayerAvatar>() != null) continue;
                return false;
            }
            return true;
        }

        bool WasTried(Vector3 p)
        {
            foreach (Vector3 t in triedViews) if ((t - p).sqrMagnitude < 0.36f) return true;
            return false;
        }

        /// <summary>Closest reachable spot (on the NavMesh) around the object from which the camera has a clear line to it.</summary>
        bool ChooseViewpoint(Vector3 from, Vector3 tc, out Vector3 best)
        {
            best = tc;
            float bestLen = float.MaxValue;
            bool found = false;
            for (float r = 1.5f; r <= 2.5f; r += 0.5f)
                for (int a = 0; a < 360; a += 30)
                {
                    Vector3 cand = tc + new Vector3(Mathf.Sin(a * Mathf.Deg2Rad), 0f, Mathf.Cos(a * Mathf.Deg2Rad)) * r;
                    NavMeshHit h;
                    if (!NavMesh.SamplePosition(cand, out h, 0.7f, NavMesh.AllAreas)) continue;
                    if (WasTried(h.position)) continue;
                    if (!ClearLine(h.position + Vector3.up * 1.5f, tc)) continue;
                    float len;
                    if (!PathLength(from, h.position, out len)) continue;
                    if (len < bestLen) { bestLen = len; best = h.position; found = true; }
                }
            return found;
        }

        // ---- navigation -----------------------------------------------------------------------
        bool PathLength(Vector3 a, Vector3 b, out float len)
        {
            len = 0f;
            NavMeshHit h1, h2;
            if (!NavMesh.SamplePosition(a, out h1, 4f, NavMesh.AllAreas) || !NavMesh.SamplePosition(b, out h2, 4f, NavMesh.AllAreas)) return false;
            if (!NavMesh.CalculatePath(h1.position, h2.position, NavMesh.AllAreas, navTmp) || navTmp.status != NavMeshPathStatus.PathComplete) return false;
            Vector3[] c = navTmp.corners;
            for (int i = 1; i < c.Length; i++) len += Vector3.Distance(c[i - 1], c[i]);
            lastCorners = c;
            return true;
        }

        void Repath(Vector3 p, Vector3 goal)
        {
            nextRepath = Time.time + 1.0f;
            pathGoal = goal;
            NavMeshHit h1, h2;
            if (NavMesh.SamplePosition(p, out h1, 4f, NavMesh.AllAreas) && NavMesh.SamplePosition(goal, out h2, 4f, NavMesh.AllAreas) &&
                NavMesh.CalculatePath(h1.position, h2.position, NavMesh.AllAreas, navPath) && navPath.status != NavMeshPathStatus.PathInvalid)
            {
                corners = navPath.corners;
                ci = 1;
            }
            else { corners = new[] { goal }; ci = 0; }
        }

        /// <summary>Walk toward goal along the NavMesh. True once within arriveDist (flat distance).</summary>
        bool Follow(Vector3 goal, float arriveDist, bool sprintOk, float turnRate, float alignDeg)
        {
            PlayerController pc = Local();
            Vector3 p = pc.transform.position;
            Vector3 flat = goal - p; flat.y = 0;
            if (flat.magnitude <= arriveDist) return true;
            if (flat.magnitude > 5f && TeleportPlayer(goal, "far goal")) return false;   // superpower: no need to walk

            if (Time.time >= nextRepath || (pathGoal - goal).sqrMagnitude > 1f) Repath(p, goal);

            Vector3 next = goal;
            for (; ci < corners.Length; ci++)
            {
                Vector3 d = corners[ci] - p; d.y = 0;
                if (d.magnitude > 0.9f) { next = corners[ci]; break; }
            }
            Transform c = Cam();
            float err = AimAt(new Vector3(next.x, c.position.y, next.z), turnRate);
            BotInput.Move.y = err < alignDeg ? moveScale : 0f;
            if (SprintWise(pc, sprintOk && err < 25f && !calmMove && (flat.magnitude > 10f || forceSprint))) Held.Add(InputKey.Sprint);
            TrackStuck(p, next);
            return false;
        }

        void TrackStuck(Vector3 p, Vector3 next)
        {
            if (Time.time - stuckT < 1f) return;
            stuckT = Time.time;
            if ((p - stuckPos).magnitude < 0.5f)
            {
                stuckCount++;
                if (stuckCount >= 2 && TeleportPlayer(pathGoal, "stuck")) { stuckCount = 0; stuckPos = p; return; }   // superpower
                if (!calmMove)   // never jump around inside the truck: low ceiling
                {
                    jumpUntil = Time.time + 0.2f;
                    strafeUntil = Time.time + 0.6f;
                    strafeDir = UnityEngine.Random.value < 0.5f ? -1f : 1f;
                }
                nextRepath = 0f;
                if (stuckCount >= 5)
                {
                    Plugin.Log.LogWarning("stuck at " + p + " heading to " + next + " (corner " + ci + "/" + corners.Length + "), giving up on target");
                    if (state == S.Home) homeFail++;
                    GiveUp(120f);
                    stuckCount = 0; hasViewpoint = false;
                    Go(S.Pick, "stuck");
                }
            }
            else stuckCount = 0;
            stuckPos = p;
        }

        /// <summary>Turn the camera toward a world point; returns the remaining angle in degrees.</summary>
        float AimAt(Vector3 world, float degPerSec)
        {
            Transform c = Cam();
            CameraAim ca = R.GetStatic<CameraAim>(typeof(CameraAim), "Instance");
            if (c == null || ca == null) return 180f;
            Vector3 to = world - c.position;
            float wantYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            float wantPitch = -Mathf.Atan2(to.y, new Vector2(to.x, to.z).magnitude) * Mathf.Rad2Deg; // + = looking down
            float step = degPerSec * Time.deltaTime;
            float ah = R.Get<float>(ca, "aimHorizontal"), av = R.Get<float>(ca, "aimVertical");
            R.Set(ca, "aimHorizontal", Mathf.MoveTowardsAngle(ah, wantYaw, step));
            R.Set(ca, "aimVertical", Mathf.MoveTowards(av, wantPitch, step));
            return Vector3.Angle(c.forward, to);
        }

        // ---- developer channel: run/cmd.txt in, run/state.json out ------------------------------
        void PollCommands()
        {
            try
            {
                string f = Dir + "cmd.txt";
                if (!File.Exists(f)) return;
                string[] lines = File.ReadAllLines(f);
                File.Delete(f);
                foreach (string raw in lines)
                {
                    string trimmed = raw.Trim();
                    // 'say <text>' makes the bot speak in chat (keeps the original capitals)
                    if (trimmed.StartsWith("say ", StringComparison.OrdinalIgnoreCase)) { Say(trimmed.Substring(4), true); continue; }
                    string cmd = trimmed.ToLowerInvariant();
                    if (cmd == "chatinfo")
                    {
                        ChatManager cmi = R.GetStatic<ChatManager>(typeof(ChatManager), "instance");
                        Plugin.Log.LogInfo("chatinfo: active " + R.Get<bool>(cmi, "chatActive") + " message '" + R.Get<string>(cmi, "chatMessage") + "' prev '" +
                                           R.Get<string>(cmi, "prevChatMessage") + "' state " + R.Get<object>(cmi, "chatState") + " spamTimer " + R.Get<float>(cmi, "spamTimer") +
                                           " history " + CountOf(R.Get<object>(cmi, "chatHistory")));
                        continue;
                    }
                    if (cmd == "markshop") { SetFlag(ShopKey()); Plugin.Log.LogInfo("shop marked as already done: " + ShopKey()); continue; }
                    if (cmd == "chat on") { chatOn = true; Plugin.Log.LogInfo("chat announcements ON"); continue; }
                    if (cmd == "chat off") { chatOn = false; Plugin.Log.LogInfo("chat announcements OFF"); continue; }
                    if (cmd == "on") SetActive(true);
                    else if (cmd == "off") SetActive(false);
                    else if (cmd == "dump") Dump();
                    else if (cmd == "revive") ReviveStep(manualRevive++);
                    else if (cmd == "kill") KillSelf();
                    else if (cmd == "hurt") HurtSelf(65);
                    else if (cmd == "headmove on") { headMove = true; Plugin.Log.LogInfo("head driving ON"); }
                    else if (cmd == "headmove off") { headMove = false; Plugin.Log.LogInfo("head driving OFF"); }
                    else if (cmd == "superpowers on") { superpowers = true; Plugin.Log.LogInfo("superpowers ON (teleport)"); }
                    else if (cmd == "superpowers off") { superpowers = false; Plugin.Log.LogInfo("superpowers OFF"); }
                    else if (cmd == "autoheal on") { autoHeal = true; Plugin.Log.LogInfo("auto-heal ON"); }
                    else if (cmd == "autoheal off") { autoHeal = false; Plugin.Log.LogInfo("auto-heal OFF"); }
                    else if (cmd == "autorevive on") { autoRevive = true; Plugin.Log.LogInfo("auto-revive ON"); }
                    else if (cmd == "autorevive off") { autoRevive = false; Plugin.Log.LogInfo("auto-revive OFF"); }
                    else if (cmd == "skip") { if (target != null) Blacklist(target, 300f); target = null; Go(S.Pick, "skip command"); }
                    else if (cmd == "press")
                    {
                        pressEp = NearestEp(Local() != null ? Local().transform.position : Vector3.zero);
                        pressFrom = EpState(pressEp);
                        Go(S.PressButton, "press command");
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("cmd: " + e.Message); }
        }

        static string F(float x) { return x.ToString("0.##", CultureInfo.InvariantCulture); }
        static string V(Vector3 v) { return "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]"; }
        static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "'") + "\""; }

        void WriteState(bool inLevel)
        {
            try
            {
                PushTrace();
                RunManager rm = R.GetStatic<RunManager>(typeof(RunManager), "instance");
                var sb = new StringBuilder("{");
                sb.Append("\"time\":").Append(F(Time.time));
                sb.Append(",\"active\":").Append(BotInput.Active ? "true" : "false");
                sb.Append(",\"inLevel\":").Append(inLevel ? "true" : "false");
                sb.Append(",\"level\":").Append(Q(lastLevel)).Append(",\"levelsCompleted\":").Append(R.Get<int>(rm, "levelsCompleted"));
                sb.Append(",\"hp\":").Append(Hp()).Append(",\"dead\":").Append(Dead() ? "true" : "false");
                sb.Append(",\"state\":").Append(Q(state.ToString()));
                sb.Append(",\"status\":").Append(Q(status));
                sb.Append(",\"delivered\":").Append(delivered);
                PlayerController pc = Local();
                if (pc != null)
                {
                    sb.Append(",\"pos\":").Append(V(pc.transform.position));
                    sb.Append(",\"energy\":").Append(F(R.Get<float>(pc, "EnergyCurrent")));
                }
                Transform c = Cam();
                if (c != null) sb.Append(",\"camYaw\":").Append(F(c.eulerAngles.y)).Append(",\"camPitch\":").Append(F(Mathf.DeltaAngle(0, c.eulerAngles.x)));
                CameraAim ca = R.GetStatic<CameraAim>(typeof(CameraAim), "Instance");
                if (ca != null) sb.Append(",\"aimH\":").Append(F(R.Get<float>(ca, "aimHorizontal"))).Append(",\"aimV\":").Append(F(R.Get<float>(ca, "aimVertical")));
                if (target != null)
                    sb.Append(",\"target\":{\"name\":").Append(Q(target.name)).Append(",\"pos\":").Append(V(target.transform.position))
                      .Append(",\"value\":").Append(F(R.Get<float>(target, "dollarValueCurrent"))).Append("}");
                sb.Append(",\"valuables\":").Append(vals.Count);
                sb.Append(",\"held\":[").Append(string.Join(",", Held.Select(k => Q(k.ToString())).ToArray())).Append("]");
                sb.Append(",\"extraction\":[");
                sb.Append(string.Join(",", eps.Where(e => e != null).Select(e => "{\"state\":" + Q(EpState(e)) + ",\"haul\":" + R.Get<int>(e, "haulCurrent") +
                    ",\"goal\":" + R.Get<int>(e, "haulGoal") + ",\"locked\":" + (R.Get<bool>(e, "isLocked") ? "true" : "false") +
                    ",\"pos\":" + V(e.transform.position) + "}").ToArray()));
                sb.Append("],\"enemies\":[");
                sb.Append(string.Join(",", enemies.Where(e => e != null).Select(e => "{\"name\":" + Q(R.Get<string>(e, "enemyName")) + ",\"state\":" +
                    Q(EnemyStateName(e)) + ",\"pos\":" + V(EnemyPos(e)) + "}").Take(8).ToArray()));
                sb.Append("]}");
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Dir + "state.json", sb.ToString());
            }
            catch (Exception e) { LogEvery(10f, "state: " + e); }
        }

        static int CountOf(object collection)
        {
            System.Collections.ICollection c = collection as System.Collections.ICollection;
            return c == null ? -1 : c.Count;
        }

        /// <summary>Cosmetics state, shop money/cart and the items on sale, for designing the shopping logic.</summary>
        void DumpMetaAndShop()
        {
            MetaManager mm = R.GetStatic<MetaManager>(typeof(MetaManager), "instance");
            if (mm != null)
                Plugin.Log.LogInfo("meta: tokens " + CountOf(R.Get<object>(mm, "cosmeticTokens")) + " unlocked " + CountOf(R.Get<object>(mm, "cosmeticUnlocks")) +
                                   " equipped " + CountOf(R.Get<object>(mm, "cosmeticEquipped")) + " of " + CountOf(R.Get<object>(mm, "cosmeticAssets")) + " cosmetics");
            CosmeticShopMachine cm = R.GetStatic<CosmeticShopMachine>(typeof(CosmeticShopMachine), "instance");
            if (cm != null)
                Plugin.Log.LogInfo("cosmetic machine at " + V(cm.transform.position) + " state " + R.Get<object>(cm, "stateCurrent"));
            foreach (CosmeticWorldObject cw in FindObjectsOfType<CosmeticWorldObject>())
                if (cw != null) Plugin.Log.LogInfo("cosmetic object " + cw.name + " rarity " + R.Get<object>(cw, "rarity") + " pos " + V(cw.transform.position) + " inExtraction " + R.Get<bool>(cw, "inExtraction"));

            ShopManager sm = R.GetStatic<ShopManager>(typeof(ShopManager), "instance");
            if (sm != null)
                Plugin.Log.LogInfo("shop: currency " + R.Get<int>(sm, "totalCurrency") + " (run currency per SemiFunc " + SemiFunc.StatGetRunCurrency() + ") cart cost " + R.Get<int>(sm, "totalCost") + " cart items " + CountOf(R.Get<object>(sm, "shoppingList")));
            // upgrades the game has recorded for the players, and the items that are lying around but are not for sale
            StatsManager stm = R.GetStatic<StatsManager>(typeof(StatsManager), "instance");
            if (stm != null)
                foreach (string up in new[] { "Health", "Stamina", "Strength", "Speed", "Range", "ExtraJump", "Launch", "CrouchRest", "Throw", "TumbleClimb" })
                {
                    System.Collections.IDictionary d = R.Get<object>(stm, "playerUpgrade" + up) as System.Collections.IDictionary;
                    if (d == null || d.Count == 0) continue;
                    var parts = new List<string>();
                    foreach (System.Collections.DictionaryEntry kv in d) parts.Add(kv.Key + "=" + kv.Value);
                    Plugin.Log.LogInfo("upgrade " + up + ": " + string.Join(", ", parts.ToArray()));
                }
            foreach (ItemAttributes oi in FindObjectsOfType<ItemAttributes>())
                if (oi != null && !R.Get<bool>(oi, "shopItem"))
                    Plugin.Log.LogInfo("  owned item " + R.Get<string>(oi, "itemName") + " type " + R.Get<object>(oi, "itemType") + " pos " + V(oi.transform.position) +
                                       " upgradeDone " + R.Get<bool>(oi.GetComponent<ItemUpgrade>(), "upgradeDone"));
            ExtractionPoint sep = ShopEp();
            if (sep != null)
            {
                Bounds? sb = AreaBounds(sep);
                Plugin.Log.LogInfo("shop checkout at " + V(sep.transform.position) + " state " + EpState(sep) + " area " + (sb.HasValue ? V(sb.Value.center) + " size " + V(sb.Value.size) : "none"));
            }
            int n = 0;
            foreach (ItemAttributes ia in FindObjectsOfType<ItemAttributes>())
            {
                if (ia == null || !R.Get<bool>(ia, "shopItem")) continue;
                Plugin.Log.LogInfo("  shop item " + R.Get<string>(ia, "itemName") + " price " + R.Get<int>(ia, "value") + " type " + R.Get<object>(ia, "itemType") + " pos " + V(ia.transform.position));
                if (++n >= 40) break;
            }
        }

        void Dump()
        {
            Plugin.Log.LogInfo("=== DUMP ===");
            PlayerController pc = Local();
            Transform c = Cam();
            if (pc != null) Plugin.Log.LogInfo("player ctrl pos " + V(pc.transform.position));
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            if (pa != null) Plugin.Log.LogInfo("player avatar pos " + V(pa.transform.position));
            if (c != null) Plugin.Log.LogInfo("camera pos " + V(c.position) + " euler " + V(c.eulerAngles));
            Plugin.Log.LogInfo("hp " + Hp() + " dead " + Dead() + " level " + lastLevel);
            Refresh();
            foreach (ValuableObject v in vals)
            {
                Rigidbody rb = R.Get<Rigidbody>(v, "rb");
                float len = -1f;
                if (pc != null) PathLength(pc.transform.position, v.transform.position, out len);
                RoomVolumeCheck rvc = R.Get<RoomVolumeCheck>(v, "roomVolumeCheck");
                Plugin.Log.LogInfo("valuable " + v.name + " $" + F(R.Get<float>(v, "dollarValueCurrent")) + " mass " + F(rb != null ? rb.mass : -1f) +
                                   " pos " + V(v.transform.position) + " path " + F(len) + " inArea " + InAnyArea(v.transform.position) +
                                   " gameSaysInExtraction " + R.Get<bool>(rvc, "inExtractionPoint") + " asleep " + (rb != null && rb.IsSleeping()));
            }
            foreach (ExtractionPoint e in eps)
            {
                GameObject ea = R.Get<GameObject>(e, "extractionArea"), rv = R.Get<GameObject>(e, "roomVolume");
                string eaB = ea == null ? "none" : string.Join("; ", ea.GetComponentsInChildren<Collider>().Select(c => V(c.bounds.center) + " size " + V(c.bounds.size) + (c.isTrigger ? " trigger" : "")).ToArray());
                string rvB = rv == null ? "none" : string.Join("; ", rv.GetComponentsInChildren<Collider>().Select(c => V(c.bounds.center) + " size " + V(c.bounds.size) + (c.isTrigger ? " trigger" : "")).ToArray());
                Plugin.Log.LogInfo("extraction geometry: extractionArea " + eaB + " | roomVolume " + rvB);
            }
            foreach (ExtractionPoint e in eps)
                Plugin.Log.LogInfo("extraction " + e.name + " state " + EpState(e) + " haul " + R.Get<int>(e, "haulCurrent") + "/" +
                                   R.Get<int>(e, "haulGoal") + " locked " + R.Get<bool>(e, "isLocked") + " pos " + V(e.transform.position) +
                                   " area " + V(AreaCenter(e)));
            foreach (EnemyParent e in enemies)
                Plugin.Log.LogInfo("enemy " + R.Get<string>(e, "enemyName") + " state " + EnemyStateName(e) + " pos " + V(EnemyPos(e)));
            float vOrig = 0f, vNow = 0f;
            foreach (ValuableObject vv in vals) { if (vv == null) continue; vOrig += R.Get<float>(vv, "dollarValueOriginal"); vNow += R.Get<float>(vv, "dollarValueCurrent"); }
            Plugin.Log.LogInfo("value of the valuables still in the level: original " + F(vOrig) + " now " + F(vNow) + " (lost " + F(vOrig - vNow) + ")");
            DumpMetaAndShop();
            Plugin.Log.LogInfo("=== END DUMP ===");
        }
    }
}
