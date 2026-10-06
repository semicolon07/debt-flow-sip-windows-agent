# SIP Windows Agent P5 D performance hardening result

## Status

P5 D source implementation is complete and release-gated. Control-plane hot paths no longer perform
avoidable SQLite scans, per-call audio-device probes, per-event replay delivery waits or UTF-8 decode and
re-encode cycles in the file logger. Windows and PBX benchmark evidence remains required before production
performance acceptance.

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
| Audio | Caches the working playback device and preferred process audio sessions; device notifications invalidate both caches |
| Preferences | Avoids rewriting the audio preference file when the selected volume value is unchanged |
| Replay | Queues up to 250 durable events and waits for one page delivery barrier while preserving ordering and bounded memory |
| Protocol | Parses WebSocket messages from `ReadOnlyMemory<byte>` backed by `ArrayBufferWriter<byte>` without an intermediate copy |
| Logging | Queues UTF-8 bytes, drains batches and flushes within 1 second while preserving redaction, rotation and overflow summary |
| Metrics | Adds coordinator, storage, audio probe, replay and dropped-log instruments under meter `DebtFlow.SipAgent` |

## Verification

| Check | Result |
| --- | --- |
| Release cross-build | PASS, 0 warnings and 0 errors |
| Core tests | PASS, 79 of 79 |
| Windows host test project | COMPILE PASS |
| Replay page ordering and barrier test | COMPILE PASS; execution requires Windows Desktop runtime |
| Cached storage health regression | PASS |
| Whitespace verification | PASS with the existing workspace-load warning |

The Windows sampler is `scripts/measure-p5d-performance.ps1`. Record baseline and optimized evidence with
`docs/manual-verification/p5d-windows-performance-result-template.md` on the same Windows machine, PBX,
network path and audio devices.

## Remaining release gates

- Execute all host tests on Windows Desktop runtime.
- Compare idle, active-call and soak CPU, memory, handles and threads against the pre-change baseline.
- Measure at least 30 outbound-start and inbound-answer samples.
- Verify reconnect replay with 250, 1,000 and 10,000 pending events.
- Verify default-device, Bluetooth disconnect and device-return paths.
- Complete the existing two-hour or 50-call PBX soak and privacy review.

Until these gates pass, the status remains `Implementation complete — release gated`.

