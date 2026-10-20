#This mod is free, but if you could star my 2 repositories, I’d appreciate it.❤️

# Sandbox House

A MelonLoader mod for **Sarah's House** (AceStudio) that turns the house into a free sandbox: no story, no quests, no saving — just the house, the characters, their animations and the game's own two-person scenes, replayable on the spot.

> Unofficial fan mod. Not affiliated with AceStudio. You need your own copy of the game.
> Tested on game **v0.11.6** (Windows, IL2CPP) with **MelonLoader 0.7.3**.

----

## Features at a glance

| | |
|---|---|
| **Sandbox mode** | Quests, dialogs, cutscenes and time are frozen. Saving is disabled (menus, autosave and the bed), so your real saves are never touched. |
| **Characters** | Bring everyone to you, freeze them in idle, teleport one to you, undress / dress anyone (state is kept everywhere). |
| **Interaction menu** | Talk to a character (or aim + `G`) to open a menu instead of the game dialog: clothes, animations, two-person scenes, "come here", "stay there"… |
| **Animations** | Play any of the character's animations (or any animation loaded by the game) on them, looped, snapped to the floor, adjustable height. |
| **Two-person scenes, in place** | The game's love scenes replayed directly in the house — player + character animations, sounds, steps, ending — with no scene change and no save. Swap the girl (Lucy / Lisa / Sarah / Mia), change rhythm and speed, fix placements. Stop at any time and everyone goes back where they were. |
| **Love Room portal** | A pink sphere in the hallway: aim at it and press `G` to loop the opening scene (Love Room) endlessly. |
| **Free camera** | `F6` anywhere, with the player's head visible. |
| **Doors** | Locked doors can be unlocked from an interaction menu, or all at once. |
| **Gallery** | Every gallery scene is unlocked and replayable. |
| **Main menu** | A native-looking **Sandbox** button next to *Load*, and a short "Modded by Vex" splash after the Unity logo. |
| **No blinking** | The player never closes his eyes (no black screen) while in sandbox / scenes / free cam. |

---

## Installation

### Option A: ready-to-play build (easiest)

Download **both** files from the [latest release](https://github.com/MeuxyVex/SarahsHouse-Sandbox/releases/latest):
`SarahsHouse_Sandbox_v0.13.1_part1of2.zip` and `SarahsHouse_Sandbox_v0.13.1_part2of2.zip`.
Extract both into the **same folder**, then run `SarahsHouse.exe`. MelonLoader, the mod, the splash images and the day-63 sandbox save are already included.
The first launch takes a while (MelonLoader generates its files).

### Option B: install the mod on your own copy

1. Install **MelonLoader 0.7.x** on the game (`SarahsHouse.exe`) and launch the game once so it generates its files.
2. Copy `SandboxHouse.dll` into the game's `Mods` folder.
3. *(Optional, for the splash screen)* copy the images into `UserData/SandboxHouse/`:
   `vex_logo.png`, `modded_by_vex.png`.
4. Launch the game.

To uninstall, delete `Mods/SandboxHouse.dll`. Your saves are never modified by the mod.

---

## User guide

### Keys

| Key | Where | Action |
|---|---|---|
| `F9` | Main menu / in game | Sandbox panel |
| `G` | In game | Interact with the character, door or portal under the crosshair |
| *Game interact key* | Sandbox | Talking to a character opens the sandbox interaction menu |
| `F6` | In game | Free camera on / off |
| `F8` | In game | Pose builder (solo animations + precise placement, player included) |
| `Space` | Two-person scene / Love Room | Show / hide the mouse |
| `Esc` | Menus / two-person scene | Back / stop |
| `←` `→` | Two-person scene | Previous / next step |
| `↑` `↓` | Two-person scene | Speed |
| `1`…`9` | Interaction menu | Pick an option |

### 1. Starting a sandbox

**A ready-made save is included** (day 63, everybody lives in the house). On the first launch the mod copies it into the game's save folder on a free slot (your own saves are never overwritten). It appears in the load list / Sandbox list.

- **From the main menu:** click **Sandbox** (under *Load*). The game's load screen opens titled *Sandbox*; pick a save. Best: a save where everybody already lives in the house.
- **In game:** press `F9` → **Enable**. You're taken to the house.

While the sandbox is on, nothing advances the story and nothing is saved. **Disable** gives the game back (reload a save to continue the story cleanly).

### 2. The F9 panel

- **Everyone here / Release everyone** – brings all characters in front of you (frozen, idle) or gives them back to their schedule.
- **One line per character** – **To me** (teleport in front of you) and **Undress / Dress**. The line **You** does the same for the player.
- **▸ Options**
  - Block quests / dialogs / cutscenes, freeze time
  - Time of day (Morning … Night)
  - **House** (go back home), **Unstuck** (gives control back if the player gets stuck), **Unlock doors**
  - **Place the Love Room portal here**
  - Detailed log, **Quit to main menu**

### 3. Interaction menu

Talk to a character, aim at them and press `G`, or click them while `F9` is open:

- **Take your clothes off / Put your clothes back on**
- **Play an animation…** – list of the character's animations (filter box, *All game animations*, *Loop*, height ▲▼ and *Snap to floor*). The character comes in front of you and plays it.
- **Two-person scene…** – the game's scenes for this character that take place in the house.
- **Come in front of me**, **Stay right there / You can go**, **Goodbye**

### 4. Two-person scenes

Played right where you are, with the game's own positions, animations and sounds. A bar at the bottom gives:

- **◀ Step / Step ▶**, **Auto** (next step after the chosen time), **speed − / +**, **■ Stop**
- **Rhythm 1 / 2 / 3** – the game's three rhythm variants of a loop, when the scene has them
- **Role X: ◀ name ▶** – swap the girl playing the role (Lucy, Lisa, Sarah, Mia share the same rig; the voice stays the role's original one)
- **Target / Placement** – turn (⟲90 ⟳90 ⟲10 ⟳10) and move (X / Z / ▲▼) the **whole scene** or **one character only**. Saved per scene in `UserData/SandboxHouse/placements.txt` (useful for a few developer test scenes whose placement is wrong in the game data).
- `Space` shows / hides the mouse.

Each step repeats automatically (no need to press the game's action key). **Stop** puts everyone back where they were.

### 5. Love Room portal

A pink sphere is placed in the hallway (move it with *Options → Place the Love Room portal here*). Aim at it and press `G`: the opening scene plays in the Love Room and loops between **3.6 s and 14.85 s** (before the phone rings). The **Start / End** sliders adjust the loop, `Space` shows the mouse, **■ Stop** brings you back to where you were.

### 6. Pose builder (`F8`)

Build your own scenes: pick a **target** (you, or any character), give it a **solo animation** (filter, *All game animations*, *Loop*) and place it precisely.

- **Bring in front of camera** – puts the target 1.5 m in front of the current camera (free cam included).
- **Left / Right / Fwd / Back / Up / Down** – directions follow the camera; **step** 1 / 5 / 25 cm.
- **⟲ / ⟳** – turn by 1 / 5 / 15 / 90°.
- **Snap to floor** – on by default for characters, switched off as soon as you move one up or down.
- **Stop anim / Release** – stop the animation, or give the character (or you) back to the game.
- Numpad: `4/6` left-right · `8/2` forward-back · `9/3` up-down · `7/1` turn · `+/-` step size.

Combine it with the free camera: fly around, place every character (and yourself) where you want, each with its own pose.

### 7. Free camera (`F6`)

`ZQSD` / `WASD` move · `Space` / `E` up · `Ctrl` / `C` down · `Shift` fast · mouse wheel = speed. When one of the mod's menus is open, hold the right mouse button to look around.

---

## Configuration

`UserData/MelonPreferences.cfg`, section `[SandboxHouse]`:

| Entry | Default | |
|---|---|---|
| `ShowSplash` | `true` | Show the "Modded by Vex" screen |
| `SplashColor` | `"#E58FAE"` | Splash background colour |
| `SandboxButtonOffsetX / Y` | `330 / 0` | Position of the Sandbox button relative to *Load* |
| `FreeCamKey` | `F6` | Free camera key |
| `LoveRoomPortal*` | – | Saved portal position |

---

## Known limitations

- Retargeted girls (role swap) may be slightly off where bodies differ; the face extras of a step are not played.
- Speed `↑/↓` doesn't change the speed of a retargeted girl (she stays in sync with the player every cycle).
- The free camera doesn't render some of the game's post effects.
- The mod hooks a few game functions; save-loading code is never hooked (it crashes IL2CPP games).

---

## Building from source

The project targets **.NET 6** and references the game's MelonLoader assemblies (not included in this repository).

```
dotnet build -c Release -p:GameDir="C:\path\to\SarahsHouse"
```

References used: `MelonLoader/net6/*.dll` and `MelonLoader/Il2CppAssemblies/*.dll` from your own game folder (generated by MelonLoader on first launch).

---

## Credits

- Mod by **Vex**
- Built with [MelonLoader](https://github.com/LavaGang/MelonLoader), HarmonyX and Il2CppInterop
- *Sarah's House* © AceStudio — please support the developers.
