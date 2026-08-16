## 0.1.0 | 16 August 2026

Initial release. Graveyard Keeper played entirely by ear — menus, dialogue, the world,
crafting, the graveyard, the dungeon and the DLC content are all narrated through a screen
reader. Developed 3 June – 16 August 2026 over 151 commits.

**Requires BepInEx 5.4.x (Windows x64).**

### Speech output

- Speaks through **Prism**, which drives NVDA, JAWS, Orca, Voiceover and others directly, and falls back to SAPI when no screen reader is running.
- **Braille support** — text goes to speech and a braille display in a single call on
  backends that support it.
- Prism ships with the mod (Windows, Linux and macOS binaries), so there is nothing extra
  to install.
- Full UTF-8, so German umlauts and other non-ASCII characters are spoken correctly.

### Menus and interface

- Main menu, title screen, new game and save slots; save slots can be loaded and deleted.
- Pause menu (Escape), which says the game is paused and explains what each entry does —
  including that leaving to the main menu does *not* save.
- Controls page, with every key binding read aloud, rebindable from the keyboard, and a
  reset-to-defaults entry.
- All option menus, with sliders adjustable by keyboard.
- Inventory and chests, including item quality stars, tool and weapon durability, food
  buffs, and what an item decomposes into. Empty containers announce that they are empty.
- Hotbar/quick-use slots, assignable from the inventory.
- Items can be destroyed from the inventory.
- Tutorial popups and new-technology popups read their full text and close by keyboard.
- Yes/no confirmation boxes list the question itself as a row alongside the options, so it can
  be read again after moving to Yes or No.
- Technology tree, including perk descriptions and why a technology is locked.
- NPC and quest menus, church sermon and donation reports, and the time machine.

### Dialogue

- Speech bubbles and NPC dialogue are spoken.
- Multiple-choice dialogue is keyboard-navigable; disabled options say *why* they are
  disabled instead of going silent.
- Nested dialogue no longer traps the player, and duplicate options are filtered out.
- Cutscenes announce themselves and can be advanced from the keyboard.

### World navigation

- Object navigator with categories — landmarks, doors, NPCs, vendors, harvestables,
  buildables, storage, graves, fishing spots, corpses, quests and more.
- Auto-walk with pathfinding, including long-distance travel across the map, exit
  assistance and a compass fallback.
- Announces the area you enter, and what is blocking a tile you cannot reach.
- Objects inside buildings stay listed while you are in the room, and outdoor objects are
  hidden when you are indoors — no x-ray effect.
- Content from DLC you do not own is filtered out of the object list.

### Interaction, crafting and building

- Approach and interaction announcements, with a consistent key for context actions.
- Craft stations read recipes, tabs, missing materials, ingredient quality requirements and
  predicted star-quality odds; queued crafts and multi-crafting work.
- Alchemy tables, the combining table, the organ enhancer, the soul healer and remote craft
  control are all navigable.
- Station upgrades and repairs announce their materials and what is still missing.
- Grave building with ghost placement, a readable catalog, decoration points, and demolish
  mode; furniture and wall decorations can be placed indoors.
- Zombie stations: work efficiency, skull counts, assignment, craft queues, the porter
  transport station, and crate/pallet handling.

### Graveyard

- Grave decoration, fence repair, exhumation and river disposal.
- Empty, diggable and decoratable graves each have their own category.
- Autopsy table with per-part skull values, and announcements of what adding or removing a
  part will do.
- Church sermons and donation collection.

### Dungeon and combat

- Reveals the whole dungeon level at once, with separate categories for enemies,
  destructibles and mining veins.
- Auto-aim, toggleable auto-attack, attack-nearest and enemy scanning, with hit and death
  feedback.
- Toggleable auto-eat and auto-drink at low health or energy.
- Both dungeon exits are distinguished, with an emergency key to walk out, plus a safety net
  for getting wedged off the navigation mesh.

### Feedback

- Item pickups, technology points, health and energy changes, skull changes, and buffs.
- Game saves and Steam achievements.
- Day and time, money, zone ratings, current quest and active quest list on their own keys.

### Fishing

- Auto-catch with narration of state, bait and cast; bait is selectable while fishing.

### Localisation

- All spoken text is localised. English and German are complete; Spanish, French, Italian
  and Russian are stubbed.

### Known limitations

- **The BepInEx config menu (F1) is not accessible.** Opening it breaks NGUI keyboard input —
  arrows and Escape stop responding and speech stops — because BepInEx's Configuration Manager
  and the game's UI both poll the keyboard at the same time. The feature is disabled rather
  than left half-working; everything else is reachable without it.
- **The game's opening intro is not narrated.** Its subtitles are drawn by a separate system
  that is not hooked yet.
- **Linux and macOS are untested.** The Prism speech libraries for both are bundled, but
  nobody has confirmed yet that BepInEx loads on the game's native Mac and Linux builds. Windows
  is verified. Linux players using Proton run the Windows build, which works, but Prism cannot
  reach a Linux screen reader such as Orca from inside Proton.
- **Only English and German are fully translated.** Spanish, French, Italian and Russian
  files exist but are near-empty and fall back to English.
- **The quest category only lists quests that have a map marker.** Quests without one are not
  listed — that is how the game itself tracks them, not something the mod can add.

---

## Development history

Every dated change, oldest first.

### June 2026

- **03 Jun** — Main menu readable through the screen reader. All other menus patched; options menu improved.
- **04 Jun** — Title screen, new game button and save slots accessible. Improved button discovery and UI element discovery; smart deduplication of discovered elements. Scene and dialogue logging added for debugging. First dialogue capture hook via reflection. First attempt at keyboard-adjustable sliders. Attempted Configuration Manager accessibility (unsuccessful, left disabled).
- **06 Jun** — Object detection added; experimental walking click-sounds.
- **07 Jun** — Doors announced correctly. Dialogue speaking fixed. Sliders working.
- **09 Jun** — Inventory readout. Rudimentary auto-walker.
- **10 Jun** — Switched from Tolk to **Prism** for screen reader detection.
- **11 Jun** — Pathfinder and quest detection fixed.
- **12 Jun** — Corpses trackable and collectable. Crafting made accessible, including no longer getting stuck at a table when a craft did not finish. Table interaction reworked around a consistent key. Health, energy, skulls, time of day and points announceable; graves prepared in the object tracker. Menus, inventory and chests refactored to report when empty. Grave crafting and placement fixed.
- **13 Jun** — Correct day announced; shortcut to hear the active quest. Multiple-choice dialogue made keyboard-navigable (previously a dead end). Buying, selling and trading made accessible.
- **14 Jun** — Trading fixed again; pathfinder overhauled. Door pointers in the landmarks category corrected. Inventory interactable objects fixed. Dialogue options and their greyed-out state read. Item qualities read out. Quest and NPC menus read out. Skull changes announced immediately. Fence blocking fixed; exhumations and river disposal announced. Money moved to its own key. Stale quest-readout hint removed. Crafting stations reading fixed.
- **19 Jun** — Technology tree made accessible. Repair stations fixed; categories added for bushes and other collectables. Trading fixed. Greyed-out entries now spoken, and stations announce what is missing to craft or build. Buildings made removable; blocked tiles announce what blocks them. Points and inventory additions announced. Tutorial and unlocked-technology announcements fixed. Machine and table repair fixed, including disabled repair actions. Skull readout fixed. NPC and quest menu readouts fixed.
- **20 Jun** — Graves fully accessible, including decoration. Furnaces accessible and reporting contents. DLC zones filtered out when the DLC is not installed; destroyed objects cleared. Navigation lands exactly on interaction spots.
- **21 Jun** — Map changes announced. Missing furnace fuel announced. Misleading "craft in progress" messages removed for auto-crafts. Tavern door corrected in the pathfinder. Health and energy changes announced. Speech bubbles no longer interrupt. Buildables that would otherwise vanish added to navigation. Phantom chest items no longer read in the inventory.
- **23 Jun** — Vendor money fixed; dungeon entry announced. Missing entries restored to object navigation. Table interaction at range fixed. Technology tree announces perk descriptions. Nested dialogue no longer gets stuck, and greyed-out threads give a reason. Alchemy table reads its requirements. Object tracker categories reorganised. Removed the x-ray effect of seeing distant objects from inside buildings.
- **24 Jun** — Further x-ray removal; science hardcoded on the alchemy table where it could not be read. Sermons and money collection fixed. Health/energy buffs added; unused function removed to keep inventory readouts clean; active quest list bound to a key. Crafting more than one item at a time fixed. Royal services mailbox fixed. Alchemy tables and study collection fixed. Inventory items can be destroyed.
- **25 Jun** — Study rewards fixed again; new category for mushrooms.
- **26 Jun** — Corpse finding improved. Body parts announce their values. Further study-collection work. Fixed the interaction key not always targeting the right object.
- **27 Jun** — Church door landing corrected. Combining table made accessible. Announces what adding or removing a body part will do. DLC objects no longer appear without the DLC. Dungeon levels read properly; breakables recategorised.
- **29 Jun** — Fixed triggering a game bug when stepping past an available craft quality, which could wedge the whole crafting process.

### July 2026

- **03 Jul** — Removed stale "craft in progress, please stand still" messages. **Fishing made accessible**, including an animation hang and a conflict with another fishing mod.
- **04 Jul** — Further auto-walk interaction targeting. Walking after teleports. Wall placement in the church. Save and Steam achievement announcements.
- **05 Jul** — Durability readout. Misleading "upgradable" message on empty garden beds. Beehives recategorised. False "enemy defeated" when an enemy simply left the screen. **Toggleable auto-attack and auto-eat for the dungeon.** Breakable detection fixed. Whole dungeon level revealed at once. Item naming corrections.
- **09 Jul** — Toolbar/hotbar made accessible. Incorrect point rewards corrected.
- **10 Jul** — Bait selectable during fishing.
- **11 Jul** — Short indoor walks after teleporting or sleeping. Mass combat swinging into thin air and draining energy without hitting anything. Crafting stations listed at long distance.
- **12 Jul** — Unowned DLC content leaking into the object tracker. Player tavern content leaking in. Buffet quality requirements. Interrupted requirement readouts in craft menus. Message when something cannot be built on a wall.
- **14 Jul** — Graves not exhumable; regression preventing placement of a cremation site.
- **15 Jul** — Disabled dialogue options now say why. Building furniture inside the house.
- **16 Jul** — Alchemy table. Location announcements, and decoration points of built objects. Build messages when removing things. Incorrect narration when removing items in the cellar.
- **18 Jul** — Pallet and crate merchant flow. Resurrection table. Transport station accessibility. Object tracker refresh lag. Zombie efficiency and skulls read out. Vendors category added, including the egg seller. Dungeon breakables fixed and enemies given their own category.
- **19 Jul** — Zombie mines renamed. Zombie efficiency readout in the object tracker.
- **21 Jul** — Dungeon exits distinguished; emergency exit key to get unstuck.
- **22 Jul** — Diamonds, gold and silver finally findable in the dungeon.
- **24 Jul** — Marble mines shown properly; zombie mines category added.
- **26 Jul** — Quality requirements spoken at zombie stations. Inventory and chest interaction refactored for efficiency. Decorative build objects read out.
- **28 Jul** — Graves reachable from outside the morgue; objects inside a room stay visible. Technology tree refactored. Fountain placement failing due to wrong rotation.
- **30 Jul** — Snake meeting point added to the quest category.
- **31 Jul** — Getting stuck in dungeons with no way back; safety net rewritten.

### August 2026

- **02 Aug** — Zone scores spoken in DLC areas. Required crafting materials not always read. Cutscene triggers adjusted; Enter added to skip pauses by keyboard.
- **06 Aug** — Bag (Universalbeutel) accessibility. **Soul healer made accessible.**
- **08 Aug** — Stained glass window placement. Quest markers for the snake and the ghost. Clotho's memory quest accessibility, and her name in the quest text.
- **09 Aug** — Items and NPCs with a speech bubble given their own category.
- **12 Aug** — Map and soul remote crafting made accessible.
- **13 Aug** — Broken build areas.
- **14 Aug** — Tavern event popups. Duplicate dialogue options. Object navigator refactored and sped up. Items read what they decompose into. Time machine buttons, and a teleport bug that could wedge the player.
- **15 Aug** — **Braille support implemented.** "On the ground nearby" no longer said for distant objects. "Enemy defeated" line removed. Empty and diggable graves sorted into the right categories. **Localisation added**, with untranslated labels fixed.
- **16 Aug** — Further translation IDs. **Prism upgraded to v0.17.3 and now bundled for Windows, Linux and macOS**, so releases work without a separate Prism install.
