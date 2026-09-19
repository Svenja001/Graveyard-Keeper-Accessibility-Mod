## 0.3.0 | 20 September 2026

**The fishing mini-game can be played by ear, the opening cutscene and the storybook scenes are read
out, the fourteen painted pictures in the game are described for the first time, and auto-walk no
longer leaves you stranded at the bed.**

- **You can now catch a fish by hand.** Turning auto-catch off (Ctrl+F) used to leave you with the
  mini-game as the game shipped it: a fish drifting up and down a bar you cannot see. It is now played by ear. The bite is a double
  ding, with "Bite!" spoken right behind it: the window to hook the fish is under a second, so the
  ding is the cue you react to and the word only confirms it. Then a tone follows the fish while you fly the bar:
  it sounds high while the fish is above you, low while it is below, and it stops pulsing and holds
  steady the moment you have it — that steady tone is you winning. While the fish is inside the bar
  the pitch still drifts as it wanders towards an edge, so you can catch it slipping before it slips.
  Blips mark each quarter of the catch bar as it fills or drains, a low double tick warns that you
  are about to lose the fish, and a thud tells you the bar has hit the bottom. The cues follow your
  master volume.

- **And the fish will wait for you.** Ctrl+F now cycles three ways of fishing rather than two: catch
  it automatically, catch it yourself with the fish swimming at half speed, or catch it yourself at
  the game's own pace. The middle one is the one to start with. The fastest fish in the game cross a
  third of the bar in well under a second, which is a speed built for watching rather than listening
  — at half pace the same fish swims the same path and needs the same time in the bar to land, it
  just stops being a reflex test. Nothing else about the catch changes, and full speed is one
  keypress away.

- **The opening cutscene is narrated.** Starting a new game played a full minute of animation with
  story text painted onto it, and none of it was spoken — you heard "Loading", then nothing at all,
  and then you were standing in the graveyard. It is not a cutscene as the game counts them and not
  dialogue either, which is why it slipped past everything. You now hear that the opening scene is
  playing, each of its lines as it appears, and that it is over.

- **The pictures themselves are now described.** Fourteen painted scenes in the game are shown and
  never explained in words — the sinners' windows in the church, the villagers' tales, the scenes of
  the opening. Six of them are a picture and five seconds of silence and nothing else, so there was
  no way to know what you had just unlocked. Each now gets a written description read out as it
  appears: what is in the picture, who is in it and what they are doing. The descriptions were
  written for this mod — the game has no text for them — and are translated into German alongside
  everything else. They can run on a little past the picture they belong to; that is the intended
  trade for saying enough.

- **The storybook scenes are read out.** At a few points the game stops and shows a painted picture
  with a line of narration under it instead of a speech bubble. None of that is dialogue as far as
  the game is concerned, so none of it was ever spoken — the window announced itself as
  "Illustrations" and then went quiet until the scene ended. Every line is now read as it appears,
  and the scene no longer counts as an open menu, so the reminders and the world keys keep working
  through it.

- **The text crawl before the first time machine memory is read out.** That memory does not begin
  with the scene — it begins with a wall of text sliding away into the distance, Star Wars style. It
  is not a speech bubble, not a subtitle and not one of the painted cards, so nothing in the mod had
  ever seen it. It is now read as the crawl starts, and no longer counts as an open menu while it
  slides.

- **Cutscenes now say what they are doing, not just what is said.** A lot of these scenes tell their
  story in mime, and the silent gaps are long enough that the only thing you heard was the reminder
  that a scene was still running. Those beats are now described as they happen. Sounds you can
  already hear — doors, cheering crowds, a body hitting the ground — are deliberately left alone.

- **Auto-walk no longer strands you at the bed.** Some spots indoors are places the game's own
  pathfinding has no idea about — the bed in your house is one, a chest tucked into a corner of the
  graveyard another. The mod could walk you there by sliding you the last stretch in a straight
  line, but once you stood on such a spot it could not walk you anywhere else, in a room you had
  just been walked across. It now remembers the spot it set off from and quietly walks you back to
  it before carrying on.

- **The log file is a fifth of the size.** `BepInEx\LogOutput.log` is what you attach to a bug
  report, and it had been growing to forty megabytes in a single session. Eighty percent of that was
  two messages the game prints to itself over and over, neither of which ever said anything. They
  are now dropped before they are written; everything that helps diagnose a problem is still in
  there.

## 0.2.4 | 4 September 2026

- **Removed a stale message that kept firing during the tutorial.** Pressing E on an object could
  answer "Not available during the intro" even though the object could be used perfectly well. The
  check no longer matched what the game actually blocks, so it is gone.

## 0.2.3 | 3 September 2026

**Trees, flowers, mushrooms and ore now say which kind they are and whether you are allowed to work
them yet; a blocked build spot says what is in the way and how to clear it; auto-walk has been tuned
again to keep you out of walls; and another batch of objects that were read out as raw code have
proper names.**

- **Thirty-one more things say what they are.** Anything the game has no word for was read out as
  the internal code it is filed under — buildings, props, the smashed things,
  and a few people the game never named. Where it numbers a family you get one name rather than a
  numbered parade: all six piles of broken glass are "Pile of broken glass".

- **Trees, mushrooms and ore now say which kind they are — and whether you are allowed to work them
  yet.** The game gives its scenery no names, so the mod named whole families at once and all
  twenty-nine kinds of tree were "Tree". That hid the distinction that decides everything: the game
  sorts resources into groups and opens them one technology at a time, and until you own the right
  one it refuses the swing — saying so with a padlock drawn onto the work prompt, a picture, which
  never reached the speech. Every node now carries its group's name — **Small tree** and **Big
  tree**, **Edible mushroom** and **Red mushroom**, **Iron ore** and **Iron vein** — and anything
  you cannot work yet says what it is waiting for: *"Big tree, needs the Woodcutter technology"*. It
  asks the game's own unlock check, so it stops saying that the moment you buy the technology.

- **The nine flowers are three different alchemy ingredients, and every one of them was "Flower".**
  They belong to no group, but what a flower gives tells it apart, so each is now named after its
  bloom: **Yellow flower**, **White flower** and **Red flower**. An apple tree in blossom is still
  an apple tree; this only applies where the family name was the vague one.

- **A build spot with no room now says what is in the way, how to get rid of it, and where it is.**
  Pressing Space used to end with a name and nothing else — "Blocked by tree" — which leaves you no
  way to tell thirty seconds of work from a dead end. Every blocker now comes with what to do about
  it (*"chop down with the axe"*, *"demolish at the build desk"*), or which technology the work is
  waiting on; a tree that leaves a stump says so, since the stump blocks the build just as the tree
  did. Clearable spots say where they then open up — *"Clear the sawhorse and it fits 2 up, 3
  right"* — with a second option when a different set of things would also do, and then everything
  else in the build area that could go, counted and located, because the readout used to name one
  sawhorse while a few bushes two tiles away were the real obstacle. The line for a spot that cannot
  be cleared at all was English-only, and is now translated.

- **Auto-walk has been adjusted again to keep you out of walls.** It watches whether a walk is
  carrying you into something solid and puts you back on the path when it is. This round taught it
  the difference between clipping the edge of something as you pass and genuinely being pulled
  through a building, so it holds on where it should — and it no longer gives up on a journey
  because you grazed a heap of coal in the smithy yard.

- **A few more characters the game keeps off-stage have stopped turning up in your lists.** The last
  batch covered the row of spots it parks its cast on; this one covers another spot that is not
  named like the others, so it had to be recognised by the shape of the crowd standing on it.

## 0.2.2 | 1 September 2026

**The game runs faster, walking to spots you never should see is fixed, auto-walk stays out of walls and gets through the graveyard, chests
answer the moment you press, and a batch of things that were still being read out in English — the
day an NPC names, the soul healer's organs, several buildings — say what they are in your language.**

- **Dialogue, tasks and quests say which day of the week they mean.** The game never writes a
  weekday as a word — it draws a little picture of that day's sin and writes the sentence around it,
  so the day never reached the speech at all and you heard the raw code, "d4" or "d2". All six now
  read as the day they stand for — "Talk with the Merchant on Day of Gluttony" — in the same wording
  the Q key uses for today's date. This runs on every piece of game text the mod speaks: NPC
  dialogue and your own replies, task and quest text, the NPC list and the tech tree.

- **The spot where you throw a corpse in the river is a landmark now.** Yorick asks you to dig up the
  unpleasant neighbour and get rid of him in the river, and then tells you nothing about where that
  is: no quest arrow, the dialogue just says "the river", and the place had no name of its own, so
  it read as "Throw body river" in the **Other** list — and only while you were standing near it.

  It now sits in **Landmarks**, from anywhere on the map, as *"River bank, throw a body in here"*,
  and it reads the same wherever else you meet it. It is **not** tied to the quest: throwing a body
  in the river works for the whole game, so it stays in the list afterwards — including for players
  who took that quest long ago and could never find the place again.

  **And while you are carrying a corpse it joins Crafting stations**, next to the morgue throw-in
  and the crematorium — the short list you go to when the question is "where do I put this". It
  appears when a body goes on your shoulder and disappears when your hands are free. It has to work
  that way round: the game never registers this step as a task, so there is no quest entry to hang a
  marker on. Yorick only says it out loud, once.

  The bank is no longer listed in **Other** as well. Between that, the landmark and the carried-body
  entry, standing next to it offered you the same spot three times over.

- **Gerry's scene at the river can actually be reached now.** After your first corpse goes in the
  water, Gerry turns up on the bank to have a word about it — and that scene is what opens the NPC
  list for the first time. It does not play when you throw: the last step arming it is walking into
  an invisible trigger zone a few steps along the bank, which a sighted player crosses without
  noticing as they wander off. If you arrive by auto-walk, throw, and stand still, nothing happens.

  While that scene is pending, the bank now appears under **Quests** as *"River bank, Gerry wants a
  word about the body"*, and walking to it starts the scene. It shows up only in the window between
  the throw and the meeting, so it is silent for everyone else.

- **The game runs at full speed again.** On a long session the picture had been falling steadily
  behind the sound: the world moved in jerks while music and footsteps kept perfect time. That was
  the mod. Keeping the list of things you can walk to up to date costs real work, and it was doing
  all of that work again from scratch about twice a second, forever — asking the engine for every
  object in the game just to find the items on the ground, re-deciding what kind of thing each of
  the two thousand-odd objects around you was, and working out the spoken name of all eighteen
  hundred of them.

  All three are fixed. Ground items now come from the list the game itself already keeps; what an
  object is and what it is called are worked out once and remembered, until the object turns into
  something else. A rebuild has gone from about a sixth of a second to roughly a fortieth, and the
  mod as a whole from a third of the machine's time to a small fraction of it.

  Nothing you hear has changed. Names that can change while the object stays the same — whether a
  grave is empty or has a body in it, how many crates are on a pallet, what stage a garden bed is at
  — are deliberately never remembered, and are still worked out fresh every time. Switching the
  game's language throws the remembered names away.

- **Auto-walk no longer drags you through walls.** To walk you somewhere the mod hands you to the
  game's own mover with your controls switched off, which is what lets it thread gates and fences
  the way a villager does. The cost is that nothing physically stops you — so when the mod could not
  find a proper route and fell back on a dead straight line, that line went through whatever was in
  the way, house walls included, and left you standing where the game never expects anyone to be.

  Straight-line walks are now checked before you are moved at all, and a walk with a wall in it is
  not made. If a running walk does carry you into something solid, you are put back on the last spot
  where you stood in the clear and a different route is worked out from there; only when there is
  genuinely no way round does it say so and switch to turn-by-turn guidance. You are never stopped
  standing inside a wall, because handing your controls back there would leave you wedged.

  It also stopped arguing with the game about what a wall is. Chairs, tables, trees and fence rails
  that the game itself walks straight through are not obstacles — brushing past them is what lets
  auto-walk thread the village gates and get you from your front door to your bed. Only somewhere
  the game's own map agrees nobody can stand counts as a wall.

- **Auto-walk works in the graveyard.** Among the graves it used to give up and leave you to walk
  yourself, even though the way through was plainly there. Two separate reasons, both fixed.

  The game keeps two maps of where you can walk: a coarse one for villagers, and a fine one for you.
  The coarse one's squares are nearly a whole tile across, so where graves are packed together it
  has no free square left in the gaps. When it gives up, the mod now asks the fine map — whose
  squares fit between the headstones — and drives that route instead.

  The second reason was the search area: the game only looks for a route inside a narrow corridor
  between you and your destination, so a way round that leaves the corridor is never considered.
  That is fine for stepping round a fence and useless for walking round a building, which is what
  reaching the morgue door asks for. After a walk fails once, the mod now widens that corridor
  considerably and tries again.

- **Landmarks for places aim at the middle of the place.** A landmark like the village covers a huge
  area, and the mod was pointing you at whichever of its objects happened to be nearest — which,
  from the house meant the high ground on its far side. Auto-walk then set off up over the
  cliffs while the tavern, a landmark inside the same village, took the road the whole time. It now
  heads for the middle of an area instead, which is both the sensible destination and a fixed one:
  the distance it reads out no longer drifts as you walk.

- **The crowd of NPCs standing in the void is hidden.** Graveyard Keeper does not remove a character
  who is finished for the day; it teleports them off the edge of the map and leaves them standing
  there — everyone whose day it is not, every tavern guest between visits, the whole cast. A sighted
  player never sees that place. The mod did: they were listed under **People** and **Vendors**, they
  turned up as quest targets, and you could auto-walk to them, off the map into a part of the world
  that is not meant to exist.

  It turned out not to be one spot but a whole row of them, one per off-duty cast member, side by
  side next to the general one everybody else goes to. All of them are now recognised, and so is the
  mark the game stamps onto a character when it sends it away, which is kept in your save. Anyone
  parked is invisible to the mod everywhere — the object lists, quest markers, the readout of what
  is near you, the corpse search and the combat scans. What is NOT hidden: the place your zombies
  stack wood, which is named almost the same way and which you very much need to find.

- **Moving a whole stack into a chest answers straight away.** Holding Shift and pressing Enter on a
  stack moves all of it at once and tells you how much went, but there was a noticeable pause first:
  the mod was reading and naming every item in the chest and in your bags twice over for the one
  press. It does that once now. It was also writing several hundred diagnostic log lines per pass,
  which cost more than the work itself with the log window open; those lines are still there for
  anyone chasing a bug, but off unless asked for.

  The same press at a trader now says what moved as well. It always worked, but the trader screen
  spoke only the new balance and dropped the "Moved 5 wood" half on the floor.

- **Five more things say what they are instead of reading out a code.** The funeral pyre, the morgue
  building, both corpse hatches, and a grave once a body is in it were among the handful of objects
  Graveyard Keeper never named in any language, so they came out as "Mf pyre", "Morgue 1" and
  "Grave corp". They now read as *Funeral pyre*, *Morgue*, *Corpse hatch* — the outside one the donkey drops
  a body into and the inside one you clear it through — and *Grave with a body*, which is the game's
  own wording for it elsewhere.

- **The soul healer names its organs in your language.** Each of the seven sins wants one particular
  body part, and the row that tells you which one read it out in English — *"Sloth, takes brain"* —
  in every language. The station asks the game for the part by a family name, and the family name is
  the one thing the game never writes down: it has words for *heart with 2 red and 1 white*, and
  none for *heart*. Where a word does exist under the bare name — flesh, fat, skin, blood — the mod
  was not asking for it either.

  Both are fixed, and the words come from the game's own text rather than being written fresh, so
  every language it ships in is right. This covers the same gap wherever else it shows up, including
  recipe ingredients that accept any quality.

- **The chest window's close button is no longer English in a German game.** Buttons that carry no
  writing of their own fall back on the name the artists typed into the game, which is always
  English and never translated. In a chest that was the only button there is, so it read as "close
  button" in the middle of otherwise German speech. The common ones are translated now.

## 0.2.1 | 31 August 2026

**Three things that were filed in the wrong place, or under the wrong name.**

- **Garden beds have a category of their own.** A bed changes what kind of object it is at every
  stage of its life, and the tracker used to follow the game rather than the gardener: the plot you
  had just marked out was a shovel node and sat in Gatherables, the prepared bed was a crafting
  station (planting seeds is a craft), the growing crop was in no list at all, and the ripe crop was
  back in Gatherables. Working one field meant hunting through three lists. Every stage now lands in
  a single **Garden beds** category — the plain beds, the ones with sticks, the vineyard and the
  refugee camp's beds.

  Because the game gives every stage of a crop the same name, each entry now says which stage it is:
  *still to dig*, *empty, plant here*, *growing*, *ready to harvest*. Beds stay listed while they are
  off screen, at the same reach as the other things you walk out to and work, so you can find one
  from across your plot instead of only when it is already in front of you.

  **The village farm's fields are not in the list.** They belong to the farmer, and the game leaves
  them permanently ripe as scenery — there is no way to pick them. They looked exactly like your own
  ripe crop, so the list offered them and walking to one across the whole map ended at a bed that
  ignored every keypress. Only beds you can actually do something at are listed now.

- **The portal pedestal on the witch hill says what it is.** The game calls it "Marble pedestal",
  which sounds like scenery and gave no hint that this is where the things to end the game go to
  open the portal. It now reads as the portal's marble stand. It stays under crafting stations,
  which is what it is when you stand at it.

- **Your bed is called a bed.** The game names beds after their blanket — the one in your house is
  "Common bedspread", and the keeper's-room ones are "Blue bedspread", "Red bedspread" and so on —
  so nothing in the spoken name said *bed*, and looking for somewhere to sleep turned up nothing.
  Beds now read as "Bed", and they moved out of the Other list, where they had been sitting with the
  scenery, into Built objects with the rest of the furniture.

## 0.2.0 | 29 August 2026

**Walk there yourself, one direction at a time.** Auto-walk (Ctrl+Home) does the walking for you,
which is not what everyone wants — several players would rather move themselves and only be told
where to go. Until now the only thing on offer for that was the compass beacon, which points
straight at the target in a dead line: through fences, through the church wall, through the
graveyard hedge. It was never a route. **Ctrl+B** now gives you the route, spoken one step at a
time, and your legs stay yours.

### How it works

Pick something the way you always do — **Page up** / **Page down** through a category — and press
**Ctrl+B**. The mod asks the game for the same obstacle-aware route the auto-walker drives, reduces
it to the corners worth mentioning, and talks you along it:

> Guiding to the sawmill, 12 meters. Walk 8 meters east, then north.

Hold the one key it names. When you reach the end of that stretch it gives you the next one — "Now
5 meters north" — and when you arrive it says so and turns you to face the thing, so plain **E**
interacts without any fiddling to line yourself up.

Then it is quiet. While you are on course it says nothing at all, and it only speaks up when
something has changed: you have drifted off the line, something is in the way, or you are there.

- **Every step is one direction, so it is one key held down.** North, south, east or west, never
  "north-east". Partly because one instruction should be one key, and partly because this game moves
  you up and down at four fifths of the speed it moves you left and right — so a diagonal is not a
  straight line and any diagonal instruction would drift by design. A slanting stretch of route
  becomes a staircase of short cardinal steps instead.

- **Distances are in metres, and the next turn comes with the step.** "Walk 12 meters east, then
  north" — so when the east stretch runs out you already know which key comes next instead of
  standing still waiting to be told.

- **A step is only ever offered along ground you can actually walk.** Each one is checked twice:
  against the game's navigation data, and against the same collision the game uses to stop your
  body. "Walk 12 meters north" never means "walk into the fence".

- **When something is in the way, it names it and takes you round.** "Fence blocks the way. Walk 6
  meters south, then east." The name is the thing your character actually collided with — the fence,
  the barrel, the gravestone — or "something solid" when it is part of a building rather than an
  object of its own. It works out how far round you really have to go, so a barrel costs you a metre
  and a fence costs you the length of the fence.

- **It corrects you rather than repeating itself.** Drift off the line and it tells you how to get
  back onto it — "Off course. Walk 3 meters east" — not the same instruction again. Speech takes
  time and you are still holding the last key while you listen, so nothing is judged off course
  until the instruction has had time to land.

- **Ctrl+B never moves you.** It is a toggle for the directions and nothing else. Only Ctrl+Home,
  the key that means "walk me there", may take over your legs.

- **It stays on until you switch it off.** It is a mode, not a one-shot. Arriving leaves it on,
  ready for the next thing, and so do cutscenes, teleports and anything else that interrupts. You
  can switch it on before choosing anything — it says "Guidance on" and waits.

- **The directions follow whatever you select.** With it on, paging to another object re-aims the
  guidance: "Now guiding to the gravestone. Walk 6 meters north." Browsing for somewhere to go no
  longer means pressing Ctrl+B again for every candidate. It waits until you stop paging before
  switching, so stepping through a long list does not set off a burst of talking.

- **Home repeats the step you are on**, with how far is left and where you are heading.

- **Indoors, it takes you to the door first.** Choosing something outside while you are standing in
  your house gets you out of the house rather than a bearing through the wall.

- **If it truly cannot see a way, it says so honestly and keeps the target.** You get the direction
  and the distance, and it quietly tries again as soon as you have walked a few metres — often that
  is all it takes. It will not repeat itself at you while you move.

- **Escape** stops the directions, though only once nothing else is moving you, so Escape still
  stops an auto-walk first.

- **Auto-walk falls back to directions instead of the beacon.** When the auto-walker gets boxed in
  by geometry it cannot drive through, it now hands you turn-by-turn directions along the route it
  did find. The old compass bearing is kept for the one case that has no route at all.

### Getting it right

This was built and rebuilt across twenty rounds of play-testing. Directions that are wrong
occasionally are worse than none: you cannot see what the mod got wrong, so every wrong instruction
costs you a walk into a fence and your confidence in the next one.

Most of that time went on three things. **Believing the route** — the mod follows the path the
game's own pathfinder returns instead of scoring compass directions for itself, so anywhere
auto-walk can walk you, this can talk you. **Believing the collision** — every step is measured
against the thing that actually stops your body, because the navigation maps do not know a wooden
fence is there. And "that way is blocked" is now said only when something really is there and you
were really walking into it, so pausing to think is no longer mistaken for being stuck.

### A finished craft says where the result went

The line that speaks when a craft finishes told you the result was lying on the ground beside you.
That is true of a workbench you worked yourself and wrong nearly everywhere else — and it was said
even when you had long since walked away.

- **It only speaks while you are still at the station.** A station with a zombie docked in it keeps
  working after you leave, and a station that unloads behind you looks exactly like one that has
  just finished, so completion lines were arriving from the other side of the map. If you are more
  than a few metres away when the craft ends, nothing is said at all.

- **Each kind of station now says where its output really goes.** Work a station yourself and the
  result drops at your feet, as before. A station run by a docked zombie, or run remotely on
  gratitude points, files it into the linked storage. The tavern kitchen and oven hand it to the
  barman, and the refugee camp's kitchen, hive and well hand it to the camp's own store. And the
  crates you build at the elevator for the merchant are not on the ground anywhere — they go down
  the shaft: "Box of vegetables crafted, sent down the elevator to the cellar".

- **Where the game hands the result to a script, the mod stops guessing.** Embalming, the rat cell
  and the skull and soul crafts now finish with a bare "… crafted" and no claim about where the
  thing is, because the game decides that somewhere the mod cannot follow.

### GOG: your own screen reader, at last

The GOG version of Graveyard Keeper is a 32-bit program, and Prism — the library the mod speaks
through — has never been built for 32-bit Windows. So GOG players have been getting the Windows
SAPI voice: everything was spoken, but in a voice that was not theirs, at a speed that was not
theirs, and never on a braille display.

**The GOG download now bundles Tolk instead**, a second speech library that does have a 32-bit
build, and it drives **NVDA, JAWS and ZoomText** directly, braille included. Nothing to install and
nothing to choose: the mod loads whichever library fits the version of the game it is running
inside, and the two are never both awake. If no screen reader is running, GOG still falls back to
the SAPI voice, the same as everywhere else.


- **The GOG download is now half the size.** Each bundle carries only the speech library its own
  version of the game can load — Tolk for GOG, Prism for everyone else — instead of both. That
  takes the GOG download from 2.6 MB down to 1.3 MB. The `ModOnly` download still contains both,
  because it does not know which store your game came from.

### The keys are where you can find them

The keyboard reference, the install instructions, the changelog and the licence used to ship only
inside `BepInEx\plugins\GraveyardKeeperAccessibility\` — four folders down, behind a name there is
no reason to open. In practice the list of keys was effectively missing.

All four now sit in the **root of every download**, named `Accessibility-Mod-README.md`,
`Accessibility-Mod-KEYBINDINGS.md`, `Accessibility-Mod-CHANGELOG.md` and
`Accessibility-Mod-LICENSE.txt`. The prefix keeps them together in a folder listing and tells them
apart from BepInEx's own changelog and licence. The copies beside the mod stay where they are, so a
Vortex install still has them.

### Also in this release

A handful of words were still coming out in English, or not coming out at all, in the German game.

- **The grave window says fence and cross in your language.** The two decoration rows were labelled
  from hardcoded English, so a German player heard "No cross" and "No fence" in the middle of a
  German sentence; they now read "Kein Kreuz" and "Kein Zaun". The repair entries behind them follow
  the game's own wording ("Grabstein reparieren") instead of a fixed "Repair cross".

- **A grave's decay is spoken as a percentage, with a translated name.** It used to come out as a
  bare count of the untranslated word "decay"; it is now "Verfall 47 Prozent" / "decay 47 percent".

- **The icon-only counters are read out.** Quite a lot of the game's text is written around a little
  picture — "Die Tür ist verschlossen, bis ich (happy)80 beim Ingenieur habe" — and the picture is
  the thing the sentence is about. Relationship, a grave part's quality, the merchant's fame, the
  refugee camp's happiness, water and camp quality, tavern quality and gratitude capacity now all
  get a spoken name, so that line becomes "…bis ich 80 Beziehung beim Ingenieur habe". Where the
  icon is only decoration next to the word it illustrates, it is dropped rather than said twice.

- **Amounts written after the icon are picked up too.** Task text like "Erreiche (rel) 100" puts a
  space between the two; that used to leave the number stranded with no idea what it counted.

- **Crafting and building tabs have names.** The game draws every tab as a bare icon and has no
  text for any of them, so the mod had been reading out the internal id — the stone grave decoration
  tab announced itself as "scross". All fifty of them are now named in both languages.

- **Skull counts say what they are counting.** A corpse used to read out as "1 red, 7 white", which
  only makes sense if you can see the two skull icons the numbers sit next to. It is now "1 red
  skull, 7 white skulls". The same applies to the autopsy grid, the cut-out and insert previews, the
  graveyard's zone score and the soul healer's organ readout.

- **German counts one skull the way you would say it.** The count sits inside the phrase rather than
  in front of it, so the adjective can agree with it — "ein roter Schädel, 7 weiße Schädel", and in
  the accusative where the sentence needs it ("verliert einen weißen Schädel"). The same singular
  now reads correctly wherever a skull is counted: the inline skull and cross icons, a zombie's
  efficiency, and the grave rating's "ein roter Schädel senkt sie".

## 0.1.4 | 23 August 2026

**Items can now tell you what they are for.** Press **O** on any item and the mod says what it is,
what you can make with it, and where it is made.

- **New key: O — details about the focused item.** It works on every item the game shows you: in
  your inventory, in a chest, in a vendor's list, on a station's recipe row. You hear the item's
  name and quality, whatever the game itself has to say about it, and then the part the game never
  tells anyone — **what the item is used for**. "Wooden plank. Used to make Box of vegetables, Box
  of goods, Carved wood … Used for building Beehive, Workbench …"

  Graveyard Keeper only ever wrote descriptions for about 80 of its 770 items, so for nearly
  everything the mod works this out from the recipes themselves. Only recipes you have already
  unlocked are counted, so O never gives away something you have not researched yet.

- **Press O twice for the whole list.** Staple materials go into a lot of recipes, so the list stops
  after six and says "and 12 more". Press O again on the same item to hear all of them. Moving to
  another row starts over at the short version.

- **Enter on an item that cannot be used now explains the item** instead of just repeating its name.
  Pressing Enter on a plank or a quest item did nothing and said nothing useful, which gave no clue
  why nothing happened. It now reads the same details as O.

- **Values that the game draws as little icons are finally spoken.** Food, potions and tools show
  their effect as an icon with a number next to it — a heart, a lightning bolt — and the mod used to
  read the raw code around it, or nothing at all. Health, energy, sanity, faith and the coloured
  research points now come out as words: "gives 3 health", "drains 5 energy". This is fixed
  everywhere the mod reads game text, not only for the new key.

- **Removed a wrong "used to make Story" from 281 items.** The research table sometimes
  produces a story, and that lucky roll is written into the data as if every studied item made one.
  Studying is not crafting, so studying no longer counts as a use at all — what studying gives you
  is still spoken by the "not studied yet" line. Clean paper still says it, because the zombie at the
  pulpit really does turn it into stories.

- **"Loading" is back when you pick a save.** Choosing a save slot (or "new game") went
  silent in 0.1.2, so there was nothing to tell you the game had accepted the press and was
  loading. The mod now says it the moment the slot is pressed, whether you press Enter on it or
  click it.

The keyboard reference that ships next to the mod (KEYBINDINGS.md) lists the new key.

## 0.1.3 | 22 August 2026

**Naming fixes: one wrong German day name, and a pile of objects that were read out as raw
internal ids.**

- The sixth day of the week was called "Tag des Hochmuts". It is now **"Tag des Stolzes"**. (The
  sin itself is still "Hochmut" where the game talks about sins — only the day is renamed.)
- **English: dialogue that names a day now gets the weekday read out too.** When an NPC says they
  will come on a certain day, the mod adds which day of the week that is — "day 6 (Day of Sloth)".
  In the English game some of those are written without a space ("day6"), and the mod only
  recognised the spaced form, so English players heard the bare number and no weekday. Both forms
  work now. German was never affected.
- **Twenty-odd world objects had no name and were read out as their internal id.** Things like
  "Candelabrum 2 1", "Vegit bracken 1" or "Village wc Face 2" — the game gives them no name of its
  own, because a sighted player just sees what they are. They now have proper names in English and
  German: candelabra and wall candelabra, church candles, spiral staircases, the wine press,
  dungeon grates, shelves, vases, the dungeon lift, ferns, hops, houses, the tavern, the village
  outhouse, earth graves, empty graves, church visitors, wooden barriers and the abandoned Smiler
  box.

If you hear an object read out as something that looks like an id rather than a name, that is
worth reporting — the mod writes each one into `BepInEx/LogOutput.log` on a line starting
`[NAMES]`, and that line is all that is needed to give it a name.

## 0.1.2 | 20 August 2026

**The mod was making the game stutter, and this release fixes the cause.** Several players
reported the game running rough — worst while walking around. That was the mod's doing, not the
game's.

Three of the mod's systems — the "what is next to me" readout, the combat assist, and the object
tracker — each asked Unity for a list of every object in the world, and they did it *every single
frame*. Building that list means walking the entire scene and allocating a fresh several-thousand
entry array, and then the mod sifted each list with checks that allocated a new piece of text per
object per check. Tens of thousands of throwaway allocations a second is what the stutter was: the
memory collector kicking in over and over.

- The mod now keeps its own list of world objects and updates it as objects come and go, instead of
  rebuilding it from scratch dozens of times a second. Everything each object is checked for that
  cannot change — is it the player, is it a prefab shell, does it belong to a DLC you own — is
  worked out once per object instead of once per object per frame.
- The proximity readout no longer sorts the entire world to find the nearest thing to you.
- The title-screen check no longer searches the whole scene on every frame of normal play.
- The world-zone list behind the navigation landmarks is reused rather than re-gathered on every
  refresh.

Nothing about what the mod says or how it behaves changes — it does the same work, far more
cheaply. If your game still runs rough, the mod now writes its own timings into
`BepInEx/LogOutput.log` (lines beginning `[PERF]`), and that log is the most useful thing you can
attach to a report.

## 0.1.1 | 19 August 2026

**GOG players: the 0.1.0 download could not work, and gave no sign of it.** The GOG build of
Graveyard Keeper is a 32-bit program, and the loader in `..._WithBepInEx.zip` was the 64-bit one.
A 32-bit game cannot load it, so the game simply started without the mod — no error message, no
log file, nothing.

- New download **`..._WithBepInEx_GOG_32bit.zip`**. Take this one for GOG; take
  `..._WithBepInEx.zip` for Steam. Everything else about them is identical.
- On the GOG build, speech comes from Windows SAPI rather than your screen reader. Prism, the
  library that drives NVDA, JAWS and braille displays, has no 32-bit version — there is no
  release of it that does. The mod still speaks everything, just in the Windows voice.
- Tested on the GOG build and working. It is still built against the Steam version of the game, and
  GOG's is older, so if you meet something that behaves oddly only on GOG, please report it.

## 0.1.0 | 16 August 2026

Initial release. Graveyard Keeper played entirely by ear — menus, dialogue, the world,
crafting, the graveyard, the dungeon and the DLC content are all narrated through a screen
reader. Developed 3 June – 16 August 2026 over 151 commits.

**Requires BepInEx 5.4.x (Windows x64).**

`KEYBINDINGS.md` ships next to the mod and lists every key it adds.

Two downloads: `..._WithBepInEx.zip` includes the BepInEx loader and is extracted into the game
folder — the whole install in one step. `..._ModOnly.zip` is the mod on its own, for players who
already run other Graveyard Keeper mods, and goes into the existing `BepInEx` folder.

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

- **The BepInEx config menu, if installed, (F1) is not accessible.** Opening it breaks NGUI keyboard input —
  arrows and Escape stop responding and speech stops — because BepInEx's Configuration Manager
  and the game's UI both poll the keyboard at the same time. The feature is disabled rather
  than left half-working; everything else is reachable without it.
- **Linux and macOS are untested.** The Prism speech libraries for both are bundled, but
  nobody has confirmed yet if it correctly loads on the game's native Mac and Linux builds. Windows
  is verified. Linux players using Proton run the Windows build, which works, but Prism cannot
  reach a Linux screen reader such as Orca from inside Proton, the dlls for this need to be downloaded manually, as mods can't do it in this case.
- **Only English and German are fully translated.** Spanish, French, Italian and Russian
  files exist but are near-empty and fall back to English.
- **The quest category in the Objecttracker only lists quests that have a map marker.** Quests without one are not
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
