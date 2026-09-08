# Session 79 — Dedicated water rendering and separate depth defects

**Date:** `2026-09-05`–`2026-09-06`
**Branch/commit:** `codex/water-depth-light`, HEAD `bc9309c`; implementation remains uncommitted
**Mod version:** `0.4.11`–`0.4.18`; latest private test artifact `0.4.18`
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

## Context and investigation

Continued Session 78's unresolved water junction. The owner distinguishes a broad brightness
band, a thin junction line/pattern, and scattered pale blips. They must not be treated as one
symptom. This session includes the depth/capture work, the accepted revision of the dedicated
water renderer plan, its implementation, and the owner's latest 0.4.18 comparison.

The old handoff's proposed `depth on` then `clarity 0` test was performed after rebuilding:
the seam stayed. `surface off` removes cached water and the thin junction line but leaves the
broad band. This rules out treating the broad band as simply the cached surface's blending.
The exact responsible term is still unproven. Earlier negative wave, walls, fresnel, depth
and blend tests remain recorded; they are not fresh avenues without changed evidence.

## Work narrative

### 1. Murk and the depth image

The owner initially saw murk darken normally bright terrain, then useful, close-to-vanilla
murk on cached seabed only when viewed through vanilla water. Shader sampler collection was
allowing engine liquid depth and mod climate data to alias. Explicit fixed sampler units and
consistent rebinding are now checked against the installed engine's actual include expansion
and uniform collector. Extending the separate liquid-depth image across cached water lets the
same ground treatment operate through cached water too. Primary terrain depth is not written.

The extension introduced pale moving blips. Captures localized sampled blips to ground color
before either water surface was blended, coinciding with exact clear (`1.0`) liquid-depth
texels. Bounded repair reads an immutable copy and fills only small enclosed holes whose two
axes predict matching depth, leaving existing depth intact. By 0.4.15 the owner reported blips
gone except at the seam. Turning the cached wave strip off did not remove the residual moving
pattern; this does not disable vanilla's own water animation.

### 2. Dedicated renderer, with appearance held fixed

The owner approved a revised plan: extract the surface program, move composition to vanilla's
OIT stage, then resolve boundary continuity before decorative features. Reuse the cache,
selection, mesh format and suitable water meshes. No blanket surface overlap or camera-radius
boundary, and no assumption that six OIT attachments imply a particular cost multiplier.

- **0.4.16:** dedicated water shader and shared geometry/appearance includes. Owner reports
  legacy and dedicated views identical. The offline checks passed 22,066 assertions.
- **0.4.17:** dedicated water draws at OIT/0.38, after vanilla liquid. A frame/world identity
  keeps the prepared selection alive through the later pass and rejects duplicate/stale
  callbacks. Opaque fallback is selected before drawing when OIT is unavailable; mid-pass
  failure defers fallback to the next frame to avoid double blending. Indexed blending is
  preserved; primary depth writes remain disabled. Checks passed 22,162 assertions.
- Owner reports no visible difference between opaque and OIT pipelines. Logs confirmed
  324 OIT water draws, no skip/failure, and zero OIT draws when opaque was selected. This is
  evidence for the tested view, not full overlapping-transparency/lifecycle acceptance.

Both pipeline samples reported water GPU p95 of 75 us and depth+repair p95 of 25 us.
These are histogram upper bounds in 25-us buckets at this scale, not exact equality or a
controlled benchmark. Surface timing resets on pipeline changes. The run's GPU capability
probe encountered a pre-existing `InvalidValue`, leaving terrain on its legacy fallback.
Water OIT ran successfully; the cause of the earlier GL error and normal GPU terrain-path
coverage remain open. Initial shader errors preceded mod includes; the later successful
reload was confirmed, and the startup issue summary repeated the initial errors.

### 3. Captured residual and 0.4.18 junction depth

Capture roots below are under `%APPDATA%/VintagestoryData/VintageHorizons/water-captures/`:

| Capture | Evidence |
|---|---|
| `20260905-151117-425-825d9469` (0.4.14) | Sampled scattered blips coincide with combined-depth holes before surface color; recorded repair fixture. |
| `20260905-205243-979-fd669b48` (0.4.16) | Remaining sampled seam marks already occur on the seabed before surface blending. |
| `20260906-004105-065-267351c0` (0.4.17) | OIT active, 324 depth and surface draws, no failures. Mask-only opaque-end image equals pre-water image; seam blips remain before composition. |

The 0.4.17 capture is 1920x1080 color, 480x270 liquid depth, configured vanilla distance 128,
effective far distance 8192, flat opacity capped at 0.3, clarity 0, absorption off,
wave strip 0, glint/foam 0, and murk/coverage/repair on. Five sampled bright pixels at
(564,535), (675,523), (822,507), (1044,483), (1388,531) all read exactly `1.0` in vanilla,
unrepaired combined and repaired depth. This attributes those samples, not all broad-band
pixels. Repair changed 45 texels elsewhere; clear sky/openings are not defects by themselves.

The depth pass had discarded cached geometry wherever the chunk mask assigned vanilla
ownership, regardless of actual raster coverage. **0.4.18** freezes vanilla-only depth before
appending cached water. An owned water fragment may supply depth only if that exact pixel
was clear and its column touches a cache-owned neighbour (including corners). It uses real
cached geometry and its rasterized depth, not interpolation or expanded geometry. Existing
vanilla depth and the interior of its footprint remain authoritative. Surface ownership and
color are unchanged. This is deliberately a depth-only exception to the shared mask rule.

The immutable copy shares allocation sequentially with repair's later combined-depth snapshot;
it is never sampled while attached to the draw framebuffer. Resource/copy failure retains the
old mask-only coverage. The added unit 10 is registered and restored. Ordinary frames draw the
water-depth meshes once; requested captures additionally render a mask-only reference before
the junction pass so coverage changes can be compared within one frame. Copy work is included
in depth timing; explicit diagnostic readback frames are excluded from its GPU samples.

**Owner result on 0.4.18, with repair off:** the seam blips go away with the junction correction;
scattered blips elsewhere remain. The broad band does not change. Thus junction depth and
bounded repair address distinct remaining coverage symptoms. This does not establish a
repair-free renderer, eliminate every thin-line mechanism, or close the broad-band gate.
No new 0.4.18 capture/log inspection is claimed by this documentation update.

---

## Delivered

- Private 0.4.18 zip built, byte/CRC/manifest verified and copy-installed; all older zips retained.
  SHA256 `F7E870777C1AAC1A890BDF1FADE0D1494104FAC50DFC879DA730E7C88A35E24C`, 32 entries.
- Latest Release checks: **22,304 assertions, zero failures**. No offline GLSL compilation claim.
  Shader runtime/visual evidence comes from the owner's game runs.
- `.vhwater shader dedicated|legacy`, `pipeline oit|opaque`, `junction on|off`, `repair on|off`,
  `murk on|off`, `coverage on|off`, `surface on|off`, `log`, and `capture` support comparison.
  These diagnostic switches are session-only. Junction defaults on; repair defaults on.
- Capture includes vanilla depth, optional same-frame `02-mask-only-liquid-depth`,
  `02a-unrepaired-liquid-depth`, repaired combined depth, ground depth/color, opaque-stage end,
  final composition, frame identity, effective settings, projection/view and wave inputs.
- Documentation validation: `dev/DocCheck.ps1` ran 2,075 checks. Its only five failures
  are personal Windows paths in Git-ignored diagnostic logs: `.testdata/water-extraction/`
  `build.txt` and `checks.txt`, plus `.testdata/water-oit/` `build.txt`, `checks.txt` and
  `checks-junction.txt`. `git check-ignore -v` confirms all five under `.gitignore` line 24.
  The checker labels these tracked text despite enumerating ignored files. No doc version,
  compatibility, source-count, link or shader-ASCII failures remain. Logs were preserved.
- Documentation reconciled at the owner's request. No commit, push, public release, new version
  bump, or agent-launched game run is part of this documentation turn.

## Decisions

- Remain in the water plan's Phase 2 continuity gate. Broad-band attribution and remaining
  dependence on repair block the first milestone; foam/shadows/extra waves remain later work.
- Keep seam blips, scattered blips, the residual thin line and the broad band separately tested.
- Preserve repair as a bounded fallback and diagnostic. Do not widen it as a substitute for
  explaining the remaining interior depth holes.
- Next investigation: inspect the owner's latest 0.4.18 capture/log if available; compare its
  mask-only, junction and repaired depths. Independently localize the broad band's earliest
  stage. Investigate the startup GPU fallback before generalizing performance claims.

## Traps

- Chunk ownership does not guarantee per-pixel water depth. Sampled seam holes existed before
  blending; OIT migration alone could not fix them. Use actual coverage and geometry evidence.
- Hiding pale pixels with murk or repair is not proof of a continuous water surface. Separate
  raw, corrected and repaired depth, and record the owner's distinct symptom outcomes.
- Startup error summaries can repeat already-resolved compilation failures. Trace timestamps,
  successful reload and effective draw state before declaring a pipeline unavailable.
- Equal percentile buckets are not exact timing parity. Record bucket precision and renderer
  fallback state. The unrelated startup GL failure remains unattributed.
- A final build attempt was interrupted by the approval service's usage limit. It was retried
  successfully after the owner resumed; final build/package verification was completed.

## Flagged and unverified

- **Judgement calls awaiting human review:** long-term acceptance of bounded repair; the
  junction-depth exception across shallow shores, chunk corners and stale/loading cache;
  final water appearance and optional later features.
- **Evidence still missing:** broad-band cause/fix, repair-free scattered coverage, complete
  thin-line/surface continuity, the plan's above/below-water and loading/movement matrix,
  overlapping transparency, resize/reload/world-change and failed-resource runtime coverage,
  0.4.18 capture analysis, measured junction-copy cost and normal GPU terrain-path comparison.
- Retrieved Nexus water-radius memory predates the current chunk mask. Live source and this
  session take precedence. No Nexus write was retried: an earlier automatic review rejected
  exporting the private handoff; this session's durable record is local.
