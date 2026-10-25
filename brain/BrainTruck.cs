using System;
using System.Linq;
using UnityEngine;

namespace RepoBot
{
    /// <summary>
    /// Moving the game on: when the level is finished (or the shopping is) the bot goes to the truck and,
    /// once the game says every player is inside, presses the truck screen that starts the next level.
    /// It never leaves anyone behind: it waits for SemiFunc.PlayersAllInTruck().
    /// </summary>
    public partial class Brain
    {
        bool readyToLeave;              // the level / the shopping is done
        float allInTruckSince, noAdvanceUntil;
        bool gotoNextCalled;
        int homeFail;                   // times we gave up on walking into the truck

        // ---- the lobby between shop and level: alone we start the next level ourselves; with others the host decides ----
        float lobbySince, nextLobbyTry;
        int lobbyTries;

        void WatchLobby()
        {
            bool inLobby = SemiFunc.RunIsLobby() && Local() != null;
            if (!inLobby) { lobbySince = 0f; lobbyTries = 0; return; }
            if (lobbySince == 0f) lobbySince = Time.time;
            if (!BotInput.Active || SemiFunc.IsMultiplayer()) return;
            if (Time.time - lobbySince < 4f || Time.time < nextLobbyTry || lobbyTries >= 5) return;
            TruckScreenText scr = NearestTruckScreen(Local().transform.position);
            nextLobbyTry = Time.time + 12f;
            lobbyTries++;
            if (scr == null) { Plugin.Log.LogWarning("lobby: no truck screen found to start the next level"); return; }
            Plugin.Log.LogInfo("lobby: starting the next level (try " + lobbyTries + ")");
            scr.GotoNextLevel();
        }

        /// <summary>The game says everybody is in the truck - or we are alone, so there is nobody to wait for.</summary>
        static bool EveryoneReady() { return SemiFunc.PlayersAllInTruck() || !SemiFunc.IsMultiplayer(); }

        static TruckScreenText NearestTruckScreen(Vector3 p)
        {
            TruckScreenText best = null; float bd = float.MaxValue;
            foreach (TruckScreenText t in FindObjectsOfType<TruckScreenText>())
            {
                if (t == null || R.Get<StaticGrabObject>(t, "staticGrabObject") == null) continue;
                float d = (t.transform.position - p).sqrMagnitude;
                if (d < bd) { bd = d; best = t; }
            }
            return best;
        }

        /// <summary>Called every frame while waiting at home: when everybody is in the truck for a few seconds, go on.</summary>
        void CheckAdvance()
        {
            if (!readyToLeave || Time.time < noAdvanceUntil) { allInTruckSince = 0f; return; }
            if (EveryoneReady())
            {
                if (allInTruckSince == 0f) allInTruckSince = Time.time;
                if (Time.time - allInTruckSince > (SemiFunc.IsMultiplayer() ? 5f : 2f)) { gotoNextCalled = false; Go(S.Advance, "everybody is ready"); }
            }
            else allInTruckSince = 0f;
        }

        void DoAdvance(Vector3 p)
        {
            Held.Remove(InputKey.Grab);
            TruckScreenText scr = NearestTruckScreen(p);
            if (scr == null) { noAdvanceUntil = Time.time + 30f; Go(S.Home, "no truck screen found"); return; }
            if (!EveryoneReady()) { Go(S.Home, "somebody left the truck"); return; }

            SayOnce("advance" + lastLevel, ShopMode ? "Siamo tutti pronti: lascio il negozio!" : "Siamo tutti pronti: si va avanti!");
            GameObject col = R.Get<GameObject>(scr, "staticGrabCollider");
            Vector3 sp = col != null ? col.transform.position : scr.transform.position;
            status = "going on to the next level";

            // Measured: pressing the screen with the keys did not react; the screen's own GotoNextLevel() does. Use it after a moment.
            if (!gotoNextCalled && Time.time - stateT > 3f)
            {
                gotoNextCalled = true;
                Plugin.Log.LogInfo("calling the truck screen's GotoNextLevel()");
                scr.GotoNextLevel();
            }
            if (Vector3.Distance(Cam().position, sp) > 2.2f && !Follow(sp, 1.2f, false, 540f, 35f)) { if (Time.time - stateT > 30f) { noAdvanceUntil = Time.time + 60f; Go(S.Home, "could not start the next level"); } return; }
            AimAt(sp, 540f);
            Held.Add(((Time.time - stateT) % 6f) < 3f ? InputKey.Grab : InputKey.Interact);
            if (Time.time - stateT > 30f) { noAdvanceUntil = Time.time + 60f; Go(S.Home, "the truck screen did not start the next level"); }
        }
    }
}
