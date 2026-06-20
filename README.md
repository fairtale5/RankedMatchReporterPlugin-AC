# RankedMatchReporterPlugin

AssettoServer plugin that snapshots **race session results** at session end and **POSTs** them to **serv-brain** ingest (`POST /v1/races`). OpenSkill and Postgres writes stay in the brain — this plugin only reports match facts.

**Standalone repo:** [github.com/fairtale5/RankedMatchReporterPlugin-AC](https://github.com/fairtale5/RankedMatchReporterPlugin-AC)

**Monorepo path:** `serv-game/plugins/RankedMatchReporterPlugin/`

**Separate from [ScoreTrackerPlugin](../ScoreTrackerPlugin/)** — lap times stay on game hosts; ranked match history is central Postgres state.

---

## v1 focus (now)

1. Race end → build JSON from `EntryCarResult` + grid-at-start snapshot.
2. **`DryRun: true`** (default) → log payload until serv-brain ingest exists.
3. **`DryRun: false`** → HTTP POST to serv-brain → queue → worker → Postgres.

**How reporting works (no shared library):** [`docs/REPORTING-TO-BRAIN.md`](docs/REPORTING-TO-BRAIN.md)

**Deferred (chat, late-join noclip, etc.):** [`docs/NEXT-STEPS.md`](docs/NEXT-STEPS.md) · [`docs/roadmap/planned/ranked-match-reporter-deferred.md`](../../../docs/roadmap/planned/ranked-match-reporter-deferred.md)

---

## Configuration

Copy [`plugin_ranked_match_reporter_cfg.example.yml`](plugin_ranked_match_reporter_cfg.example.yml) to server `cfg/`. Add to `extra_cfg.yml`:

```yaml
EnablePlugins:
  - RankedMatchReporterPlugin
```

| Setting | Default | Purpose |
|---------|---------|---------|
| `DryRun` | `true` | Log JSON only — no HTTP until brain is up |
| `IngestUrl` | `http://127.0.0.1:10000/v1/races` | serv-brain POST target |
| `LeagueId` / `ServerId` | — | Identifiers in payload |
| `MinimumDriversForRanked` | `4` | Sets `counted_for_ranked` on payload |
| `PeakWindow` | 18:30–22:30 BRT | Sets `counted_for_ranked` on payload |

---

## Build

Included in the fleet solution — one publish builds server + all plugins:

```bash
cd serv-game/AssettoServer
rm -rf out-linux-x64
dotnet publish -c Release -r linux-x64
```

Output: `serv-game/AssettoServer/out-linux-x64/plugins/RankedMatchReporterPlugin/`

---

## Related docs

- Ingest contract: [`serv-db/docs/ranked-system-data-plan.md`](../../../serv-db/docs/ranked-system-data-plan.md)
- Product roadmap: [`docs/roadmap/planned/ranked-system-roadmap.md`](../../../docs/roadmap/planned/ranked-system-roadmap.md)
- serv-brain (planned): [`serv-brain/README.md`](../../../serv-brain/README.md)

---

## Status

🚧 **Phase A** — reporting scaffold on branch `feat/brainapi-database-ranked`. Next: serv-brain ingest + SQL migrations.
