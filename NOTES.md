# R.E.P.O. bot - what was learned

## Tutorial (brain/BrainTutorial.cs, command `tutorial` in run/cmd.txt)
- Starts from the main menu (`MenuPageMain.ButtonEventTutorial`) or restarts from inside a level.
- A corridor along +x at z=0: 35 pages, one room per page, a `TutorialDoor` at the end of each room (x of door N = end of room N).
  Doors are one-way: the player cannot walk back (a conveyor pushes them on), so the cart must be taken through each door.
- Pages 0-12 work: movement, jump (Space), sneak (Crouch), crawl under a mattress (patrol under it), sprint, tumble (Q), grab, push/pull
  (the game reads `InputManager.KeyPullAndPush`, patched from the brain), rotate (hold Rotate), item toggle (Interact), inventory fill/empty
  (grab item + Inventory1/2/3, pulse the key, 1 s cooldown), map (hold Map).
- Page 13-15 (cart, load the cart, extraction): the cart is steered only through its handle box (`physGrabObjectGrabArea`, a side of the cart),
  and only when `cartActive` is on (a fresh grab press on the handle). Not yet reliable. The bot must keep the cart in hand and push it east through
  each door right after the page completes. A 70 s no-progress fallback calls `TutorialDirector.NextPage()`.
- The tutorial level has no walkable NavMesh: `WatchPosition` must stay off there (it teleported the bot back every 3 s).
- Objects: use `PhysGrabObject.centerPoint` to aim, not a child collider.

## Multiplayer lessons
- `PlayerAvatar.ReviveRPC` is master-only: a client can never revive itself. The legit way: the dead player drives their death head into the truck
  (take over the head with Interact, aim, hold Jump to charge, release to hop). Written in BrainHead.cs, untested.
- Teleport cheats never work for a client; the verification must keep ONE pending check or it never fires. Cheats are off by default, C toggles.
- Do not take items held by others or in a cart (BrainManners.cs); carry slowly and calmly; never write "bot" in chat.
- Shooters (Huntsman) kill from range: count line of sight. Flee towards cover, not just away; a dead end must never leave the bot standing still.
- Only blame a teammate after two real hits (>= 8 damage) from someone within 4 m and no monster near; then only run away, never fight.

## Ideas not done
- Use Q (tumble) as an escape move in normal play.
- Buy medkits and use them; carry-time obstacle avoidance.
- Finish tutorial pages 13-16 and test singleplayer without cheats.
