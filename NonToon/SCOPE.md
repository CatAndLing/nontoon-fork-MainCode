# NonToon (Fork) — Support scope and known limitations

> **Status: published as `com.catandling.nontoon` 0.3.11 (2026-09-18).**
> It is still a fork of a 0.1.3-era upstream and several areas remain unmeasured — this file states
> exactly what the package claims, what it does not, and what has not yet been measured.
> If something is not listed as supported below, treat it as unsupported.

## What this is

A fork of `lilxyzw/NonToon` **0.1.3**. It uses its **own package id** (`com.catandling.nontoon`) and
its own shader names (`nontoon-fork`, `nontoon-fork-fur`, `nontoon-fork-twopass`) — deliberately
**not** the upstream id and names (`jp.lilxyzw.nontoon`, `NonToon`). Installing this fork therefore
does **not** upgrade the official NonToon in place: the two now coexist instead of colliding
(assembly names and asset GUIDs are retained, so existing material references keep working).
**Remove the old package before installing this one** — otherwise the same GUIDs are imported twice.

**It is not an official upstream release.** Upstream licence and attribution are retained.
Its display name is marked as a fork.

## Supported environment

| | |
|---|---|
| Unity | 2022.3 |
| Render pipeline | **Built-in RP.** URP is not validated in this candidate. |
| Target | VRChat **PC** avatars. Quest is not validated. |
| Declared dependency | `jp.lilxyzw.shadercore` `^0.1.9` |
| Install route | From a checkout / git source. **Not published to any VPM listing.** |

## Supported material modes

- **Opaque**, **Cutout**, **Transparent**. Two-pass transparency is available as the generated
  variant `nontoon-fork-twopass` (`tools/make-twopass.mjs` regenerates it; regenerating must produce no diff).
- Shading features represented: upstream **Shade gradient ramp** (the default shading path),
  lilToon-compatible **light min/max limits**, **Emission**, **MatCaps**, **RimLight**, **RimShade**,
  **HairSpecular**, **Specular**, **Details** (up to 4 normal/detail slots), **DistanceFade**,
  **Nearer**, **ShadowColor** (opt-in), **SelfLight** (baked private light).

## Experimental / not validated

| Feature | Status |
|---|---|
| **Fur** (`nontoon-fork-fur`) | Asset identity preserved. **Not validated on Radeon** — upstream issue "Fur expands unexpectedly on Radeon" is unfixed. |
| **MatCap VR stereo parallax** | Not validated against a real stereo rendering path in this candidate. |
| **Two-pass transparency** | Correctness verified numerically on one asset (effective-transmittance measurement). **Not** verified across a broad material matrix. |
| **SelfLight realtime mode / companion rig** | **Removed in tools 0.5.4; not implemented here.** See "Removed in this candidate" below — the depth-source decision is still open. |

## Removed in this candidate

- The **self-rendered linear-depth PCSS path** (`useUnityShadowMap = false`) and its **15
  `_SelfLightRt*` material properties**. They were inert at their defaults and no runtime path in the
  shipped flow ever enabled them, so the shader exposed 15 controls that could not do anything.
- Its renderer shader **`Shaders/Modules/SelfLight/NTRTDepth.shader` was deleted in 0.3.12** — its only
  caller was the tools package's C# half, which was removed in tools **0.5.4**. The shader package now
  contains **no self-rendered depth code**. ⚠️ That sentence was **false before 0.3.12** (the file was
  still shipped while this document claimed it was gone) — corrected here rather than left standing.

**The realtime self-shadow path is currently NOT implemented.** This document previously claimed
"the active realtime self-shadow design is unchanged … nothing in that path was removed". **That claim
was wrong and is withdrawn** (measured 2026-09-18):

- The tools package's runtime rig (`NTSelfRealtimeShadow`) was **removed in 0.5.4**, because the shader
  side had already stopped supporting it — the 15 `_SelfLightRt*` hooks went in **0.3.11**.
- A code search finds **no shader path that samples a private light's Unity shadow map**.
- The only self-shadow this shader performs reads the **baked** `_SelfLightShadowMap` (a distance map,
  so its PCSS does a genuine blocker search — but the shadow's shape is **frozen at bake time**).

⇒ Making realtime soft self-shadow work again requires deciding **where its depth comes from**:
Unity's shadow map (D3D11 exposes only a **comparison** sampler ⇒ PCSS must degrade to comparison
sampling) or a self-rendered linear depth map (which this project's frozen rules reject, so it would
have to be un-frozen deliberately). **Undecided.**

## Known limitations (inherited; not fixed by this fork)

- **A backlit subject in a world with no realtime shadow map looks wrong** (upstream issue #3).
  This fork does not fix it.
- **Fur expands unexpectedly on Radeon** (upstream issue #7). Not fixed.
- ShaderCore's property parser **does not skip `//` comments and does not follow `#include`** inside
  `<shader>_properties.hlsl`; that file must be pure declarations. Affects anyone extending this package.

## Explicitly NOT claimed

- **Not lilToon-equivalent.** lilToon features with no representation here include **Glitter**,
  **Emission2nd**, **Parallax**, **Refraction**, **Gem**, and lilToon's multi-layer / mask / animation systems.
- **Pixel equality with upstream 0.1.3 has not yet been measured** across a regression matrix.
- **Default-path performance parity is not established.** Pixel equality alone would not establish it,
  because a default-off module can still compile in work behind a uniform branch.

## Pending evidence (deliberately not done in this candidate)

1. An upstream-vs-fork **default regression matrix** (opaque / cutout / transparent / fur / a real avatar
   material, under several lighting conditions including a shadowless one and an avatar-mounted light),
   with per-region error thresholds and every intentional difference recorded in a ledger.
2. A **compiled-shader comparison** for the default base passes (instructions, passes, keywords, resources).
3. A **clean-project install test** (the dependency must resolve without an existing development project).
4. **Converter fidelity** — the jacket knit/speckle and hair hue-shift failures remain undiagnosed.
   This belongs to the tools package and is **not** part of this candidate.

## Blocker that must be decided before any real release

This fork **reuses upstream's package id**. Therefore any third-party package that declares
~~`jp.lilxyzw.nontoon: ^0.1.3` cannot resolve against 0.3.x~~ **已作废（2026-09-18）**：本 fork 已改用自有包 id `com.catandling.nontoon` @ 0.3.11，与上游 id 不再冲突。第三方包若依赖上游 `jp.lilxyzw.nontoon`，会各自安装、互不覆盖；但也**不会**作用于本 fork（LightLimit 例外：它按路径含 nontoon 扫描 .scshader，仍会命中本包）。
At least one published package does exactly that. Package identity must be settled — either an explicit
replacement policy with coordinated dependency support, or a distinct package/asset identity that can
coexist — **before** this is published to a listing.
