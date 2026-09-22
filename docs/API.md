# API v1

## 0.4.0 additions

`GET /v1/environment?body=<internal-body-name>&altitude=<metres>` returns pressure (kPa), density (kg/m³), temperature (K), sound speed (m/s), local gravity and oxygen availability using the installed body's API. `world` adds the active aerodynamic model and native lift/drag multipliers.

Catalog parts add semantic `roles`, propellant ratios/densities/flow modes, engine thrust directions and curves, stock lifting/control-surface coefficients and curves, intakes, wheel roles, crossfeed and separator metadata. `supported:false` engine models cannot be used to claim a performance pass.

0.4.1 adds native `wheelType` (FREE/MOTORIZED/LEG), aerodynamic `omnidirectional`, `airbrake` and `disabledByNode` flags. Heat shields, air brakes and landing legs are not promoted to main-wing/rolling-gear roles simply because they share KSP base modules.

Validation adds a `placements` array (`id`, part-aligned metre position, quaternion rotation). SPH roots use the native +90° X rotation. Surface parts may use `surfaceOrientation: wing|fin|default`; `mirrorOf` references an earlier matching surface part with the same stage/default resources and a compatible parent. Native `.craft` output includes Mirror counterpart links.

Natural-language design uses these facts for deterministic preflight screening and feeds failed metrics back to the model. Generic `/validate` and `/build` remain structural tools, not flight certification endpoints. See `PERFORMANCE.md` for numerical assumptions and limitations.

Base URL: `http://127.0.0.1:18080`. Auth: `Authorization: Bearer <settings.json token>`.
Only the running VAB/SPH scene serves requests. Responses and POST bodies are UTF-8 JSON. In 0.3.0 the game UI starts a hidden one-shot Python worker automatically for health checks, and on explicit design clicks for generation. Generation permission is remembered in `PluginData/desktop.json` (default enabled).

## Craft plan

`POST /v1/validate` and `POST /v1/build` accept the same object. Required: `name`, `facility`, `parts`.

```json
{
  "name": "Single Pod",
  "facility": "VAB",
  "parts": [
    {
      "id": "pod",
      "partName": "mk1pod.v2",
      "stage": -1,
      "rollDegrees": 0,
      "resources": [{"name": "ElectricCharge", "amount": 40}]
    }
  ],
  "maxWetMassTonnes": 2,
  "maxCost": 2000
}
```

- `name`: 1–80 characters, nonblank; no controls or `{ } = \ /` characters. Used as craft title, never a path.
- `facility`: exactly `VAB` or `SPH`. Build requires matching the current editor; validate can examine either.
- `parts`: 1–128 items, parent-before-child order. Each requires `id`, exact catalog `partName`, and `stage`.
- `id`: unique ASCII `[A-Za-z0-9_-]`, 1–48 characters; case-sensitive.
- First part is the sole root: omit or empty `parentId`, `parentNodeId`, `childNodeId`.
- Every other part supplies `parentId`, `parentNodeId`, `childNodeId`. Both nodes must exist, be stack-attachable, have matching `size`, and be unused.
- In 0.2.0 a surface child instead supplies `attachment: "surface"`, `parentId`, `radialAngleDegrees` in `[-360,360]` and `surfaceHeight` in `[-1,1]`, with no stack node IDs. Both parts' surface attach rules must allow it. Height is a fraction of the parent's half-height, and angle runs around local Y. Placement is an elliptical approximation from prefab dimensions, not collider intersection. Root parts cannot use surface attachment.
- `stage`: integer JSON token, `-1` (unstaged) or `0–99`. KSP inverse-stage semantics: **highest numbered stage fires first**. No fractional/exponent tokens; staging is not inferred from topology.
- `rollDegrees`: finite `[-360, 360]`, default `0`; root roll is about +Y, child roll is about the opposing parent node normal in world coordinates.
- `resources`: optional array of unique `{name, amount}` pairs (up to 128). Unspecified resources keep defaults; supplied amounts must fit the existing resource capacity. No added resource types or capacity editing.
- `maxWetMassTonnes`/`maxCost`: finite nonnegative numbers, default `0` meaning unconstrained. Estimates use prefab defaults, not a mission feasibility calculation.
- Unknown/duplicate JSON keys, numeric strings, nonfinite numbers, missing required fields and trailing JSON are rejected. This API does not accept raw craft text, asset names, file paths or scripts.

Success: `{valid:true, partCount, wetMassTonnes, estimatedCost, warnings, craftFile}`. `craftFile` is populated only by build; it identifies a new file in `PluginData/Builds` and is not a remote load handle. Validation supplies explicit limitations in `warnings`.

Only the latest generated candidate appears in the UI during that editor session. Older candidates remain on disk. A new session resets the current task UI; 0.3.0 restores the saved generation permission. Manually copy a retained candidate or backup into the save's `Ships/VAB` or `Ships/SPH` folder if you want to load it through the normal craft browser.

## Catalog

`GET /v1/catalog?offset=0&limit=100&search=...` returns `{total, offset, parts}`. `offset >= 0`, `limit = 1..200`. Search matches internal name or display title, case-insensitively. Paging is sorted by internal name. Locked and hidden/legacy loaded parts may appear; inspect `unlocked` before selection.

Each part includes:

- Internal `name`, localized/display `title`, `category`, `unlocked`, `crewCapacity`.
- `stackAttach`, `allowStack`, `nodes`: stack `id`, `size`, prefab-local `position`/`orientation` vectors `{x,y,z}`.
- `dryMassTonnes`, `wetMassTonnes`, `defaultCost`: default prefab estimates, including module contributions.
- `resources`: `name`, `amount`, `maxAmount`, `densityTonnesPerUnit`, `unitCost`.
- 0.2.0 adds `experimental`, `moduleNames`, `experiments` (science experiment IDs), `surfaceAttach`, `allowSurfaceAttach`, `prefabSize`, and `surfaceNode`. Temporary experimental unlocks count as available. All geometry is default-prefab geometry.
- 0.2.2 derives `prefabSize` and `prefabCenter` from part model meshes because the native prefab size field is not initialized in the catalog. `geometrySource` is `default-mesh-bounds`, `all-mesh-bounds`, `prefab-size`, `stack-node-estimate`, or `unavailable`; surface placement remains approximate.
- 0.2.3 uses the native editor's surface rotation convention: `LookRotation(surfaceNormal,parentUp) * LookRotation(childSurfaceOrientation,up)`, plus requested roll. This is intentionally different from opposing stack-node normals. Large modded catalog pages may exceed the transport's 256 KiB response bound; the designer uses smaller, adaptively reduced pages.
- `engines`: `engineId`, `nominalMaxThrustKn`, `vacuumIspSeconds`, `seaLevelIspSeconds`, propellant names.

Engine values are independent module definitions, not the combined thrust of an operating stage. Sea-level Isp is evaluated at 1 atm, not necessarily the installed home world's surface pressure. Multimode engines, airbreathing flow curves, propellant ratios and fuel connectivity need a future simulation layer.

## Ship state

`GET /v1/ship` returns `name`, `facility`, `partCount`, `dryMassTonnes`, `resourceMassTonnes`, `wetMassTonnes`, `cost`, `estimatedCenterOfMassWorld`, `homeBody`, `homeSurfaceGravity`, `parts`, `limitations`.

`parts` contains the editor's native `craftId`, `partName`, `parentCraftId` (0 for root), `stage`. Native craft IDs are separate from logical plan IDs. CoM is an approximation, particularly for physicsless/module/crew mass handling. This endpoint does not report invented delta-v/TWR/CoL values.

## Contracts and world (0.2.0)

- `GET /v1/contracts?state=active&offset=0&limit=50`: read-only summaries with stable GUID `id`, title/type/state/prestige and deadline/expiry universal times. `state` is `active`, `offered`, or `all`; max page size 100. Response: `{available,gameMode,total,offset,contracts}`. Sandbox may return `available:false` and an empty list.
- `GET /v1/contracts/<GUID>`: `{schemaVersion,contract,synopsis,description,notes,saveName,gameMode,universalTime,requirements,warnings}`. Only the current contract collection is queried; absent IDs return 404. It never accepts or modifies contracts.
- `GET /v1/world`: current save/mode/funds and installed celestial bodies. Fields include `name`, display name, parent, home-world flag, radius (m), gravitational parameter (m³/s²), surface gravity (m/s²), atmosphere depth (m), sea-level pressure (kPa), and sphere of influence (m, 0 for unbounded/nonfinite values).

Requirement nodes include `id` (tree path), fully qualified `type`, title/notes, game `state`, `optional`, `logic` (`all`, `any`, `exactlyOne`), `kind`, bounded `facts`, and `children`. Supported static prerequisites expose `minimumCrewCapacity`, `requiredPart`, `requiredModules`, `crewMode`, `requireNewVessel`, `resourceName` and `minimumResource`. Part-request alternatives are exported for review without assuming their group semantics.

Stock extraction uses a bounded whitelist of fields/getters in KSP 1.12.5. Unsupported layouts produce `extractionIssue`/`unknown`, not silently satisfied conditions. Trees are limited to 256 visited parameters and depth 12. Titles/notes are bounded to 4000 characters. Contract progress remains a game runtime property; the Python assessment never equates a valid craft with completed contracts.

The external designer posts to the configured model's `/chat/completions` or `/responses`, never through the KSP HTTP server. `opencode_oauth` reads the existing OpenAI login without modifying it and defaults to Responses. Streaming responses require explicit completion; typed output events are reassembled if the final event omits aggregated output. The model returns a draft `plan` plus textual mission steps/assumptions/rationale. The client validates the plan with `/v1/validate` and assesses the actual contract tree separately, with at most three generation/correction attempts. It saves files only; build/load remain the existing explicit workflow. See [NATURAL-DESIGN.md](NATURAL-DESIGN.md).

## Failure and lifecycle

Errors: `{"error":{"code":"...","message":"..."}}`.

| Status | Typical reason |
| --- | --- |
| 400 | Invalid JSON/HTTP framing, unknown or duplicate plan fields |
| 401 | Missing/incorrect bearer token |
| 403 | File generation disabled, browser-origin request or invalid Host |
| 404 | Unsupported route/method combination |
| 409 | Build facility mismatch |
| 413 | Request exceeds body limit |
| 415 | Unsupported POST content type |
| 422 | Structurally invalid plan, missing/unavailable part, invalid catalog query |
| 500 | KSP/mod-specific operation failure |
| 503 | Editor unavailable, queue/connection limit, timeout |

HTTP transport accepts four concurrent connections, reads at most 8 KiB of headers and 256 KiB of body, closes connections after one response, and rejects `Origin`, `Transfer-Encoding`, ambiguous/duplicate framing. Header/body deadline is five seconds, response-write deadline two seconds. Only GET/POST and `Host: 127.0.0.1:<port>` are accepted.

Main-thread queue waits four seconds. `queue_expired` work has not started and is discarded. `outcome_unknown` work started; a candidate file may still be produced. Inspect the UI/disk before deciding whether to resubmit. There is no automatic POST retry or idempotency cache in v1.
