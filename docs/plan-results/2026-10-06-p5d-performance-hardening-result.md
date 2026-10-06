# SIP Windows Agent P5 D performance hardening result

## Status

P5 D source implementation and the 07/10/2026 meticulous follow-up are complete and release-gated.
Control-plane hot paths no longer perform avoidable SQLite scans, synchronous audio inventory enumeration,
per-call audio-device probes, per-event replay delivery waits or UTF-8 decode and re-encode cycles in the
file logger. Windows and PBX benchmark evidence remains required before production performance acceptance.

This phase does not change protocol V1, Agent version 1.0.0, SQLite schema version 4 or the portable artifact.
SQLite continues to use `synchronous=FULL` and `secure_delete=ON`.

## Implemented changes

| Area | Result |
| --- | --- |
| Coordinator | Records bounded queue-wait metrics without call, command or phone identifiers |
| Snapshot | Uses cached pending count, oldest event and storage bytes instead of querying SQLite every tray tick |
| Tray | Refreshes immediately at startup and then every 5 seconds |
| Command journal | Loads counters once and performs indexed command-state lookup instead of full-table capacity scans |
| WAL | Batches passive checkpoints by 250 acknowledged events or 30 seconds; explicit and shutdown checkpoints still truncate |
| Audio | Returns a cached immutable inventory on snapshot/tray paths; notification or 30-second fallback refreshes it off the coordinator, while playback positive/negative TTL and generation invalidation prevent permanent stale cache |
| Preferences | Avoids rewriting the audio preference file when the selected volume value is unchanged |
| Replay | Queues up to 250 durable events and waits for one page delivery barrier while preserving ordering and bounded memory |
| Protocol | Parses WebSocket messages from `ReadOnlyMemory<byte>` backed by `ArrayBufferWriter<byte>` without an intermediate copy |
| Logging | Queues UTF-8 bytes, drains batches and flushes within 1 second; sink failure retains the pending line and retries with bounded exponential backoff |
| Storage | Command-journal mutations invalidate cached DB+WAL bytes; oldest-pending time remains chronological even when event timestamps arrive out of sequence |
| Metrics | Adds coordinator, storage, audio probe, replay, dropped-log and log-writer-failure instruments under meter `DebtFlow.SipAgent` |

## Verification

| Check | Result |
| --- | --- |
| Release cross-build | PASS, 0 warnings and 0 errors |
| Core tests | PASS, 86 of 86 |
| Windows host test project | COMPILE PASS |
| Replay page ordering and barrier test | COMPILE PASS; execution requires Windows Desktop runtime |
| Cached storage health regression | PASS |
| Whitespace verification | PASS with the existing workspace-load warning |

The Windows sampler is `scripts/measure-p5d-performance.ps1`; schema v2 records resource growth and can start
`dotnet-counters` with `-CollectRuntimeCounters`. Record baseline and optimized evidence with
`docs/manual-verification/p5d-windows-performance-result-template.md` on the same Windows machine, PBX,
network path and audio devices.

## Remaining release gates

- Execute all host tests on Windows Desktop runtime.
- Compare idle, active-call and soak CPU, memory, handles and threads against the pre-change baseline.
- Measure at least 30 outbound-start and inbound-answer samples.
- Verify reconnect replay with 250, 1,000 and 10,000 pending events.
- Verify default-device, Bluetooth disconnect and device-return paths.
- Fault-inject logger directory denial/disk-full and confirm writer recovery plus
  `sip_agent.logging.writer_failures` without call-control failure.
- Complete the existing two-hour or 50-call PBX soak and privacy review.

Until these gates pass, the status remains `Implementation complete — release gated`.
