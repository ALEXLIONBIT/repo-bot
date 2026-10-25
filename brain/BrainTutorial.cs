using System.Collections;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace RepoBot
{
    /// <summary>The game's tutorial: a row of tasks (move, jump, sneak, ...), each filled by doing it. This walks through them.</summary>
    public partial class Brain
    {
        float nextTutLog;
        bool tutListed;
        string lastTutPage = "";

        static TutorialDirector TutDir() { return R.GetStatic<TutorialDirector>(typeof(TutorialDirector), "instance"); }

        static string TutPageName(TutorialDirector td)
        {
            IList pages = R.Get<object>(td, "tutorialPages") as IList;
            int cur = R.Get<int>(td, "currentPage");
            if (pages == null || cur < 0 || cur >= pages.Count) return "?" + cur;
            return R.Get<string>(pages[cur], "pageName", "?") + "#" + cur;
        }

        void StartTutorialFromMenu()
        {
            MenuPageMain mp = FindObjectOfType<MenuPageMain>();
            tutListed = false; tutDoorX.Clear(); tutSkip.Clear();
            if (mp != null) { Plugin.Log.LogInfo("tutorial: starting from the main menu"); mp.ButtonEventTutorial(); return; }
            // already inside a level: do what the menu button does
            Plugin.Log.LogInfo("tutorial: (re)starting the tutorial");
            TutorialDirector td = TutDir();
            if (td != null) td.Reset();
            RunManager rm = R.GetStatic<RunManager>(typeof(RunManager), "instance");
            if (rm == null) return;
            rm.ResetProgress();
            rm.ChangeLevel(true, false, RunManager.ChangeLevelType.Tutorial);
        }

        // ---- the tutorial is a corridor along +x, one room per page, a door at the end of each room ----
        readonly System.Collections.Generic.Dictionary<int, float> tutDoorX = new System.Collections.Generic.Dictionary<int, float>();
        bool tutPatrolBack;
        float tutCartPulse, tutCartReleaseUntil;
        string tutLastPage = ""; float tutLastProg, tutProgT;
        bool tutTrigLogged; float tutDoorLog;
        readonly System.Collections.Generic.Dictionary<int, float> tutSkip = new System.Collections.Generic.Dictionary<int, float>();
        int tutTryId; float tutTrySince;
        bool TutSkipped(Component o) { float until; return o != null && tutSkip.TryGetValue(o.GetInstanceID(), out until) && Time.time < until; }
        float tutSeriesUntil, tutFillLog;
        bool tutWantsMove; Vector3 tutPos; float tutPosT, tutUnstickUntil, tutUnstickDir;
        float nextTutDbg, tutErr, tutDist;

        void TutorialScan()
        {
            tutDoorX.Clear();
            foreach (TutorialDoor d in FindObjectsOfType<TutorialDoor>()) tutDoorX[R.Get<int>(d, "tutorialPage")] = d.transform.position.x;
        }

        /// <summary>Walk straight at a point (the corridor has no corners); returns the distance left.</summary>
        float TutWalk(Vector3 goal, float scale = 1f, float turn = 360f)
        {
            Transform c = Cam();
            Vector3 flat = goal - c.position; flat.y = 0f;
            float err = AimAt(new Vector3(goal.x, c.position.y, goal.z), turn);
            tutErr = err; tutDist = flat.magnitude; tutWantsMove = scale > 0.1f && err < 25f;
            BotInput.Move.y = err < 25f ? scale : 0f;
            return flat.magnitude;
        }

        /// <summary>Walk back and forth between two points (for tasks that need continued movement).</summary>
        void TutPatrol(Vector3 a, Vector3 b, float scale = 1f)
        {
            Vector3 p = Local().transform.position;
            Vector3 goal = tutPatrolBack ? a : b;
            if (TutWalk(goal, scale) < 1.2f) tutPatrolBack = !tutPatrolBack;
        }

        /// <summary>X range of the low obstacle in the room (found by looking up from the floor).</summary>
        float mattLo = float.NaN, mattHi;
        void FindMattress(float lo, float hi)
        {
            if (!float.IsNaN(mattLo)) return;
            mattLo = float.PositiveInfinity; mattHi = float.NegativeInfinity;
            for (float x = lo; x <= hi; x += 0.25f)
            {
                RaycastHit hit;
                if (Physics.Raycast(new Vector3(x, 0.2f, 0f), Vector3.up, out hit, 3f, 1 << LayerMask.NameToLayer("Default"))) { mattLo = Mathf.Min(mattLo, x); mattHi = Mathf.Max(mattHi, x); }
            }
            if (float.IsPositiveInfinity(mattLo)) { mattLo = (lo + hi) / 2f - 1f; mattHi = (lo + hi) / 2f + 1f; }
        }
        float MattressLo(float lo, float hi) { FindMattress(lo, hi); return mattLo; }
        float MattressHi(float lo, float hi) { FindMattress(lo, hi); return mattHi; }

        int TutPageOfRoomLo(float lo) { foreach (var kv in tutDoorX) if (Mathf.Abs(kv.Value + 1.2f - lo) < 0.01f) return kv.Key + 1; return 14; }
        float TutRoomLo(int page) { float x; return page == 0 ? -6f : (tutDoorX.TryGetValue(page - 1, out x) ? x + 1.2f : 0f); }
        float TutRoomHi(int page) { float x; return tutDoorX.TryGetValue(page, out x) ? x - 1.2f : TutRoomLo(page) + 10f; }

        void TutorialDo(TutorialDirector td, Vector3 p)
        {
            PushPull = 0f;
            if (tutDoorX.Count == 0) TutorialScan();
            int page = R.Get<int>(td, "currentPage");
            float lo = TutRoomLo(page), hi = TutRoomHi(page), mid = (lo + hi) / 2f;
            Vector3 A = new Vector3(lo + 0.5f, 0f, 0f), B = new Vector3(hi - 0.5f, 0f, 0f);
            // caught on the rubble lying around: stand up, hop and sidestep for a moment
            if (Time.time < tutUnstickUntil)
            {
                BotInput.Move.y = 0.6f; BotInput.Move.x = tutUnstickDir;
                if (((int)(Time.time * 4f)) % 2 == 0) Held.Add(InputKey.Jump);
                return;
            }
            if (Time.time - tutPosT > 1.2f)
            {
                if (tutWantsMove && (p - tutPos).magnitude < 0.3f)
                {
                    tutUnstickUntil = Time.time + 0.9f;
                    tutUnstickDir = UnityEngine.Random.value < 0.5f ? -1f : 1f;
                    Plugin.Log.LogInfo("tutorial: stuck at " + V(p) + ", hopping over it");
                }
                tutPos = p; tutPosT = Time.time; tutWantsMove = false;
            }
            // first get into this page's room (the door opens a moment after the previous page is done)
            if (p.x < lo - 0.5f) { TutWalk(new Vector3(mid, 0f, 0f)); return; }
            switch (page)
            {
                case 0: TutPatrol(A, B, 0.8f); break;                                                    // movement
                case 1:                                                                                  // jumping
                    TutWalk(new Vector3(mid, 0f, 0f), 0.3f);
                    if (((int)(Time.time * 2f)) % 2 == 0) Held.Add(InputKey.Jump);
                    break;
                case 2: Held.Add(InputKey.Crouch); TutPatrol(A, B, 0.8f); break;                          // sneaking
                case 3:
                    Held.Add(InputKey.Crouch);
                    // the low spot is a mattress: crawl under it, back and forth, until the bar is full
                    TutPatrol(new Vector3(MattressLo(lo, hi) - 1.6f, 0f, 0f), new Vector3(MattressHi(lo, hi) + 1.6f, 0f, 0f), 0.8f);
                    break;         // crawling under
                case 4: Held.Add(InputKey.Sprint); TutPatrol(A, B); break;                                // sprinting
                case 5:                                                                                  // tumbling
                    TutPatrol(A, B, 0.6f);
                    if (((int)(Time.time * 1.5f)) % 3 == 0) Held.Add(InputKey.Tumble);
                    break;
                case 6: TutGrab(TutFind("Valuable Vase", lo, hi)); break;                                // grabbing
                case 7:                                                                                  // pushing and pulling
                    if (TutGrab(TutFind("Valuable", lo, hi))) PushPull = ((int)(Time.time * 2f)) % 2 == 0 ? 1f : -1f;
                    break;
                case 8:                                                                                  // rotating
                    if (TutGrab(TutFind("Valuable Vase", lo, hi))) Held.Add(InputKey.Rotate);
                    break;
                case 9:                                                                                  // item toggle
                    if (TutGrab(TutFind("Item Orb", lo, hi)) && ((int)(Time.time * 2f)) % 2 == 0) Held.Add(InputKey.Interact);
                    break;
                case 10: TutInventoryFill(lo, hi); break;
                case 11: TutInventoryEmpty(); break;
                case 12: Held.Add(InputKey.Map); break;                                                  // map
                case 13: TutCartMove(); break;
                case 14: TutCartFill(lo, hi); break;
                case 15: TutExtraction(); break;
                default: TutWalk(new Vector3(mid, 0f, 0f)); break;
            }
            if (page == 14 && !tutTrigLogged)
            {
                tutTrigLogged = true;
                foreach (Collider col in FindObjectsOfType<Collider>())
                    if (col.isTrigger && col.bounds.center.x > 100f && col.bounds.center.x < 125f && col.gameObject.layer != LayerMask.NameToLayer("CollisionCheck"))
                        Plugin.Log.LogInfo("tutorial trigger: " + col.name + " (" + col.GetType().Name + ") center " + V(col.bounds.center) + " size " + V(col.bounds.size) + " comps " + string.Join(",", col.GetComponents<Component>().Select(c2 => c2.GetType().Name).ToArray()));
            }
            if (page == 14 && Time.time > tutDoorLog)
            {
                tutDoorLog = Time.time + 3f;
                foreach (TutorialDoor d in FindObjectsOfType<TutorialDoor>())
                    if (R.Get<int>(d, "tutorialPage") >= 12) Plugin.Log.LogInfo("tutorial door " + R.Get<int>(d, "tutorialPage") + " state " + R.Get<int>(d, "currentState") + " x " + d.transform.position.x.ToString("0.0") + " y " + d.transform.position.y.ToString("0.0") + " moveDone " + R.Get<bool>(d, "moveDone") + " animDone " + R.Get<bool>(d, "animationDone"));
            }
            if (page == 14 || page == 13)
            {
                if (Time.time < tutSeriesUntil) Plugin.Log.LogInfo("series: cam " + Cam().eulerAngles.y.ToString("0") + " p " + Cam().eulerAngles.x.ToString("0") + " grabbed " + R.Get<bool>(Grabber(), "grabbed") + " held " + string.Join(",", Held.Select(k => k.ToString()).ToArray()) + " move " + BotInput.Move.y.ToString("0.0") + " pos " + V(p) + " cartActive " + (TutCart() != null && R.Get<bool>(TutCart(), "cartActive")) + " press " + R.Get<float>(Grabber(), "initialPressTimer").ToString("0.00"));
                else if (Time.time > tutSeriesUntil + 3f) tutSeriesUntil = Time.time + 8f;
            }
            if (Time.time > nextTutDbg)
            {
                nextTutDbg = Time.time + 3f;
                PlayerController dpc = Local();
                Plugin.Log.LogInfo("tutorial dbg: page " + page + " move " + BotInput.Move + " vel " + dpc.rb.velocity + " held " + string.Join(",", Held.Select(k => k.ToString()).ToArray()) +
                                   " crouching " + R.Get<bool>(dpc, "Crouching") + " crawling " + R.Get<bool>(dpc, "Crawling") + " active " + BotInput.Active + " grabbed " + R.Get<bool>(Grabber(), "grabbed") + " pushpull " + PushPull + " pushing " + R.Get<bool>(Grabber(), "isPushing") + " pulling " + R.Get<bool>(Grabber(), "isPulling") + (TutCart() != null ? " cart " + V(TutCart().transform.position) + " handle " + V(R.Get<Transform>(TutCart(), "handlePoint") != null ? R.Get<Transform>(TutCart(), "handlePoint").position : Vector3.zero) + " active " + R.Get<bool>(TutCart(), "cartActive") : ""));
                if (dpc.rb.velocity.magnitude < 0.1f && BotInput.Move.y > 0.1f)
                    foreach (Collider col in Physics.OverlapSphere(p + Vector3.up * 0.4f, 0.9f))
                        if (!col.name.StartsWith("Collision") && col.gameObject.layer != LayerMask.NameToLayer("PlayerVisuals") && col.gameObject.layer != LayerMask.NameToLayer("PlayerVisualsLocal"))
                            Plugin.Log.LogInfo("  touching: " + col.name + " layer " + LayerMask.LayerToName(col.gameObject.layer) + " trigger " + col.isTrigger + " center " + V(col.bounds.center) + " size " + V(col.bounds.size));
            }
        }

        // ---- grabbing things in the tutorial rooms ----
        public static float PushPull;   // -1..1, what the game reads as the push/pull axis

        /// <summary>Nearest light object whose name starts with `prefix` inside [lo, hi] on x.</summary>
        PhysGrabObject TutFind(string prefix, float lo, float hi, bool light = true)
        {
            PhysGrabObject best = null; float bd = float.MaxValue;
            Vector3 me = Local().transform.position;
            foreach (PhysGrabObject o in FindObjectsOfType<PhysGrabObject>())
            {
                if (o == null || TutSkipped(o) || !o.name.StartsWith(prefix) || o.transform.position.x < lo || o.transform.position.x > hi) continue;
                Rigidbody rb = R.Get<Rigidbody>(o, "rb");
                if (light && rb != null && rb.mass > 8f) continue;
                float d = (o.transform.position - me).sqrMagnitude;
                if (d < bd) { bd = d; best = o; }
            }
            return best;
        }

        /// <summary>Walk up to an object, look at it and hold the grab button; true while we hold it.</summary>
        bool TutGrab(PhysGrabObject o)
        {
            if (o == null) return false;
            PhysGrabber pg = Grabber();
            Transform c = Cam();
            bool holding = R.Get<bool>(pg, "grabbed");
            Vector3 oc = o.centerPoint;   // the game's own centre of the thing (a child collider can sit anywhere)
            if (oc == Vector3.zero) oc = o.transform.position + Vector3.up * 0.15f;
            if (holding) { Held.Add(InputKey.Grab); tutTryId = 0; return true; }
            // an object that will not be taken (out of reach, on a ledge): give up on it for a while and try another
            if (tutTryId != o.GetInstanceID()) { tutTryId = o.GetInstanceID(); tutTrySince = Time.time; }
            else if (Time.time - tutTrySince > 12f) { tutSkip[tutTryId] = Time.time + 90f; tutTryId = 0; Plugin.Log.LogInfo("tutorial: cannot take " + o.name + ", trying another"); return false; }   // keep the view where it is: the object follows the camera
            float err = AimAt(oc, 400f);
            float d = Vector3.Distance(c.position, oc);
            BotInput.Move.y = d < 1.4f ? -0.5f : ((err < 20f && d > 1.9f) ? 0.6f : 0f);   // too close to aim down at it: back off
            tutWantsMove = BotInput.Move.y > 0.1f;
            if (Time.time > nextTutDbg - 2.9f && Time.time < nextTutDbg - 2.8f) Plugin.Log.LogInfo("tutorial grab: " + o.name + " at " + V(o.transform.position) + " d " + d.ToString("0.00") + " err " + err.ToString("0") + " mass " + R.Get<Rigidbody>(o, "rb").mass + " grabRange " + R.Get<float>(pg, "grabRange", 4f) + " camPitch " + c.eulerAngles.x + " looking at " + (R.Get<PhysGrabObject>(pg, "currentlyLookingAtPhysGrabObject") != null ? R.Get<PhysGrabObject>(pg, "currentlyLookingAtPhysGrabObject").name : "nothing") + " disable " + R.Get<float>(pg, "grabDisableTimer"));
            if (err < 15f && d < Mathf.Min(2.1f, R.Get<float>(pg, "grabRange", 4f) * 0.85f)) Held.Add(InputKey.Grab);
            return false;
        }

        float tutKeyAt;
        static InputKey InvKey(int i) { return i == 0 ? InputKey.Inventory1 : (i == 1 ? InputKey.Inventory2 : InputKey.Inventory3); }

        /// <summary>One short key press now and then (the game wants a fresh press, and a cooldown sits between equips).</summary>
        void TutPulse(InputKey k)
        {
            if (Time.time < tutKeyAt) return;
            tutKeyAt = Time.time + 1.0f;
            Held.Add(k);
        }

        void TutInventoryFill(float lo, float hi)
        {
            Inventory inv = Inventory.instance;
            if (inv == null) return;
            int free = inv.GetFirstFreeInventorySpotIndex();
            if (inv.InventorySpotsOccupied() >= 3 || free < 0) return;
            PhysGrabObject it = TutFind("Item ", lo, hi);   // a free item lying about
            if (TutGrab(it)) TutPulse(InvKey(free));
        }

        void TutInventoryEmpty()
        {
            Inventory inv = Inventory.instance;
            if (inv == null) return;
            for (int i = 0; i < 3; i++)
                if (inv.IsSpotOccupied(i)) { TutPulse(InvKey(i)); return; }
        }

        /// <summary>The cart is steered through its handle: the little grab box on one side of it, not the body.</summary>
        Vector3 TutHandle(PhysGrabCart cart)
        {
            Component area = R.Get<Component>(cart, "physGrabObjectGrabArea");
            if (area != null)
            {
                BoxCollider bc = area.GetComponentInChildren<BoxCollider>();
                return bc != null ? bc.bounds.center : area.transform.position;
            }
            return cart.transform.position;
        }

        /// <summary>Walk round to the outer side of the cart's handle, look at it and grab. True once we hold the cart.</summary>
        bool TutGrabHandle(PhysGrabCart cart)
        {
            PhysGrabber pg = Grabber();
            if (R.Get<bool>(pg, "grabbed") && pg.grabbedObjectTransform != null && pg.grabbedObjectTransform.GetComponent<PhysGrabCart>() != null) { Held.Add(InputKey.Grab); return true; }
            Transform c = Cam();
            Vector3 hp = TutHandle(cart);
            Vector3 out3 = hp - cart.transform.position; out3.y = 0f;
            out3 = out3.sqrMagnitude < 0.01f ? Vector3.back : out3.normalized;
            Vector3 stand = hp + out3 * 1.9f; stand.y = 0f;
            Vector3 me = Local().transform.position;
            if (Vector3.Distance(new Vector3(me.x, 0f, me.z), stand) > 0.7f && Vector3.Distance(c.position, hp) > 2.4f)
            {
                TutWalk(stand, 0.8f);
                tutWantsMove = true;
                return false;
            }
            float err = AimAt(hp, 300f);
            float d = Vector3.Distance(c.position, hp);
            BotInput.Move.y = d < 1.3f ? -0.4f : 0f;
            if (err < 12f && d < 2.6f) Held.Add(InputKey.Grab);
            return false;
        }

        /// <summary>Push the loaded cart into the extraction area and press the button.</summary>
        void TutExtraction()
        {
            ExtractionPoint ep = FindObjectOfType<ExtractionPoint>();
            PhysGrabCart cart = TutCart();
            if (ep == null) return;
            if (cart != null && cart.transform.position.x < ep.transform.position.x - 1f)
            {
                TutCartToRoom(cart, ep.transform.position.x - 0.3f);
                return;
            }
            PhysGrabber pg = Grabber();
            if (R.Get<bool>(pg, "grabbed")) return;   // let go of the cart first
            Transform c = Cam();
            MeshRenderer btn = R.Get<MeshRenderer>(ep, "button");
            Vector3 bp = btn != null ? btn.bounds.center : ep.transform.position;
            float err = AimAt(bp, 300f);
            float d = Vector3.Distance(c.position, bp);
            BotInput.Move.y = (err < 25f && d > 2.0f) ? 0.6f : 0f;
            tutWantsMove = BotInput.Move.y > 0.1f;
            if (d < 2.6f) Held.Add(((int)(Time.time / 2f)) % 2 == 0 ? InputKey.Grab : InputKey.Interact);
        }

        PhysGrabCart TutCart() { return FindObjectOfType<PhysGrabCart>(); }

        /// <summary>Hold the cart's handle and push / pull it along the corridor.</summary>
        void TutCartMove()
        {
            PhysGrabCart cart = TutCart();
            if (cart == null) return;
            if (!TutGrabHandle(cart)) return;
            // the cart only drives when it has been "switched on" by a fresh press of the grab button: let go and press again
            if (!R.Get<bool>(cart, "cartActive") && Time.time > tutCartPulse) { tutCartPulse = Time.time + 1.5f; tutCartReleaseUntil = Time.time + 0.35f; }
            if (Time.time < tutCartReleaseUntil) Held.Remove(InputKey.Grab);
            // walk on with the cart in front of us, then back, so it moves both ways
            bool back = ((int)(Time.time * 0.5f)) % 2 == 1;
            BotInput.Move.y = back ? -0.8f : 0.8f;
            PushPull = back ? -1f : 1f;
            tutWantsMove = true;
        }

        /// <summary>Bring the cart through the door into the room with the valuables (the lesson wants them loaded there).</summary>
        bool TutCartToRoom(PhysGrabCart cart, float wantX)
        {
            if (cart.transform.position.x >= wantX) return true;
            Transform c = Cam();
            if (!TutGrabHandle(cart)) return false;
            // push on toward the next room (the door behind us cannot be crossed again, so the cart has to come along)
            AimAt(new Vector3(wantX + 5f, c.position.y, cart.transform.position.z), 150f);
            BotInput.Move.y = 0.7f;
            PushPull = 1f;
            tutWantsMove = true;
            return false;
        }

        /// <summary>Put three valuables into the cart.</summary>
        void TutCartFill(float lo, float hi)
        {
            PhysGrabCart cart = TutCart();
            if (cart == null) return;
            if (R.Get<int>(cart, "itemsInCartCount") >= 3)
            {
                // loaded: keep hold of the cart and bring it on through the next door, which only opens one way
                float nextDoor; tutDoorX.TryGetValue(14, out nextDoor);
                TutCartToRoom(cart, nextDoor + 4f);
                return;
            }
            if (!TutCartToRoom(cart, lo + 3.5f)) return;
            PhysGrabber pg = Grabber();
            Transform c = Cam();
            Transform vol = R.Get<Transform>(cart, "inCart");
            Vector3 into = vol != null ? vol.position : cart.transform.position;
            if (Time.time > tutFillLog)
            {
                tutFillLog = Time.time + 1f;
                Transform gt = pg.grabbedObjectTransform;
                Plugin.Log.LogInfo("tutorial cart fill: grabbed " + R.Get<bool>(pg, "grabbed") + " (" + (gt != null ? gt.name + " " + V(gt.position) : "-") + ") me " + V(c.position) + " cart " + V(cart.transform.position) + " items " + R.Get<int>(cart, "itemsInCartCount"));
            }
            if (R.Get<bool>(pg, "grabbed") && pg.grabbedObjectTransform != null && pg.grabbedObjectTransform.GetComponent<PhysGrabCart>() == null)
            {
                Transform held = pg.grabbedObjectTransform;
                Vector3 off = held.position - into; off.y = 0f;
                if (off.magnitude < 0.45f) return;   // above the cart: let go (no Grab pressed)
                Held.Add(InputKey.Grab);
                float e = AimAt(new Vector3(into.x, c.position.y, into.z), 250f);
                BotInput.Move.y = e < 20f ? (off.magnitude > 1.5f ? 0.7f : 0.35f) : 0f;
                tutWantsMove = BotInput.Move.y > 0.1f;
                return;
            }
            if (R.Get<bool>(pg, "grabbed")) return;   // still holding the cart: let go first
            PhysGrabObject v = TutFind("Valuable", float.NegativeInfinity, float.PositiveInfinity);
            // the nearest valuable that is not already in the cart
            PhysGrabObject best = null; float bd = float.MaxValue;
            foreach (PhysGrabObject o in FindObjectsOfType<PhysGrabObject>())
            {
                if (o == null || TutSkipped(o) || !o.name.StartsWith("Valuable") || TakenByOthers(o.GetComponent<ValuableObject>())) continue;
                Rigidbody rb = R.Get<Rigidbody>(o, "rb"); if (rb != null && rb.mass > 8f) continue;
                float dd = (o.transform.position - c.position).sqrMagnitude;
                if (dd < bd) { bd = dd; best = o; }
            }
            TutGrab(best);
        }

        /// <summary>Called every frame while alive in the tutorial level.</summary>
        bool TutorialStep(Vector3 p)
        {
            TutorialDirector td = TutDir();
            if (td == null || !SemiFunc.RunIsTutorial()) return false;
            string page = TutPageName(td);
            if (!tutListed)
            {
                tutListed = true;
                IList all = R.Get<object>(td, "tutorialPages") as IList;
                if (all != null) Plugin.Log.LogInfo("tutorial pages: " + string.Join(", ", all.Cast<object>().Select((o, i) => i + "=" + R.Get<string>(o, "pageName", "?")).ToArray()));
                foreach (TutorialDoor d in FindObjectsOfType<TutorialDoor>()) Plugin.Log.LogInfo("tutorial door page " + R.Get<int>(d, "tutorialPage") + " at " + V(d.transform.position));
                foreach (PhysGrabObject o in FindObjectsOfType<PhysGrabObject>()) Plugin.Log.LogInfo("tutorial object " + o.name + " at " + V(o.transform.position));
                foreach (ExtractionPoint e in FindObjectsOfType<ExtractionPoint>()) Plugin.Log.LogInfo("tutorial extraction at " + V(e.transform.position));
                foreach (TutorialTruckTrigger t in FindObjectsOfType<TutorialTruckTrigger>()) Plugin.Log.LogInfo("tutorial truck trigger at " + V(t.transform.position));
            }
            if (Time.time >= nextTutLog || page != lastTutPage)
            {
                nextTutLog = Time.time + 2f; lastTutPage = page;
                IList pages = R.Get<object>(td, "tutorialPages") as IList;
                Plugin.Log.LogInfo("tutorial: page " + page + " progress " + R.Get<float>(td, "tutorialProgress").ToString("0.00") + " pos " + V(p) +
                                   (pages != null ? " (" + pages.Count + " pages)" : ""));
            }
            // a lesson that makes no progress for too long (a cart that will not switch on, ...): let the tutorial go on
            float prog = R.Get<float>(td, "tutorialProgress");
            if (page != tutLastPage || prog > tutLastProg + 0.01f) { tutLastPage = page; tutLastProg = prog; tutProgT = Time.time; }
            else if (Time.time - tutProgT > 70f)
            {
                Plugin.Log.LogWarning("tutorial: no progress on " + page + " for 70 s, moving to the next lesson");
                td.NextPage();
                tutProgT = Time.time;
            }
            status = "tutorial " + page + " mat " + mattLo.ToString("0.0") + ".." + mattHi.ToString("0.0") + " err " + tutErr.ToString("0") + " dist " + tutDist.ToString("0.0") + " move " + BotInput.Move.y.ToString("0.0") + " back " + tutPatrolBack;
            if (td != null && !R.Get<bool>(td, "tutorialActive") && false) return true;
            TutorialDo(td, p);
            return true;
        }
    }
}

namespace RepoBot
{
    [HarmonyPatch(typeof(InputManager), "KeyPullAndPush")]
    static class PullPushPatch
    {
        static void Postfix(ref float __result)
        {
            if (BotInput.Active && Brain.PushPull != 0f) __result = Brain.PushPull;
        }
    }
}
