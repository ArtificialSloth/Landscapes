# Session 82 — streaming-water improvement accepted after terrain-safe rollback

**Date:** `2026-09-09`
**Branch/commit:** `codex/water-depth-light`; accepted 0.4.39 source committed with this record
**Mod version:** `0.4.37-0.4.39`; 0.4.39 human-tested and accepted
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 81 closed the settled vanilla/cache water seam in 0.4.36. The remaining issue appeared
only during movement: when vanilla first drew a newly available water chunk, that chunk could be
briefly lighter before settling. Source tracing pointed to the deliberate two-frame delay before
the shared readiness tracker transfers ownership to vanilla. That delay protects terrain from
holes, so the first correction was intentionally confined to cached OIT water.

The owner performed every in-game test. The assistant did not launch Vintage Story. Versions
0.4.37-0.4.39 form one useful acceptance sequence: a narrowly scoped improvement, an overreach
into shared terrain state that was rejected, and a complete rollback to the safe improvement.

## Work narrative

### 1. Version 0.4.37 used only same-frame water images

The OIT water fragment path already receives two immutable images from vanilla's current frame:
quarter-resolution liquid depth and full-resolution revealage. Version 0.4.37 suppresses a cached
water surface pixel before durable chunk ownership catches up only when **both** images agree that
vanilla liquid drew there. Neither signal can suppress cached water alone: revealage may belong to
glass or another transparent material, while quarter-resolution liquid depth is spatially coarse.

This retained the 0.4.36 settled-junction rule, where full-resolution revealage replaces coarse
depth for visible surface coverage, and did not change the shared readiness tracker, ground mask,
terrain ownership or mesh residency. The Release build passed 22,656 assertions with zero
failures. The 32-entry 0.4.37 package was installed with SHA-256
`21FC1357BB8295B03C0FDFD3E61BC5609C61F341E172423124F9AD5DC25FB58B`.

The owner found no holes. The light patches remained but were much shorter and barely
perceptible—a clear improvement, though not absolute removal.

### 2. Version 0.4.38 crossed the terrain boundary and was rejected

A follow-up attempted to bridge the frame in which revealage could arrive before coarse liquid
depth. It recorded an engine-announced chunk-presence fact in a spare channel of the shared
readiness mask, carried that fact through mask rebuilds and publication, and let water combine it
with revealage. Although the design intended presence to remain weaker than ownership, it modified
the same tracker, mask and shared ownership shader used to protect opaque terrain.

The automated suite grew to 22,674 passing assertions and the water looked fine in game, but the
owner found a serious regression: large holes in terrain and the seabed. This made 0.4.38 wholly
unacceptable. The individual statement responsible inside the presence-mask bundle was not
isolated, and no such attribution is needed for the product decision—the complete shared-state
extension was rejected.

### 3. Version 0.4.39 completely restored 0.4.37 behavior

The rollback removed every 0.4.38 presence-channel change from the readiness tracker, readiness
mask, chunk-dirty path, shared ownership shader and their tests. Version 0.4.39 retains only the
water-only 0.4.37 fragment veto and its focused checks; apart from the new version identity, its
rendering behavior is the 0.4.37 candidate. The archive comparison found all non-version payload
content equivalent to 0.4.37; two shader files differed only in line endings.

The complete Release suite returned to the 0.4.37 count: **22,656 assertions, zero failures**.
The build produced zero warnings and zero errors. The verified 32-entry archive contains the root
DLL, manifest, LICENSE and required shader assets, with no PDB or game DLLs. The distribution and
installed copies share SHA-256
`1791924D1FFAEF6575EA70013079E660DE212B81ACD8229FFBF8B9AE0BB62863`.

The owner tested 0.4.39 and called it acceptable. Terrain and seabed safety are restored. The
small, barely perceptible streaming-water flicker left by 0.4.37 is accepted in preference to any
risk of a ground hole.

### 4. Water development now has a hard terrain-safety boundary

Future work on the residual streaming flicker must stay inside the dedicated water stage and use
facts that cannot change opaque terrain or seabed ownership. Water development must not extend,
reinterpret or republish the shared terrain readiness mask, and must not weaken the two-frame
terrain proof. Any proposal that changes terrain ownership is separate terrain work and needs
explicit owner authorization and its own terrain-first regression case.

This is stronger than saying that 0.4.38 happened to be buggy. The accepted trade is explicit:
a momentary water-only artifact is safer than a single missing terrain cell.

---

## Delivered

- Version 0.4.37 added a same-frame cached-OIT veto only when vanilla liquid depth and revealage
  both prove vanilla water at the pixel; the owner found the transient patch much shorter and saw
  no holes.
- Version 0.4.38's shared chunk-presence/readiness-mask extension was rejected after the owner
  found severe terrain and seabed holes.
- Version 0.4.39 removed the entire 0.4.38 extension and restored the accepted 0.4.37 behavior.
- The owner human-tested and accepted 0.4.39. Release build and all 22,656 assertions passed.
- The verified and copy-installed 0.4.39 package has SHA-256
  `1791924D1FFAEF6575EA70013079E660DE212B81ACD8229FFBF8B9AE0BB62863`.
- Documentation validation passed 2,189 of 2,194 checks. The five failures are the known personal
  Windows paths in ignored `.testdata` water-diagnostic logs; current version, compatibility,
  links and routing pass.
- Compatibility remained 1/4/6. No game process was launched by the assistant, and nothing was
  pushed, tagged or publicly released.

## Decisions

- Accept the residual barely perceptible loading flicker at 0.4.39 rather than spend terrain
  safety to remove it completely.
- Keep the 0.4.37 water-only same-frame depth-plus-revealage veto.
- Reject the complete 0.4.38 engine-presence channel and its changes to shared readiness state;
  do not salvage pieces of it without a separately authorized terrain investigation.
- Treat opaque terrain and seabed continuity as non-negotiable during water development. Shared
  terrain readiness, ownership and mask-publication semantics are frozen for water fixes.
- Keep the 0.4.36 settled seam and alpha 1.3 / foam 1.6 / clarity 40 material defaults unchanged.

## Traps

- A spare mask channel is not an isolated feature when its lifetime is implemented inside shared
  rebuild, publication and ring-slot code. Tests of channel meaning did not prove terrain safety.
- A water symptom does not make a shared ownership mechanism safe to change. The consumer may be
  water-only while the producer and lifetime rules still govern terrain.
- Passing source and harness checks cannot establish the absence of rendering holes. Terrain and
  seabed continuity require the owner's in-game verdict.
- When a narrow improvement is safe and a broader follow-up regresses terrain, revert the entire
  broader change before diagnosing it. Do not tune around holes while leaving shared state altered.

## Flagged and unverified

- **Accepted limitation:** a very brief, barely perceptible light flicker can remain when a new
  vanilla water chunk begins drawing. It is no longer a release blocker at the current severity.
- **Unisolated cause:** the 0.4.38 regression is established for the shared presence-mask change
  set as a whole; the exact internal statement that produced holes was not captured or bisected.
- **Watch item:** movement-only white blips in open cached water remain non-reproducible. Capture
  them while moving before changing glint, foam, depth repair or ownership.
- **Missing evidence:** the broader continuity matrix still includes corners, LOD joins,
  shallow/deep and underwater travel, stale cache, resize/fullscreen, shader reload, world change,
  unrelated transparency and a second graphics driver.
