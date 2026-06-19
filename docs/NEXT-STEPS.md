# RankedMatchReporterPlugin — next steps

## v1 focus (now)

Get **match reporting** working end to end:

1. Run server with plugin enabled, `DryRun: true` — confirm JSON in logs after a real race.
2. Build **serv-brain** ingest (`POST /v1/races`) — see [`serv-brain/README.md`](../../../../serv-brain/README.md).
3. Apply **Postgres migrations** — see [`serv-db/migrations/`](../../../../serv-db/migrations/).
4. Set `DryRun: false`, `IngestUrl` → brain — verify queue row + worker writes ratings.

Details: [`REPORTING-TO-BRAIN.md`](REPORTING-TO-BRAIN.md)

---

## Deferred (after v1)

Planned in repo: [`docs/roadmap/planned/ranked-match-reporter-deferred.md`](../../../../docs/roadmap/planned/ranked-match-reporter-deferred.md)

| Item | Summary |
|------|---------|
| **Chat automessages** | Broadcast ranked window times (e.g. before peak, at race end). |
| **Late-join noclip** | `ranked_spectator` via `CollisionPenaltiesManager`; unify RaceStartNoclip first. |
| **POST retry** | Stable `match_id`, retry failed ingest. |
| **Weather category** | Add field when ingest schema adds it. |

**Not in this plugin v1:** harsher ranked penalties (CollisionPenaltiesPlugin), off-track noclip (PenaltyReporterNoClip + penalties plugin), preset warmup switch.
