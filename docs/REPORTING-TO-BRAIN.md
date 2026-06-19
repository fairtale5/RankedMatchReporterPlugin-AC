# Reporting to serv-brain — how it works

There is **no shared FastFox SDK library** between the game plugin and serv-brain today. The contract is **documented JSON** + a normal HTTP POST.

---

## End-to-end flow (today → target)

```text
Race session ends on AssettoServer
        │
        ▼
RankedMatchReporterFeature reads EntryCarResult + grid snapshot
        │
        ▼
MatchReportBuilder → MatchReportPayload (C# DTO)
        │
        ▼
BrainIngestClient → System.Text.Json.Serialize → POST application/json
        │
        ▼
serv-brain ingest API (not built yet) → ranking_jobs_open + stub matches row
        │
        ▼
Ranking worker → OpenSkill → Postgres (serv-db)
```

---

## What the plugin owns

| Piece | File | Role |
|-------|------|------|
| **DTO (JSON shape)** | `Models/MatchReportPayload.cs` | Maps C# fields to ingest keys (`match_id`, `league_id`, `participants`, …) via `[JsonPropertyName]` |
| **Build from game state** | `MatchReportBuilder.cs` | Reads `SessionState.Results`, peak window, min drivers → fills DTO |
| **HTTP send** | `BrainIngestClient.cs` | `JsonSerializer` + `HttpClient.PostAsync` to `IngestUrl` |
| **Contract doc** | [`serv-db/docs/ranked-system-data-plan.md`](../../../../serv-db/docs/ranked-system-data-plan.md) § Ingest payload | Authoritative field list |

**Not used:** gRPC, protobuf, shared NuGet package, or brain client DLL. If the ingest schema changes, update **DTO + data plan + serv-brain handler** together.

---

## serv-brain status

`serv-brain/` is **README only** — no Rust ingest binary yet. Until it exists:

- Keep `DryRun: true` in yaml → plugin logs JSON to server log (validate fields on a real race).
- Or point `IngestUrl` at a temporary mock that returns 200.

When ingest lands, expected endpoint:

```http
POST /v1/races
Content-Type: application/json
Authorization: Bearer <ApiKey>   # optional
```

Body: same JSON as dry-run log (see data plan sketch).

---

## Future: shared contract?

Optional later (not required for v1):

- OpenAPI spec in `serv-brain/` generated from the DTO shape.
- Or a tiny `fastfox-ingest-schema.json` in `serv-db/` both sides validate against.

For now, **the data plan + `MatchReportPayload.cs` are the contract.**
