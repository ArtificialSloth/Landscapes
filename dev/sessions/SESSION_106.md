# Session 106 — Continuous Farseer seam fog

**Date:** `2026-09-20..21`
**Branch/commit:** `codex/water-depth-light`, 0.4.122 at `d46b776`
**Mod version:** `0.4.120–0.4.122`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Source and checks remain authoritative; the owner's in-game
> observations are the acceptance evidence for appearance.

## Context and investigation

The accepted seam fog still followed the exact 32-block Farseer/LOD ownership staircase. Version
0.4.110 had rounded turns and swept true path ends, but a distant view made the frontier read as a
sawtooth. The owner prioritized visual fidelity and explicitly allowed more fog over the Farseer
side. Work began from the clean committed 0.4.119 checkpoint.

Version 0.4.120 traced the directed grid boundary into maximal paths, simplified it within the fog
width, rounded it and resampled it before building the existing fog ribbons. The owner reported the
shape better, but screenshots `2026-09-20_21-55-15.png` and `2026-09-20_21-55-41.png` showed hard
internal gradation lines. Those were not ordinary colour quantization: translucent vertical and
horizontal cards crossed each other and accumulated alpha at different rates, so the renderer's
surfaces remained visible even after their outside outline became smooth.

The replacement therefore had to remove the surfaces, not add more cards, blur their colour or
hide the crossings with noise. The owner asked for a durable, high-quality fog rather than a quick
patch.

## Work narrative

### 1. One continuous field replaces every fog card

Version 0.4.121 keeps the exact directed ownership boundary as authority, then traces, simplifies,
rounds and densely resamples it. The CPU converts those samples into one RGBA field:

- effective signed lateral distance, with Farseer on the positive side;
- nearest seam-ground height;
- unsigned distance for conservative empty-space stepping;
- open-end fade, including a negative guard that cannot reappear under rim wobble.

The nearest-sample field is built with a separable exact squared Euclidean distance transform. Its
base scale is eight blocks per texel; both axes and total storage are bounded at 2,048 and
2,048² texels, increasing world blocks per texel when a visible window is larger. Distances are
clamped to a half-float-safe range and uploaded once as linearly filtered `RGBA16F`.

The expensive rasterization and distance transform run on one background job from an owning-thread
snapshot. A newer snapshot replaces only the pending request so continuous movement cannot starve
publication. Every result carries a revision, stale results cannot publish, cancellation is safe,
and only the render thread creates or replaces the GL texture. Farseer is held until the first field
exists; an established field stays visible while a replacement is prepared.

### 2. A depth-aware raymarch integrates the medium once

The fog draw is now one full-screen triangle. The fragment shader reconstructs a camera-relative
ray from primary depth, intersects the bounded field, applies the same globe curve as Farseer, and
integrates transmittance through one continuous medium. It uses conservative horizontal and
vertical empty-space steps, a bounded 160-step loop, world-anchored three-dimensional billows,
two-octave rim wobble and spatial step dithering. The broader positive-distance shoulder deliberately
puts more fog over Farseer.

Because opacity is integrated once, there are no internal card intersections, height slices or
corner fans that can become lines. The pass still draws at `AfterOIT` order 1.0, samples primary
depth read-only, writes RGB only, preserves primary alpha/depth, and uses explicit blending on its
single-colour framebuffer. `Fog layer detail` became `Fog quality`; the persisted internal field
name remains for configuration compatibility, but the dial now controls raymarch precision.

The seam signature also includes the sampled fog-height grid, so a terrain-height change rebuilds
the field even when the ownership bits did not move.

The owner's first 0.4.121 screenshot, `2026-09-21_00-32-42.png`, established that the fog itself
looked much better and the internal lines were gone. It also showed the remaining failure: a bright,
stepped ownership strip was readable through the otherwise good fog. Trying all available dials did
not make the seam sufficiently opaque.

### 3. The exact seam receives a normalized extinction core

The raymarched bank originally allowed its noise floor to reach the exact handoff. At a grazing
opaque-depth endpoint, the ray could hit terrain before accumulating enough extinction, so a good
organic pocket defeated the fog's primary job.

Version 0.4.122 adds a broad smooth core inside the same medium. It is not a surface or second draw.
Its LOD-side integral is normalized by the core width and the renderer's per-block extinction, so
Fog spread no longer accidentally changes the minimum opacity. Coverage selects a target optical
depth from 3.0 to 4.5; the default 25% gives 3.375, absorbing more than 96% of seam light before
organic billows or the wider Farseer shoulder contribute. Fog density still scales the entire pass
and zero still disables it. The core uses the unwobbled signed distance so noise cannot open a crack,
but it keeps the field's smooth outer and open-end fades.

## Delivered

- **0.4.120** — smoothed and rounded the directed ownership path before building ribbons. Verified,
  copy-installed and human-tested. Artifact: 689,194 bytes, SHA-256
  `845C414B2CEA081BF1140A88A7B0DBE90307ACF48BBDC2095C44027C8F8DF344`, checkpoint `e6b4a21`.
- **0.4.121** — replaced every fog card with the continuous RGBA field and depth-aware full-screen
  raymarch; moved the heavy field transform off-thread with revision-safe publication; renamed the
  visible quality dial. Verified, copy-installed and human-tested. The owner said the fog itself was
  much better, but the seam was under-covered. Artifact: 693,012 bytes, SHA-256
  `4E67B6B2631C675F8BFE5DFC8BB3666A4C02CC8CA6F3E9A552309997926845F7`, checkpoint `a5f528d`.
- **0.4.122** — added the normalized seam-sealing extinction core and corrected the coverage help
  text. Verified and copy-installed; not yet human-tested. Artifact: 693,608 bytes, SHA-256
  `DFA51C84B89127D7E7AF3D7DE99EE3150736C1533E7BC2B3DF3C482C5D62DD59`, checkpoint `d46b776`.
- Release build: zero warnings and zero errors. Final fast suite: **25,364 assertions, zero
  failures**. The package has 43 exact root-relative entries, one DLL, both fog shaders and no PDB
  or wrapper directory. No game was launched by the agent.

## Decisions

- **Remove the surfaces.** Rejected more layers, colour blur and additional noise: all leave the
  alpha-stacking topology that produced the hard lines.
- **Use a compact 2D world field plus screen-space integration.** A 3D voxel texture would spend
  memory on mostly empty altitude; analytic height and density over the 2D seam field retain the
  desired vertical form at bounded cost.
- **Preserve exact ownership.** The field and its fog may spill visually over either owner, but
  never dilate suppression, invent a boundary beside a missing tile or change terrain/water
  ownership.
- **Seal the seam independently of noise.** Organic openings are welcome in the outer bank, not at
  the exact transition the effect exists to conceal.
- **Keep build work asynchronous and GL work render-owned.** The main thread snapshots all live
  mesh-height data before the worker starts; the worker touches no live registry or GPU object.

## Traps

**Layer count cannot fix structural alpha lines.** Intersecting translucent cards expose their
topology through accumulated opacity; smoothing their perimeter changes the outline, not the
intersections. Replace the representation with a single integrated medium.

**A visually organic fog can fail its functional requirement.** Letting noise own the density at
the exact seam creates beautiful low-density pockets that reveal the handoff, and a global density
increase over-thickens everything else. Give the seam a smooth, width-normalized optical-depth
floor inside the same medium and let noise shape the outside.

## Flagged and unverified

Judgement calls awaiting human review:

- Revisit `2026-09-21_00-32-42.png`'s viewpoint on 0.4.122 at Fog density 100% and Fog coverage
  25%. The stepped terrain handoff should no longer be readable, while the broad outline and lack
  of internal gradation lines should remain.
- If the core reads as a uniform bright stripe, tune its smooth width/target rather than restoring
  noisy holes or any surface geometry.

Claims that lack the evidence level required to treat them as established:

- 0.4.122 has not been seen in game. Its shader syntax is source/static-tested; runtime compilation
  and appearance remain human gates.
- The continuous pass has no controlled GPU timing. `Fog quality` now changes march step precision,
  so the old layer-count cost question no longer applies; low/default/high quality and Farseer
  off/on still need a same-scene timing.
- Weather, night, rapid replacement, excavation and the broader Farseer ownership matrix remain
  open exactly as before.
