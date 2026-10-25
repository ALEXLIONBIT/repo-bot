using System.Collections;
using System.Linq;
using UnityEngine;

namespace RepoBot
{
    /// <summary>
    /// Playing nicely with the others: never take what a teammate holds or has put in a cart, and carry things gently
    /// (slow, no jumping, no sudden turns) so they are not knocked about, broken or devalued.
    /// </summary>
    public partial class Brain
    {
        float moveScale = 1f;   // 1 = walk at full speed; lower while carrying
        PhysGrabCart[] carts = new PhysGrabCart[0];
        float nextCartScan;

        /// <summary>A valuable somebody else is holding, or that lies in (or on) a cart: leave it alone.</summary>
        bool TakenByOthers(Component v)
        {
            if (v == null) return false;
            PhysGrabObject pgo = R.Get<PhysGrabObject>(v, "physGrabObject");
            PhysGrabber mine = Grabber();
            if (pgo != null)
            {
                IEnumerable grabbers = R.Get<object>(pgo, "playerGrabbing") as IEnumerable;
                if (grabbers != null)
                    foreach (object g in grabbers)
                        if (g != null && g != (object)mine) return true;   // someone else has hold of it
            }
            if (Time.time >= nextCartScan)
            {
                nextCartScan = Time.time + 2f;
                carts = FindObjectsOfType<PhysGrabCart>();
            }
            foreach (PhysGrabCart c in carts)
            {
                if (c == null) continue;
                IEnumerable inside = R.Get<object>(c, "itemsInCart") as IEnumerable;
                if (inside != null && pgo != null)
                    foreach (object o in inside) if (o == (object)pgo) return true;
                Transform vol = R.Get<Transform>(c, "inCart");
                Vector3 at = vol != null ? vol.position : c.transform.position;
                Vector3 d = v.transform.position - at;
                if (new Vector2(d.x, d.z).magnitude < 1.6f && d.y > -0.8f && d.y < 1.8f) return true;   // in or on the cart
            }
            return false;
        }
    }
}
