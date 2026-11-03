# Inventory and found items

The player inventory has no slot capacity limit. Players start empty, and collecting items adds as many stacks as needed. Each stack still obeys its item's stack limit. Pickups top up existing matching stacks, reuse empty slots, then create new slots for the remainder; valid pickups are collected in full. LMB on snow in inventory mode spends one item from the selected stack. LMB on a placed accessory takes it back, creating another stack when necessary.

**Tab** equips the selected accessory in the player's hand, in front and to the right of the camera. There is no floating inventory image, item name, quantity badge, background panel, or surface ghost. **Scroll** cycles occupied stacks and skips empty ones. The hand stays in its holding pose while aiming at snow. With no selected item, nothing is held.

Clicking a snow surface starts a short placement reach. The held model moves with the hand, the clicked surface location stays fixed relative to its sculpture, and the item is placed and spent at contact. The empty hand then presses and returns before holding the next item. Selection and additional clicks are blocked during that motion. Closing inventory, unlocking the cursor before contact, or losing the target cancels placement without spending an item. Lights and scarves retain their live strand/anchor workflow; anchor clicks trigger the hand reach.

| Item | Stack limit | Nearby supply quantity |
| --- | ---: | ---: |
| Twig | 12 | 4 |
| Carrot | 6 | 2 |
| Button | 24 | 8 |
| Pebble | 24 | 8 |
| Lights | 3 | 1 |
| Scarf | 2 | 1 |

Stack limits live on `AccessoryCatalog.Entry.StackLimit` or the corresponding `AccessoryItemDefinition`. Inventory adds stacks as needed. `HeldAccessoryView` uses the actual item mesh at its authored scale, disables its colliders, and follows the solved palm after IK. The wrist matches both fingertip direction and palm normal, with the grip in the palm rather than at the wrist joint. Item facing stays independent of wrist rotation. Palm orientation is also included in replicated hand intent. The normal carry hand is the right; a one-handed snowball leaves the left hand available for accessories, and carrying a two-handed snow object hides the accessory. This local held representation is not a saved or replicated placed prop; completed placements use the existing save and network path.

The camera-relative hold offset and item orientation, palm rotation relative to the item (`accessoryPalmEuler`), placement time, and return time are tunable in `SculptFeelConfig` under **Held accessories**. Hand spring/stretch settings remain in the existing Hands section. **Inventory selection** controls scroll sensitivity: the default requires one full wheel step, allows a switch every 0.2 seconds, and discards incomplete scroll gestures after 0.25 seconds of inactivity. Scroll input during placement or the switching cooldown is discarded rather than queued.

Lights and scarves each cost one item per completed strand, regardless of the number of anchor points. Shift+LMB or RMB finishes a strand. Closing the overlay also finishes a strand with at least two points. One-point strands and explicitly cancelled strands spend nothing. Selection stays locked during a strand; a live strand cannot be retrieved.

## Placing supplies in a level

Ready-made, editor-visible pickups live under `Assets/Props/Carrots/Pickups`, `Assets/Props/Sticks/Pickups`, and `Assets/Props/Accessories/Pickups`. The Accessories folder contains **Scarf_Pickup**, **Lights_Pickup**, **Button_Pickup**, and **Pebble_Pickup**. Drag them into a scene, position their roots on the ground, and adjust `ItemPickup.quantity` as needed (default one). Their reusable preview models and persistent materials live in the sibling Prefabs and Materials folders. These four pickups use the existing catalog IDs and stack limits; scarves and lights still place as live strands. Rebuild them with **Snowfield > Items > Build Accessory Pickup Prefabs**.

Add `ItemPickup` to an empty GameObject, set `itemId` to a catalog id and `quantity` to the stack quantity, and position its origin at the ground surface. At runtime it builds the existing accessory model and a generous trigger for cursor targeting. A pickup prefab can instead assign `worldModel` to an already visible child model; startup reuses it. Pickups do not block walking or snowballs. Built-in IDs are `twig`, `carrot`, `button`, `pebble`, `lights`, and `scarf`; prefab definitions add more item types.

Prefab-backed items are `AccessoryItemDefinition` assets under `Assets/Resources/Items`. Create one with **Create > Snowfield > Accessory Item**, assign a unique id, display name, stack limit, and visual prefab. Each prefab points +Y out from the snow surface, with its base at the origin, and includes a collider for retrieval. An optional direct child named **Grip** sets the in-hand grip position and palm rotation relative to the item (+Y out of the palm, +Z towards the fingertips); otherwise the position is inferred from model bounds and the palm uses the item axes. Definitions with an existing built-in id replace that item's visual while preserving save/network identity.

Existing scenes automatically get `PlayerInventory` on their `AccessoryPlacer` object and an `ItemPickupSpawner` on their `SculptTool` object. The spawner places a finite supply fan three metres in front of the player after the ground is ready; it never refills. Add `ItemPickupSpawner` to the tool in the scene and disable `spawnNearbySupplies` when using hand-placed supplies instead. Its default quantities are one-third of each item's stack limit, with a minimum of one.

## Current scope

Inventory and loose pickups last for the current scene/session and reset on a fresh launch. Sculpture save/load remains unchanged. Inventories and loose supply pickups are personal to each player; placed accessories continue to use the existing shared sculpture replication. Remote placement and save loading use `PlaceEntry` and do not spend the receiving player's inventory. Shared contested pickups and inventory persistence are not part of this first version.
