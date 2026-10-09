# SIP Windows Agent P5 D performance result

## Test identity

| Field | Value |
| --- | --- |
| Date and time | |
| Tester | |
| Commit and artifact checksum | |
| Windows version and build | |
| CPU and RAM | |
| Audio capture and playback devices | |
| PBX and network path | |
| Portal and API version | |

## Baseline and optimized runtime

Run the following command against the same process workload before and after the change:

```powershell
scripts\measure-p5d-performance.ps1 -ProcessId <agent-pid> -DurationSeconds 900 -CollectRuntimeCounters
```

The optional switch requires `dotnet-counters`. Attach the generated summary JSON, process sample CSV and
runtime counter CSV. Confirm `runtimeCounterCollectionSucceeded=true`; the counter file must contain
`System.Runtime` and `DebtFlow.SipAgent` so allocation rate, GC pauses, coordinator wait, storage latency,
audio probes, replay duration and logging failures can be compared without adding identifiers to application logs.

| Scenario | Duration | CPU average and p95 | Working/private growth | Handle/thread change | Result |
| --- | ---: | --- | --- | --- | --- |
| Idle registered | 15 minutes | | | | | |
| Active call | 15 minutes | | | | | |
| 50 call or 2 hour soak | | | | | | |

| Runtime counter | Baseline p50 and p95 | Optimized p50 and p95 | Result |
| --- | --- | --- | --- |
| Allocation rate | | | |
| GC pause duration | | | |
| Coordinator queue wait | | | |
| SQLite operation duration by bounded operation tag | | | |
| Replay duration and event throughput | | | |
| Process disk read and write rate | | | |
| Logger dropped records and writer failures | | | |
| SIP signal processing failures | | | |
| Background task faults by bounded operation | | | |
| WebSocket queue high-water by lane | | | |
| WebSocket queue aborts and realtime drops | | | |
| Command prune failures | | | |

## Control latency

Use one Windows machine, PBX account, network path and audio devices for both runs. Record at least 30 samples.

| Measurement | Baseline p50 and p95 | Optimized p50 and p95 | Regression | Result |
| --- | --- | --- | --- | --- |
| Outbound command to SIP trying | | | | |
| Inbound answer command to connected | | | | |
| Audio media ready after connected | | | | |
| Tray snapshot response | | | | |

## Replay recovery

| Pending events | Baseline duration | Optimized duration | Ordered and complete | Peak working set | Result |
| ---: | ---: | ---: | --- | ---: | --- |
| 250 | | | | | |
| 1,000 | | | | | |
| 10,000 | | | | | |

Confirm that live events generated during replay appear after the replay boundary and that reconnect does not
duplicate an acknowledged durable sequence.

แนบ diagnostic `summary.json` ก่อน/หลัง workloadและตรวจ `coordinatorProcessorState`, `sipSignalPumpState`,
background task count, WebSocket session generation, lane depth/high-water, dropped realtime, queue abortและ
writer fault count. ทุก fieldต้องไม่มี call ID, command ID, destination, Originหรือ credential.

## Audio device cache

| Scenario | Expected result | Evidence | Result |
| --- | --- | --- | --- |
| Second call with unchanged devices | Reuses cached playback device without full probe | | |
| Default playback device changes | Cache invalidates and next call probes the new inventory | | |
| Bluetooth disconnect during call | Media degradation is reported and cleanup completes | | |
| Device returns before next call | New call initializes capture and playback successfully | | |
| Inventory notification unavailable | Cached snapshot refreshes within 30 seconds without blocking tray/coordinator | | |
| Audio test overlaps outbound call | Call command gets `audio_device_busy`; no SIP side effect | | |
| Audio test overlaps inbound call | INVITE is rejected unavailable; test finishes and reservation releases | | |

## Logger failure recovery

| Scenario | Expected result | Evidence | Result |
| --- | --- | --- | --- |
| Log directory temporarily denied | Call control remains responsive; writer-failure counter increases | | |
| Directory becomes writable | Pending line is persisted after bounded retry | | |
| Disk remains unavailable during Exit | Shutdown remains bounded; process does not hang | | |

## Durability and privacy checks

- [ ] `PRAGMA synchronous=FULL` remains active.
- [ ] `PRAGMA secure_delete=ON` remains active.
- [ ] Clean shutdown truncates WAL.
- [ ] Event ACK never advances across a missing sequence.
- [ ] Performance metrics and evidence contain no phone number, SIP credential, Origin, call ID or command ID.
- [ ] Logs remain redacted and bounded during replay and device-failure bursts.

## Decision

| Field | Value |
| --- | --- |
| Performance acceptance | PASS or FAIL |
| Durability acceptance | PASS or FAIL |
| Privacy acceptance | PASS or FAIL |
| Remaining issue and owner | |
| Release recommendation | |
