using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Photon.Pun;
using UnityEngine;

namespace RepoBot
{
    /// <summary>
    /// Bought upgrades are useless until they are used. After paying, the game spawns them in the truck at the start
    /// of the next level; using one means toggling its ItemToggle (which runs the item's Upgrade()).
    /// Alone we use every upgrade. In a game with other people we only use as many of each kind as WE bought, so we
    /// never consume an upgrade a teammate paid for.
    /// </summary>
    public partial class Brain
    {
        readonly List<string> boughtPending = new List<string>();   // names of items we put in the cart, not paid yet
        float nextUpgradeCheck;

        string OursKey(string item) { return SessionId() + "|" + item; }

        /// <summary>We delivered a shop item into the cart: remember it until the payment goes through.</summary>
        void NoteBought(Component item)
        {
            string name = R.Get<string>(item, "itemName");
            object type = R.Get<object>(item, "itemType");
            if (!string.IsNullOrEmpty(name) && type != null && type.ToString() == "item_upgrade") boughtPending.Add(name);
        }

        /// <summary>The cart was paid: the items we put in it are ours to use.</summary>
        void CommitBought()
        {
            foreach (string name in boughtPending) AddOurs(name, 1);
            if (boughtPending.Count > 0) Plugin.Log.LogInfo("upgrades bought by us: " + string.Join(", ", boughtPending.ToArray()));
            boughtPending.Clear();
        }

        Dictionary<string, int> LoadOurs()
        {
            var d = new Dictionary<string, int>();
            try
            {
                string f = Dir + "ours.txt";
                if (!File.Exists(f)) return d;
                foreach (string line in File.ReadAllLines(f))
                {
                    int eq = line.LastIndexOf('=');
                    int n;
                    if (eq > 0 && int.TryParse(line.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) d[line.Substring(0, eq)] = n;
                }
            }
            catch (Exception) { }
            return d;
        }

        void SaveOurs(Dictionary<string, int> d)
        {
            try { File.WriteAllLines(Dir + "ours.txt", d.Where(kv => kv.Value > 0).Select(kv => kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture)).ToArray()); }
            catch (Exception e) { Plugin.Log.LogWarning("ours.txt: " + e.Message); }
        }

        void AddOurs(string item, int delta)
        {
            Dictionary<string, int> d = LoadOurs();
            int n; d.TryGetValue(OursKey(item), out n);
            d[OursKey(item)] = Mathf.Max(0, n + delta);
            SaveOurs(d);
        }

        /// <summary>Use the upgrades lying around (in the truck after a shop). Called every frame, works about once a second.</summary>
        void UseOwnedUpgrades()
        {
            if (ShopMode || Time.time < nextUpgradeCheck || !BotInput.Active) return;
            nextUpgradeCheck = Time.time + 1.0f;
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            PhotonView pv = R.Get<PhotonView>(pa, "photonView");
            if (pa == null || pv == null || Dead()) return;

            bool alone = !SemiFunc.IsMultiplayer();
            Dictionary<string, int> ours = alone ? null : LoadOurs();
            foreach (ItemAttributes ia in FindObjectsOfType<ItemAttributes>())
            {
                if (ia == null || R.Get<bool>(ia, "shopItem")) continue;
                object type = R.Get<object>(ia, "itemType");
                if (type == null || type.ToString() != "item_upgrade") continue;
                ItemUpgrade iu = ia.GetComponent<ItemUpgrade>();
                ItemToggle tg = ia.GetComponent<ItemToggle>();
                if (tg == null || R.Get<bool>(iu, "upgradeDone") || R.Get<bool>(tg, "toggleState") || R.Get<bool>(tg, "disabled")) continue;

                string name = R.Get<string>(ia, "itemName");
                if (!alone)
                {
                    int n; if (!ours.TryGetValue(OursKey(name), out n) || n <= 0) continue;   // a teammate's upgrade: leave it
                    AddOurs(name, -1);
                }
                Plugin.Log.LogInfo("using upgrade: " + name);
                tg.ToggleItem(true, pv.ViewID);
                SayOnce("upgrade" + ia.GetInstanceID(), "Uso l'upgrade: " + name + "!");
                nextUpgradeCheck = Time.time + 0.6f;   // one at a time
                return;
            }
        }
    }
}
