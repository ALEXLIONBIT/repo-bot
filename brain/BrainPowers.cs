using UnityEngine;
using UnityEngine.AI;

namespace RepoBot
{
    /// <summary>
    /// "Superpowers", on the user's request: teleport the player next to whatever it wants to reach,
    /// and teleport a grabbed object straight into the extraction point. Both use the game's own
    /// teleport functions (PlayerAvatar.Spawn, PhysGrabObject.Teleport). Switch off with 'superpowers off'.
    /// </summary>
    public partial class Brain
    {
        bool superpowers = false;   // C in the game switches it
        float nextTeleport, nextObjTeleport;

        // The game only honours these teleports in some situations (singleplayer / host). We check that each one really
        // moved something and, after two misses in a row, stop trying and go back to walking and carrying.
        bool playerTpOk = true, objectTpOk = true;
        int playerTpFails, objectTpFails;
        Vector3 tpDest, objDest;
        float tpCheckAt, objCheckAt;
        Component objTarget;

        // With other players in the game nothing is teleported: friends would see it, so the bot plays like a person.
        bool PlayerTp { get { return superpowers && playerTpOk && !SemiFunc.IsMultiplayer(); } }

        void VerifyTeleports()
        {
            if (tpCheckAt > 0f && Time.time >= tpCheckAt)
            {
                tpCheckAt = 0f;
                PlayerController pc = Local();
                if (pc != null)
                {
                    if (Vector3.Distance(pc.transform.position, tpDest) > 6f)
                    {
                        if (++playerTpFails >= 3 && playerTpOk && SemiFunc.IsMultiplayer())
                        {
                            playerTpOk = false;
                            Plugin.Log.LogWarning("player teleport has no effect in this game (probably not the host): walking instead");
                        }
                    }
                    else playerTpFails = 0;
                }
            }
            if (objCheckAt > 0f && Time.time >= objCheckAt)
            {
                objCheckAt = 0f;
                if (objTarget != null)
                {
                    if (Vector3.Distance(objTarget.transform.position, objDest) > 4f)
                    {
                        if (++objectTpFails >= 3 && objectTpOk && SemiFunc.IsMultiplayer())
                        {
                            objectTpOk = false;
                            Plugin.Log.LogWarning("object teleport has no effect in this game: carrying objects to the extraction instead");
                        }
                    }
                    else objectTpFails = 0;
                }
            }
        }

        /// <summary>Teleport the player onto the walkable floor near dest. False if not allowed / no floor there.</summary>
        bool TeleportPlayer(Vector3 dest, string why)
        {
            if (!PlayerTp || Time.time < nextTeleport) return false;
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            if (pa == null) return false;
            NavMeshHit h;
            // prefer a floor spot right at the destination, else the closest walkable one within 8 m (we then walk the rest)
            if (!NavMesh.SamplePosition(dest, out h, 3f, NavMesh.AllAreas) && !NavMesh.SamplePosition(dest, out h, 8f, NavMesh.AllAreas)) return false;
            Transform c = Cam();
            Quaternion rot = Quaternion.Euler(0f, c != null ? c.eulerAngles.y : 0f, 0f);
            pa.Spawn(h.position + Vector3.up * 0.2f, rot);
            if (tpCheckAt <= 0f) { tpDest = h.position; tpCheckAt = Time.time + 1.2f; }   // one pending check at a time, or it never fires
            nextTeleport = Time.time + 0.6f;
            nextRepath = 0f;
            Plugin.Log.LogInfo("teleport (" + why + ") to " + V(h.position));
            return true;
        }

        /// <summary>Teleport to an exact spot, no NavMesh needed (for the truck's own spawn point, which is not on the NavMesh).</summary>
        bool TeleportExact(Vector3 dest, string why)
        {
            if (!PlayerTp || Time.time < nextTeleport) return false;
            PlayerAvatar pa = R.GetStatic<PlayerAvatar>(typeof(PlayerAvatar), "instance");
            if (pa == null) return false;
            Transform c = Cam();
            Quaternion rot = Quaternion.Euler(0f, c != null ? c.eulerAngles.y : 0f, 0f);
            pa.Spawn(dest + Vector3.up * 0.1f, rot);
            if (tpCheckAt <= 0f) { tpDest = dest; tpCheckAt = Time.time + 1.2f; }
            nextTeleport = Time.time + 0.8f;
            nextRepath = 0f;
            Plugin.Log.LogInfo("teleport (" + why + ") exactly to " + V(dest));
            return true;
        }

        /// <summary>Teleport the object we hold into the extraction point (or the shop checkout).</summary>
        bool TeleportHeldObject(ExtractionPoint ep)
        {
            if (ep == null) return false;
            return TeleportHeldObjectTo(ShopMode ? ShopDrop(ep) + Vector3.up * 0.6f : AreaDrop(ep));
        }

        /// <summary>Teleport the object we hold to an exact spot (the game's PhysGrabObject.Teleport).</summary>
        bool TeleportHeldObjectTo(Vector3 drop)
        {
            if (!superpowers || !objectTpOk || SemiFunc.IsMultiplayer() || Time.time < nextObjTeleport || target == null) return false;
            PhysGrabObject pgo = R.Get<PhysGrabObject>(target, "physGrabObject");
            if (pgo == null) return false;
            if (Vector3.Distance(target.transform.position, drop) < 1f) return false;
            pgo.Teleport(drop, target.transform.rotation);
            Rigidbody trb = R.Get<Rigidbody>(pgo, "rb");
            if (trb != null) { trb.velocity = Vector3.zero; trb.angularVelocity = Vector3.zero; }   // no speed, no impact damage
            objTarget = target; objDest = drop; objCheckAt = Time.time + 1.2f;
            nextObjTeleport = Time.time + 0.5f;
            Plugin.Log.LogInfo("object teleported into the " + (ShopMode ? "shop checkout" : "extraction point") + ": " + target.name);
            return true;
        }
    }
}
