# Session 117 — The settings window rebuilt, presets that set detail, and the no-sampler warning

**Date:** `2026-10-19`
**Branch/commit:** `main`, checkpoints `5d0e0b4` (0.4.165) to `ff9a083` (0.4.175); nothing pushed
**Mod version:** `0.4.175`
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged; one append-only field, `AssistWelcome.SilhouetteHeightSource` = 10)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner asked how to make Algernon's Terrain Sampler a dependency of the mod. A modinfo
`dependencies` entry would have been one line, but it makes the mod refuse to load without it, and the
silhouette plan's decision D13 (2026-09-14) keeps the mod standalone with sampler mods as optional
accelerators. He kept it optional and asked for notes and a red warning when neither supported
sampler is found. That grew into a rebuild of the settings window across eleven builds, driven by his
in-game screenshots, several of them marked up in red, and a change to what presets mean.

Evidence: the owner's screenshots (Pictures\Vintagestory, 2026-10-19 18:18-21:55) and client logs,
the game's own code decompiled with ilspycmd (1.22.7: `CairoFont`, `TextDrawUtil.Lineize`,
`GuiComposer.GetElement`, `GuiDialogEscapeMenu`, `ClientMain.PauseGame`), the project's earlier
measurements (Sessions 77, 107 and 115 for feature costs), and the fast check tier. Nothing was
launched by the agent; every build was played by the owner.

Midway the owner set a working rule: talk a change through before finalizing a build. He later allowed
builds for agreed changes ("You can make the build too"), so builds followed his explicit decisions.

## Work narrative

### 1. No dependency; a warning instead (0.4.165)

The server picks the sampler, so the client cannot tell from its own mod list, least of all for
Watersheds in multiplayer. `AssistWelcome` gained field 10 `SilhouetteHeightSource`
(`EnumSilhouetteHeightSource`: 0 unknown, 1 engine previews, 2 accelerator, 3 accelerators off in the
server config). Zero from every older server shows no warning, and so does an admin's choice to turn
accelerators off. The warning shows only when the server offers silhouettes and found neither mod.
README gained a Recommended mods section. The wire check (`WireFormatChecks`) caught the new field and
was updated.

### 2. The Advanced switch goes; the dials move to a Developer tab (0.4.166-0.4.167)

The owner first asked to remove most dials outright. Every one of his saved Farseer, fog and
anti-aliasing values equalled the code default (only his reach, 8,192, differed), so nothing of his
would be lost. He chose to remove the Advanced switch, the Anti-Aliasing tab, the duplicate Reach
slider and all 22 Farseer and fog dials, and to move Farseer's two switches to General. About 830 lines
went. Within minutes he reversed the removal: keep every dial, on a **Developer** tab. The dial code
came back from the previous commit, grouped behind a drop-down (all three groups at once do not fit
the 640-tall window).

General had no free rows. He chose smaller text over growing the window or scrolling, then asked for
two columns, which freed enough room to go back to the larger text. With two columns: a **Developer
options** switch (off by default, remembered at once) shows the Developer tab, and the Performance
tab's all-cores switch moved onto General. The distance bar's marker lanes were respaced: the Farseer
lane started exactly where the L2/L4/L6 lane ended, so the L4 and Farseer boxes touched.

### 3. Text that would not stay on one line (0.4.167-0.4.169)

"Vanilla and LOD Seam Blending" wrapped onto the row below. 0.4.168 shrank a title's font when
`CairoFont.GetTextExtents` said it would not fit; it still wrapped, and "More RAM" wrapped too. The
measure said the title fitted by a pixel or two, but the game's line breaker
(`TextDrawUtil.Lineize`) counted it as reaching the box edge. 0.4.169 aims for 92% of the box and
fits the cost words as well; the owner's next screenshot showed both on one line (G292).
`GetTextExtents` returns a `cairo-sharp` type, so the project now references `Lib/cairo-sharp.dll`
(not copied into the zip).

### 4. What a preset is (0.4.168-0.4.169)

The owner set a preset in game and asked that the screen's settings define it. Presets now carry the
cached draw distance, Farseer's switch and reach, and the seam fog, as well as the seven feature
switches (`LodPreset`). He agreed Performance should keep Farseer on at 4,096: it is far cheaper than
cached terrain. He kept Quality's Farseer reach equal to its draw distance: Farseer fills unexplored
ground inside that distance, which in a new world is most of it. Quality is the fresh-install default
and the Defaults button, so players see what the mod can do. A first-run prompt asks every player once
for a preset, before the LOD-mod notice. Balanced gained varied tree colours.

Testing the switches, he found almost none moved his frame rate. Most of them change how pixels are
coloured, while what distant terrain costs is how much is drawn, which only the draw distance and the
LOD distances change. He also found that the vanilla view distance moved every automatic LOD level.
The source confirmed it: the vanilla floor (view distance + blend band + one section) started the
doubling sequence, so once it passed the pixel-based L1 every level moved out with it (G294). 0.4.169
lets the floor hold L1 alone, and presets set the automatic target in on-screen pixels (Quality 1.5,
Balanced 2, Performance 3) and switch Automatic on, keeping the player's own distances underneath.
His rule against fixed block distances made the target, not block values, the thing a preset stores.
The Developer anti-aliasing group gained an LOD detail target slider.

### 5. Layout from marked-up screenshots (0.4.170-0.4.175)

In order: Defaults stopped switching all-cores off (it is a machine setting needing a restart); the
Developer drop-down became sub-tabs; the draw-distance heading moved to the top with a rule under the
bar; then Visual features moved under the rule with Farseer, Seam fog and all-cores in its list; then
one title, "LOD Thresholds (Drag to adjust)", replaced the two headings, the draw-distance readout
(the bar's Cache box shows it) and the help line (now the title's hover); paused loading and
Developer options moved into the action row; "Automatic (Recommended)" with a line under the title.
Farseer, Seam fog and Water got cost words from the project's own measurements (decisions below).

### 6. Fog over the player at login (0.4.174)

At login the cached terrain around the player has not loaded, so the edge the seam fog follows runs
close around the player and the fog fills the screen. Nothing reports when that first load is done,
so the fog holds for 5 s from `LevelFinalize` (when the player enters the world, not mod start) and
fades in over 2 s by scaling its two densities; its field still builds meanwhile (G296).

### 7. Generate while paused in Settings (0.4.175)

"Open live controls" left Settings, which returns to the still-paused pause menu, so the integrated
server could not generate. The API has no pause call; the pause menu unpauses the game in its own
`OnGuiClosed`. The button, now "Open Live Controls + Unpause", leaves Settings, closes
`GuiDialogEscapeMenu`, then opens the live window (G293). Paused, the page shows only that button; the
generation buttons could do nothing there, and their state refresh skips buttons that are absent.

---

## Delivered

- **0.4.165 (`5d0e0b4`):** `AssistWelcome` field 10 and the red no-sampler warning; README Recommended
  mods.
- **0.4.166 (`e62f602`):** Advanced switch, Anti-Aliasing and Farseer tabs gone; Developer tab with a
  group drop-down holds every dial; Farseer and Seam fog switches on General.
- **0.4.167 (`1e7990e`):** two-column General; Developer options switch (`ShowDeveloperOptions`);
  Performance tab folded into General; marker lanes respaced (bar 214 tall).
- **0.4.168 (`84bdbb7`):** `LodPreset` (switches, draw distance, Farseer, reach, fog); Quality default
  (draw distance and reach 8,192 for new installs); first-run prompt (`PresetPromptShown`,
  `LodPresetPrompt`); text fitting; `cairo-sharp` reference.
- **0.4.169 (`09a79e7`):** vanilla floor holds L1 only; presets set the detail target and turn
  Automatic on; LOD detail target slider; 92% text fit.
- **0.4.170 (`0cf3032`):** Developer sub-tabs; Defaults keeps all-cores.
- **0.4.171-0.4.173 (`7d16b6e`, `2788609`, `f91c389`):** draw distance at the top and the rule, then
  Visual features under the rule with cost words, then one title and the action-row switches.
- **0.4.174 (`b5c99b7`):** fog login hold; Automatic (Recommended) and its line.
- **0.4.175 (`ff9a083`):** Open Live Controls + Unpause; roomier top of General.
- **Checks:** 26,432 assertions, 0 failures at 0.4.175.
- **Documentation:** this record, the index row, CHANGELOG, GOTCHAS G292-G296, WIRE_HISTORY, TODO,
  STATUS, ARCHITECTURE decisions 35 (amended), 63 and 64, README, CLAUDE.md routing.

## Decisions

- **Sampler mods stay optional** (owner; D13 stands). Rejected: a modinfo dependency, which would stop
  the mod loading without Algernon's and force it on every client of a server running it.
- **The warning shows only when silhouettes are offered and neither sampler was found.** Not for an
  older server (unknown) or an admin who turned accelerators off.
- **Every tuning dial stays, on a Developer tab gated by Developer options** (owner, after first asking
  to delete them). Rejected: deleting the dials; the Advanced switch.
- **Presets set switches, draw distance, Farseer and reach, seam fog and the automatic target, and turn
  Automatic on** (settled decision 63). Quality is the default. Farseer stays on in every preset,
  including at the cached distance (owner: unexplored ground). Rejected: presets storing fixed L-block
  distances; raising Quality's reach to 16,384; Defaults = Balanced.
- **Automatic's vanilla floor holds L1 only** (settled decision 35, amended).
- **Cost words:** Seam fog High (owner-measured 0.52-0.68 ms at 1440p, Sessions 107 and 115),
  Farseer Low (estimate after 0.4.93's geometry cut; no clean A/B), Water Effects Low (paid only where
  water shows; nothing visible in Session 77's switch test). "Varies" told a player nothing.
- **All-cores collection and Developer options are untouched by Defaults.**
- **Fog login hold is time-based, 5 s then 2 s fade**, from `LevelFinalize`. Rejected for now: a load
  signal, because none exists.

## Traps

- **The line breaker wraps text the measure says fits** (G292). Trigger: shrinking a label to exactly
  its box. Failure: it still wraps. Safer: aim for about 92%.
- **Leaving vanilla Settings keeps the game paused** (G293). Trigger: handing off from Settings to a
  window that needs the world running. Failure: the integrated server stays paused. Safer: close
  `GuiDialogEscapeMenu`, whose `OnGuiClosed` unpauses.
- **A floor that starts a doubling sequence moves every level** (G294).
- **Source-text checks read CRLF files** (G295). Trigger: a check pattern spanning two lines with `\n`.
  Failure: it never matches on Windows. Safer: normalise `\r\n` first.
- **Seam fog follows an edge that starts at the player until the cache loads** (G296).
- **`GuiComposer.GetButton` on an absent key returns null**, and setting `.Enabled` on it throws.
  Guard with `GetElement(key) is GuiElementTextButton`.
- **Tooling:** an inline Python heredoc containing apostrophes fails in the Bash tool; write edit
  scripts to the scratchpad and run them by path (already in the agent's notes).

## Flagged and unverified

- **Judgement calls awaiting human review:**
  - The cost words Farseer Low and Water Low are estimates; a timed on/off pair in one scene would
    settle them (needs a game launch; ask first).
  - The fog login hold's 5 s is a guess at a typical first load; a slow machine or a far draw distance
    may need longer.
- **Not established:**
  - The red no-sampler note has never been seen in game (needs a world with neither sampler mod).
  - The fog login hold and Open Live Controls + Unpause are harness-tested only; the owner said
    "Looks good" after 0.4.175 but did not report testing either.
  - The presets' geometry savings (Balanced about 56%, Performance about 25% of Quality's triangles)
    are arithmetic, not measured.
  - The first-run prompt: the owner said everything else worked after 0.4.169 but did not mention
    it by name; its layout at other GUI scales is unseen.
