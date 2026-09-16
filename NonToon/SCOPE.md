# NonToon (Fork) — Support scope and known limitations

> **Status: DEVELOPMENT CANDIDATE — not published.**
> This file states exactly what this shader package claims, what it does not, and what has not yet
> been measured. If something is not listed as supported below, treat it as unsupported.

## What this is

A fork of `lilxyzw/NonToon` **0.1.3**. It deliberately keeps the **same package id**
(`com.catandling.nontoon`) so that installing it upgrades the official NonToon in place.

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
| **SelfLight realtime mode / companion rig** | The runtime rig lives in the **tools package** and is **not part of this candidate**. |

## Removed in this candidate

- The **self-rendered linear-depth PCSS path** (`useUnityShadowMap = false`) and its **15
  `_SelfLightRt*` material properties**. They were inert at their defaults and no runtime path in the
  shipped flow ever enabled them, so the shader exposed 15 controls that could not do anything.
  The shader package now contains **no self-rendered depth code**.
- **Known inconsistency:** the companion tools package still contains the C# half of that path. It is
  scheduled for removal next; it is called out here rather than hidden.

The active realtime self-shadow design is unchanged: a light mounted by the companion tools package,
**Unity renders that light's shadow map, and the shader samples it**. Nothing in that path was removed.

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
