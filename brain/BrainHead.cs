using UnityEngine;

namespace RepoBot
{
    /// <summary>
    /// Dead in somebody else's game nobody can revive us from here (only the host's game may), but the game revives a player whose
    /// death head is inside the truck. As a spectator the head can be driven like a player would: press Interact to take over the head,
    /// aim the camera, hold Jump to charge and let go to hop that way. No cheat, only the normal controls.
    /// </summary>
    public partial class Brain
    {
        bool headMove = true;
        float headNextEnter, headPauseUntil, headCheckT, headBiasUntil, headHoldFrom;
        Vector3 headCheckPos;
        float headBias;
        int headTries;

        void HeadStep()
        {
            if (!headMove || !BotInput.Active || SemiFunc.IsMasterClientOrSingleplayer()) return;
            PlayerAvatar me = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            PlayerDeathHead head = R.Get<PlayerDeathHead>(me, "playerDeathHead");
            SpectateCamera sc = R.GetStatic<SpectateCamera>(typeof(SpectateCamera), "instance");
            CameraAim ca = R.GetStatic<CameraAim>(typeof(CameraAim), "Instance");
            if (head == null || sc == null || ca == null || !head.gameObject.activeInHierarchy) return;

            object st = R.Get<object>(sc, "currentState");
            if (st == null || st.ToString() != "Head")
            {
                // take over the head (Interact) as soon as it has the energy for it
                if (Time.time >= headNextEnter && R.Get<float>(sc, "headEnergy") > 0.3f)
                {
                    headNextEnter = Time.time + 1.5f;
                    Held.Add(InputKey.Interact);
                    Plugin.Log.LogInfo("head: taking over my death head");
                }
                return;
            }

            TruckSafetySpawnPoint sp = R.GetStatic<TruckSafetySpawnPoint>(typeof(TruckSafetySpawnPoint), "instance");
            Vector3 target = haveHome ? home : (sp != null ? sp.transform.position : head.transform.position);
            Vector3 hp = head.transform.position;
            Vector3 to = target - hp; to.y = 0f;
            float dist = to.magnitude;
            status = "dead: rolling my head to the truck (" + dist.ToString("0") + "m)";
            if (dist < 2.5f) return;   // in the truck: the game revives us from here

            // no progress for a while: try another way round the obstacle
            if (Time.time - headCheckT > 3f)
            {
                if (headCheckT > 0f && (hp - headCheckPos).magnitude < 0.8f)
                {
                    headTries++;
                    float[] bias = { 50f, -50f, 100f, -100f, 150f, -150f, 0f };
                    headBias = bias[headTries % bias.Length];
                    headBiasUntil = Time.time + 2.5f;
                }
                headCheckT = Time.time; headCheckPos = hp;
            }
            if (Time.time > headBiasUntil) headBias = 0f;

            float wantYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg + headBias;
            float ah = R.Get<float>(ca, "aimHorizontal"), av = R.Get<float>(ca, "aimVertical");
            float step = 360f * Time.deltaTime;
            ah = Mathf.MoveTowardsAngle(ah, wantYaw, step);
            R.Set(ca, "aimHorizontal", ah);
            R.Set(ca, "aimVertical", Mathf.MoveTowards(av, -30f, step));   // a little upward: the hop needs lift
            if (Mathf.Abs(Mathf.DeltaAngle(ah, wantYaw)) > 15f) return;   // finish turning first

            // charge the jump on the ground, let go to hop
            bool grounded = R.Get<bool>(head, "spectatedJumpGrounded", true);
            float charge = R.Get<float>(head, "spectatedJumpChargeAmount");
            float max = R.Get<float>(head, "spectatedJumpChargeAmountMax");
            if (Time.time < headPauseUntil || !grounded) { headHoldFrom = 0f; return; }
            if (headHoldFrom == 0f) headHoldFrom = Time.time;
            bool full = max > 0.01f ? charge >= max * 0.85f : Time.time - headHoldFrom > 0.8f;
            if (full || Time.time - headHoldFrom > 2.5f) { headPauseUntil = Time.time + 0.6f; headHoldFrom = 0f; return; }
            Held.Add(InputKey.Jump);
        }
    }
}
