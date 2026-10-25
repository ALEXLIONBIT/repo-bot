using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace RepoBot
{
    /// <summary>
    /// Everything the bot does between levels: use a cosmetic token on the cosmetic machine, equip the
    /// cosmetics that are unlocked, and buy a small set of upgrades with a spending cap.
    /// Shop money is shared by the whole team, so the policy below is deliberately modest.
    /// </summary>
    public partial class Brain
    {
        // ---- shopping policy (the team's money is shared: keep it modest) ----
        // With friends the money is shared, so stay modest; alone it is all ours and surviving longer pays for itself.
        static float SpendShare { get { return SemiFunc.IsMultiplayer() ? 0.4f : 0.7f; } }   // never plan to spend more than this share of the money
        static int MaxPurchases { get { return SemiFunc.IsMultiplayer() ? 3 : 6; } }          // items we put in the cart per shop visit
        // How much each upgrade is worth to us: staying alive and being able to run come first, carrying power matters least
        // (we never pick up the heavy things anyway).
        static readonly string[,] Wishlist = { { "Health Upgrade", "10" }, { "Stamina Upgrade", "8" }, { "Sprint Speed Upgrade", "6" }, { "Range Upgrade", "4" }, { "Strength Upgrade", "3" } };
        readonly Dictionary<string, int> boughtKinds = new Dictionary<string, int>();   // upgrades already chosen this visit: each extra one is worth less

        static bool ShopMode { get { return SemiFunc.RunIsShop(); } }

        // ---- progress during one shop visit ----
        int shopDelivered;        // items we have put into the checkout this visit (kept across plan resets)
        int depositTries;         // times the same item had to be picked up again because the cart did not count it
        bool shopInfoLogged;      // logged the checkout geometry once per visit
        int shopStart, plannedSpend;   // team money when we entered, and what we already planned to spend
        int bought;               // items we chose to buy
        bool paid;                // we handled the pay button
        bool machineDone;         // tried the cosmetic machine
        bool equipDone;           // equipped unlocked cosmetics
        int tokensAtStart;
        ExtractionPoint shopEpCache;

        void ResetShop()
        {
            boughtKinds.Clear(); bought = 0; paid = false; machineDone = false; equipDone = false; shopEpCache = null; shopInfoLogged = false;
            shopStart = 0; plannedSpend = 0;
        }

        // ---- remembering a finished purchase across brain reloads (a reload must not buy twice) ----
        static string SessionId()
        {
            double start = (DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds - Time.realtimeSinceStartup;   // when the game started
            return ((long)(start / 600.0)).ToString();
        }

        string ShopKey()
        {
            RunManager rm = R.GetStatic<RunManager>(typeof(RunManager), "instance");
            return "shop|" + SessionId() + "|" + lastLevel + "|" + R.Get<int>(rm, "levelsCompleted");
        }

        bool FlagSet(string key)
        {
            try { string f = Dir + "flags.txt"; return File.Exists(f) && File.ReadAllLines(f).Contains(key); }
            catch (Exception) { return false; }
        }

        bool TryLoadHome(string key, out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                string f = Dir + "homes.txt";
                if (!File.Exists(f)) return false;
                foreach (string line in File.ReadAllLines(f))
                {
                    string[] a = line.Split('=');
                    if (a.Length != 2 || a[0] != key) continue;
                    string[] c = a[1].Split(',');
                    if (c.Length != 3) continue;
                    pos = new Vector3(float.Parse(c[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        void SaveHome(string key, Vector3 pos)
        {
            try
            {
                File.AppendAllText(Dir + "homes.txt", key + "=" + pos.x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "," +
                                   pos.y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "," + pos.z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "\n");
            }
            catch (Exception e) { Plugin.Log.LogWarning("homes.txt: " + e.Message); }
        }

        void SetFlag(string key)
        {
            try { File.AppendAllText(Dir + "flags.txt", key + "\n"); }
            catch (Exception e) { Plugin.Log.LogWarning("flags.txt: " + e.Message); }
        }

        static ShopManager Shop() { return R.GetStatic<ShopManager>(typeof(ShopManager), "instance"); }
        static int Currency() { return R.Get<int>(Shop(), "totalCurrency"); }
        static int CartCost() { return R.Get<int>(Shop(), "totalCost"); }

        static int Tokens()
        {
            return CountOf(R.Get<object>(R.GetStatic<MetaManager>(typeof(MetaManager), "instance"), "cosmeticTokens"));
        }

        /// <summary>The shop's checkout: an extraction point flagged isShop.</summary>
        ExtractionPoint ShopEp()
        {
            if (shopEpCache == null)
                shopEpCache = FindObjectsOfType<ExtractionPoint>().FirstOrDefault(e => e != null && R.Get<bool>(e, "isShop"));
            return shopEpCache;
        }

        /// <summary>Where shop goods must be put to land in the cart: the shop station if the game has one.</summary>
        Vector3 ShopDrop(ExtractionPoint ep)
        {
            Transform st = R.Get<Transform>(ep, "shopStation");
            if (st != null) return st.position;
            Transform mgr = R.Get<Transform>(Shop(), "extractionPoint");
            return mgr != null ? mgr.position : ep.transform.position;
        }

        /// <summary>The game's own cart (ShopManager.shoppingList) is the truth about what counts as put in the checkout.</summary>
        static bool InShoppingList(Component item)
        {
            System.Collections.IEnumerable list = R.Get<object>(Shop(), "shoppingList") as System.Collections.IEnumerable;
            if (list == null || item == null) return false;
            foreach (object o in list) if ((o as Component) == item) return true;
            return false;
        }

        /// <summary>Has this thing arrived where it should be (extraction area, or the shop cart)?</summary>
        bool Delivered(ExtractionPoint ep, Component item)
        {
            Vector3 pos = item.transform.position;
            if (!ShopMode) return InArea(ep, pos);
            return InShoppingList(item) || (pos - ShopDrop(ep)).magnitude < 0.9f;
        }

        /// <summary>Worth of an upgrade to us (0 = not wanted); the second and third of a kind are worth less.</summary>
        float Weight(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0f;
            for (int i = 0; i < Wishlist.GetLength(0); i++)
                if (name.IndexOf(Wishlist[i, 0], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int n; boughtKinds.TryGetValue(Wishlist[i, 0], out n);
                    return float.Parse(Wishlist[i, 1], System.Globalization.CultureInfo.InvariantCulture) * Mathf.Pow(0.6f, n);
                }
            return 0f;
        }

        static string KindOf(string name)
        {
            for (int i = 0; i < Wishlist.GetLength(0); i++)
                if (!string.IsNullOrEmpty(name) && name.IndexOf(Wishlist[i, 0], StringComparison.OrdinalIgnoreCase) >= 0) return Wishlist[i, 0];
            return null;
        }

        // ---- deciding what to do in the shop ---------------------------------------------------
        void DoShopPick(Vector3 p)
        {
            Held.Remove(InputKey.Grab);
            if (Time.time < nextPick) { status = "shop: thinking"; return; }
            nextPick = Time.time + 1.0f;

            if (!paid && FlagSet(ShopKey())) { paid = true; bought = MaxPurchases; Plugin.Log.LogInfo("shop: already bought in this shop, not buying again"); }

            // 1) a cosmetic token is waiting: use it on the cosmetic machine
            CosmeticShopMachine cm = R.GetStatic<CosmeticShopMachine>(typeof(CosmeticShopMachine), "instance");
            if (!machineDone && cm != null && Tokens() > 0)
            {
                tokensAtStart = Tokens();
                Go(S.Machine, "cosmetic token available (" + tokensAtStart + ")");
                return;
            }

            // 2) wear whatever is unlocked
            if (!equipDone) { equipDone = true; EquipCosmetics(); }

            // 3) buy upgrades within the budget
            ShopManager sm = Shop();
            ExtractionPoint ep = ShopEp();
            if (sm == null || ep == null) { status = "shop not ready yet"; return; }
            if (!shopInfoLogged)
            {
                shopInfoLogged = true;
                Transform st = R.Get<Transform>(ep, "shopStation"), sbtn = R.Get<Transform>(ep, "shopButton"), mgr = R.Get<Transform>(sm, "extractionPoint");
                GameObject rv = R.Get<GameObject>(ep, "roomVolume");
                string cols = rv == null ? "none" : string.Join("; ", rv.GetComponentsInChildren<Collider>().Select(c => V(c.bounds.center) + " size " + V(c.bounds.size)).ToArray());
                Plugin.Log.LogInfo("shop layout: checkout " + V(ep.transform.position) + " station " + (st != null ? V(st.position) : "none") +
                                   " button " + (sbtn != null ? V(sbtn.position) : "none") + " manager.extractionPoint " + (mgr != null ? V(mgr.position) : "none") +
                                   " roomVolume colliders: " + cols);
            }
            int currency = Currency(), cart = CartCost();
            // the game's money reading does not go down after paying, so remember what we had when we walked in
            // and what we already planned to spend, instead of trusting the live number
            if (shopStart <= 0) shopStart = currency;
            int budget = Mathf.FloorToInt(shopStart * SpendShare) - plannedSpend;

            if (bought < MaxPurchases && budget > 0)
            {
                ItemAttributes best = null;
                float bestScore = float.MinValue; int bestPrice = 0;
                int seen = 0, notWished = 0, tooPricey = 0, atCheckout = 0, banned = 0;
                foreach (ItemAttributes ia in FindObjectsOfType<ItemAttributes>())
                {
                    if (ia == null || !R.Get<bool>(ia, "shopItem")) continue;
                    seen++;
                    if (IsBlacklisted(ia)) { banned++; continue; }
                    int price = R.Get<int>(ia, "value");
                    float weight = Weight(R.Get<string>(ia, "itemName"));
                    if (weight < 1.5f || price <= 0) { notWished++; continue; }
                    if (price > budget) { tooPricey++; continue; }
                    if (InShoppingList(ia)) { atCheckout++; continue; }   // already in the cart
                    // the most useful one that does not eat the whole budget: a cheap good upgrade beats a dear one of the same kind
                    float score = weight * (1f - 0.5f * price / budget);
                    if (score > bestScore) { best = ia; bestScore = score; bestPrice = price; }
                }
                if (best == null)
                    LogEvery(5f, "shop pick found nothing: " + seen + " shop items, " + notWished + " not on the wishlist, " + tooPricey + " over budget (" + budget +
                                 "), " + atCheckout + " already at the checkout, " + banned + " skipped before");
                if (best != null)
                {
                    float len;
                    if (!PathLength(p, best.transform.position, out len))
                    {
                        Plugin.Log.LogInfo("shop: no walkable path to " + R.Get<string>(best, "itemName") + " at " + V(best.transform.position));
                        Blacklist(best, 120f);
                        return;
                    }
                    string kind = KindOf(R.Get<string>(best, "itemName"));
                    if (kind != null) { int kn; boughtKinds.TryGetValue(kind, out kn); boughtKinds[kind] = kn + 1; }
                    target = best; bought++; plannedSpend += bestPrice; grabFails = drops = depositTries = 0; hasViewpoint = false; triedViews.Clear();
                    Go(S.ToValuable, "buy " + R.Get<string>(best, "itemName") + " for " + bestPrice + " (money " + currency + ", cart " + cart + ")");
                    return;
                }
            }

            // 4) pay once our items are in the cart
            if (!paid && (bought > 0 || shopDelivered > 0) && cart > 0 && cart <= currency)
            {
                pressEp = ep;
                Go(S.PayShop, "paying cart " + cart + " of " + currency);
                return;
            }

            status = "shop: done (bought " + bought + ")";
            readyToLeave = true;   // nothing more to buy: head for the truck and, when everybody is in, go on
            if (haveHome) Go(S.Home, "shopping finished");
        }

        void DoPayShop(Vector3 p)
        {
            ExtractionPoint ep = ShopEp();
            if (ep == null) { Go(S.Pick, "no checkout"); return; }
            int cart = CartCost(), currency = Currency();
            if (cart <= 0) { paid = true; CommitBought(); SetFlag(ShopKey()); SayOnce("paid" + lastLevel, "Ho comprato quello che mi serviva nel negozio."); Go(S.Pick, "cart is empty: paid"); return; }
            if (cart > currency) { paid = true; Go(S.Pick, "cannot afford the cart (" + cart + " > " + currency + ")"); return; }

            Transform shopBtn = R.Get<Transform>(ep, "shopButton");
            MeshRenderer btn = R.Get<MeshRenderer>(ep, "button");
            Vector3 bp = shopBtn != null ? shopBtn.position : (btn != null ? btn.bounds.center : ep.transform.position);
            if (Vector3.Distance(Cam().position, bp) > 2.2f)
            {
                status = "walking to the checkout button";
                if (!Follow(bp, 1.2f, true, 540f, 35f)) return;   // true = close enough on the floor plan: go on and press
            }
            AimAt(bp, 540f);
            // the button reacts to grab or to interact (we do not know which): alternate every 3 s
            Held.Add(((Time.time - stateT) % 6f) < 3f ? InputKey.Grab : InputKey.Interact);
            status = "paying " + cart + " of " + currency;
            if (Time.time - stateT > 30f) { paid = true; Go(S.Pick, "pay timed out"); }
        }

        // ---- cosmetic machine ------------------------------------------------------------------
        void DoMachine(Vector3 p)
        {
            CosmeticShopMachine cm = R.GetStatic<CosmeticShopMachine>(typeof(CosmeticShopMachine), "instance");
            if (cm == null) { machineDone = true; Go(S.Pick, "no cosmetic machine"); return; }
            object stObj = R.Get<object>(cm, "stateCurrent");
            string st = stObj == null ? "?" : stObj.ToString();
            int tokens = Tokens();
            status = "cosmetic machine (" + st + ", tokens " + tokens + ")";

            if (tokens < tokensAtStart && st == "Idle")
            {
                machineDone = true; equipDone = false;   // we have a new cosmetic: wear it
                Go(S.Pick, "token used, cosmetic unlocked");
                return;
            }
            if (Time.time - stateT > 60f) { machineDone = true; Go(S.Pick, "machine timed out"); return; }
            if (st != "Idle" && st != "?") return;   // the reward sequence is playing: just wait

            Vector3 mp = cm.transform.position + Vector3.up * 1.2f;
            if (Vector3.Distance(Cam().position, mp) > 2.4f && !Follow(cm.transform.position, 1.4f, true, 540f, 35f)) return;
            AimAt(mp, 360f);
            if (((Time.time - stateT) % 1.5f) < 0.3f) Held.Add(InputKey.Interact);
        }

        // ---- wearing cosmetics -----------------------------------------------------------------
        static CosmeticAsset AssetFrom(object entry, List<CosmeticAsset> assets)
        {
            CosmeticAsset a = entry as CosmeticAsset;
            if (a != null) return a;
            string s = entry as string;
            if (s != null) return assets.FirstOrDefault(x => x != null && (R.Get<string>(x, "assetName") == s || R.Get<string>(x, "assetId") == s));
            if (entry is int)
            {
                int i = (int)entry;
                if (i >= 0 && i < assets.Count) return assets[i];
            }
            return null;
        }

        /// <summary>Equip every cosmetic we own that is not worn yet (the game drops conflicting ones itself).</summary>
        void EquipCosmetics()
        {
            try
            {
                MetaManager mm = R.GetStatic<MetaManager>(typeof(MetaManager), "instance");
                if (mm == null) return;
                List<CosmeticAsset> assets = R.Get<List<CosmeticAsset>>(mm, "cosmeticAssets") ?? new List<CosmeticAsset>();
                var unlocked = new List<CosmeticAsset>();
                System.Collections.IEnumerable raw = R.Get<object>(mm, "cosmeticUnlocks") as System.Collections.IEnumerable;
                if (raw != null) foreach (object e in raw) { CosmeticAsset a = AssetFrom(e, assets); if (a != null) unlocked.Add(a); }
                var worn = new HashSet<CosmeticAsset>();
                System.Collections.IEnumerable eq = R.Get<object>(mm, "cosmeticEquipped") as System.Collections.IEnumerable;
                if (eq != null) foreach (object e in eq) { CosmeticAsset a = AssetFrom(e, assets); if (a != null) worn.Add(a); }

                int changed = 0;
                foreach (CosmeticAsset a in unlocked)
                {
                    if (worn.Contains(a)) continue;
                    if (mm.CosmeticEquip(a, false)) { changed++; Plugin.Log.LogInfo("equipped cosmetic " + R.Get<string>(a, "assetName")); }
                }
                Plugin.Log.LogInfo("cosmetics: " + unlocked.Count + " unlocked, " + worn.Count + " were worn, " + changed + " newly equipped");
                if (changed > 0)
                {
                    mm.Save();
                    mm.CosmeticPlayerUpdateLocal(true, false);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("equip cosmetics failed: " + e); }
        }
    }
}
