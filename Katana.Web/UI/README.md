# Katana local editor UI

Vue 3 + strict TypeScript. All amp communication goes through the local, same-origin `/api` endpoints using fetch. There is no Web MIDI or external service/font dependency.

## Workflow

Requires Node.js 22.18+ (or Node.js 24+) and npm. From `Katana.Web/UI`:

```sh
npm ci
npm test
npm run build
```

`build` type-checks the Vue application and writes production assets to `../wwwroot`, clearing that output directory. Start the .NET web host using the repository's root README workflow; its root serves this editor. Vite's standalone development server (`npm run dev`) serves the shared editor and proxies same-origin `/api` requests to the standalone .NET host at `http://127.0.0.1:5025`. Start that backend separately; if you change its port, update the local proxy target too. No cross-origin device access is enabled.

## Shared editor

`src/App.vue` injects the local API adapter into the Vue editor in `src/editor`. All requests use same-origin `/api` routes.

## Behaviour

- Connect explicitly. The editor polls the amp while connected and idle, and pauses polling during edits and writes.
- Controls and effect groups come from parameter metadata. Volume is **channel volume**; physical master volume is not controlled.
- Live changes use one serial write queue, coalescing pending values by control. Newer pending values overlay authoritative device responses until acknowledged. Failed writes stop the queue and require refresh/reconnect before editing, capturing or committing again.
- Amp reads, preset operations and commit are disabled while writes are active. Controls remain live while writes drain, but are disabled during other operations.
- Saving a named app preset captures the amp without committing a hardware slot. The library captures the known original patch profile, not necessarily every Artist firmware setting.
- Applying an app preset or saving the working sound overwrites the selected A1–B4 slot after confirmation.
- Presets can be renamed and deleted; deletion requires confirmation. Dates use `en-AU` formatting.

## Checks

`npm test` exercises serialisation, coalescing, pending-edit preservation and write-failure recovery with controllable asynchronous responses, plus API routes, JSON mutation headers and problem-detail error handling. `npm run build` checks strict TypeScript/Vue templates and production bundling. Real USB behaviour must be verified with the local backend and connected Artist; the UI does not pretend a successful build verifies hardware.
