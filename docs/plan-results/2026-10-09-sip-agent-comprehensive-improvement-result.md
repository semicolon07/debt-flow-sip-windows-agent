# SIP Windows Agent comprehensive improvement result

วันที่: 09/10/2026  
Plan authority: [`2026-10-09-sip-windows-agent-comprehensive-improvement-plan.md`](../2026-10-09-sip-windows-agent-comprehensive-improvement-plan.md)  
Baseline: `150241ee0f85`  
สถานะ: **Mandatory source remediation implemented; full DoD และ production release evidence pending**

## Outcome

รอบนี้ปรับเฉพาะ repository `sip-windows-agent`. Protocol ยังคง V1, agent versionยังเป็น 1.0.0,
SQLite schemaยังเป็น v4 และไม่มี DDL/migration ใหม่ จึงรักษา binary rollback contractเดิม.

ปิด source blockerหลักดังนี้:

- SIP signal หนึ่งรายการล้มไม่ทำให้ pumpหยุดอ่าน channel; completionถูกจบครั้งเดียว, Agentเข้า degraded,
  ปฏิเสธสายใหม่ และ terminal callbackถัดไปยัง cleanupสายเดิมได้
- replay จำแนก SQLite load/health failureแยกจาก WebSocket delivery failure; transport disconnectจบเฉพาะ session
  และ committed durable eventsรอ replayรอบถัดไป
- `call.created + initial state` และ `terminal state + call.ended` commitพร้อม active-call journalใน transactionเดียว
  ก่อนเปลี่ยน in-memory stateและก่อน publish
- publisher failureหลัง commitไม่ถูก mapเป็น `outbox_unavailable` และไม่ทำให้ durable stateหาย
- background operationsของ coordinator/runtimeมี fault observationและ bounded drain; shutdownเดิน cleanupต่อแม้ task/pump/runtime disposeล้ม
- WebSocket outboundแยก control/durable/realtime lane, writerยังมีตัวเดียว, controlมี priority,
  durableมี fairness และ realtime overflow dropพร้อม metricโดยไม่ abort Agent/store
- command payloadถูก deserializeครั้งเดียวเป็น prepared commandที่รวม canonical payload, call scope และ execute delegate;
  pruneย้ายออกจาก request pathพร้อม supervision, failure metricและ retryหนึ่งนาที
- logger caller pathมี non-fatal isolation, regex timeout redaction, tray async wrapper, cached audio inventory count
  และ diagnosticsเพิ่ม coordinator/WebSocket/logger operational healthแบบไม่มี identifier
- แยก pure `CallLifecyclePlanner` และ reusable `BackgroundTaskSupervisor` เพื่อลด policy/I/O coupling

## Change map

| Area | Implemented behavior |
| --- | --- |
| Application contracts | เพิ่ม atomic `AppendCallTransitionAsync` และ immutable operational-health record |
| Call lifecycle | pure plan → atomic commit → state apply → best-effort publish |
| Signal supervision | per-signal isolation, degraded policy, reject-new-call และ pump continuation |
| SQLite | batch insert + active-call journal mutationใน transactionเดียว; schema v4คงเดิม |
| Background lifecycle | tracked fault observation, active count และ bounded shutdown drain |
| WebSocket replay | store failure boundaryแยกจาก send/disconnect; normal closeไม่ถูกเรียก timeout |
| WebSocket QoS | bounded control 64, durable 256, realtime 128 และ handshake activation buffer 512 |
| Command dispatch | prepared command sourceเดียวสำหรับ parse/canonicalize/call scope/execute |
| Maintenance | command pruneเป็น supervised background operationและ failure retryเร็วขึ้น |
| Logger/tray | top-level logging isolation, cached audio counts, safe UI async operations, bounded close/exit |
| Observability | bounded-tag countersและ diagnostic snapshotสำหรับ pump/task/queue/fault state |

## Automated evidence on this workstation

Environment: macOS arm64, .NET SDK 10.0.100, cross-targeting Windows enabled.

| Command/gate | Result |
| --- | --- |
| `dotnet restore softphone-native-client.sln --locked-mode -p:EnableWindowsTargeting=true -m:1 -nr:false` | PASS; 6 projects restored/up-to-date; no `NU1510` |
| `dotnet build softphone-native-client.sln -c Release --no-restore -p:EnableWindowsTargeting=true -m:1 -nr:false` | PASS; 0 warnings, 0 errors; Host and Host.Tests compile |
| Core Release tests | PASS, 92/92, 0 failed, 0 skipped |
| Project-level `dotnet format whitespace --verify-no-changes --no-restore` | PASS สำหรับ Application, Persistence, Host, Core.Tests และ Host.Tests; host projectsรายงาน workspace-load warningเดิมแต่ exit 0 |
| `git diff --check` | PASS |
| Windows Host tests execution | NOT RUN: macOSไม่มี `Microsoft.WindowsDesktop.App 10.0.0` |
| NuGet vulnerable-package query | INCOMPLETE: SDKจบด้วย `Sequence contains no matching element`; ต้องใช้ Windows verifier |

Regressionใหม่ที่ผ่านใน core suite:

- signal handler append failureหนึ่งครั้งแล้ว terminal signalถัดไปยังสร้าง `call.ended`
- publisher transport failureหลัง atomic commitไม่ degrade Agentและ durable pairยังอยู่ครบ
- second event insertชน unique constraintแล้ว rollback eventแรกและ active-call journalทั้งหมด
- lifecycle plannerสร้าง start/terminal event orderและ durationอย่าง deterministic

Windows Host test assemblyมี QoS regressionสำหรับ realtime saturationไม่ abort control lane แต่ผลรันจริงยัง pending
เพราะ Windows Desktop runtimeไม่มีบนเครื่องนี้.

## Definition-of-done result

### ผ่านจาก source/portable evidence

- [x] P0 signal-pump fail-stopถูกปิดพร้อม deterministic core regression
- [x] replay store/transport failure boundaryถูกแยกและ storage recoveryรองรับ `outbox_unavailable`
- [x] multi-event transitionและ journal commit atomic; injected second-insert failure rollbackครบ
- [x] publisher failureหลัง commitไม่เปลี่ยน store/agent health
- [x] background workมี owner, fault observationและ bounded drain
- [x] lifetime cancellationไม่ถูกแปลงเป็น timeout/runtime business errorใน runtime/audio wrappers
- [x] QoS lanesมี hard boundsและ realtime drop/control-durable failure policyชัดเจน
- [x] command pruneไม่อยู่ใน synchronous request latency path
- [x] logger/tray caller isolationและ diagnostic operational-health fieldsถูกเพิ่ม
- [x] locked restore, Release cross-build, core tests, formattingและ whitespace gatesผ่าน
- [x] Protocol V1, agent version 1.0.0 และ SQLite schema v4ไม่เปลี่ยน

### ยังไม่ผ่านและห้ามใช้ source inspectionแทน

- [ ] Windows Host testsรันผ่านทั้งหมดจาก clean Windows worktree
- [ ] dependency vulnerability scanสำเร็จด้วย 0 advisoriesและไม่มี incomplete project
- [ ] live replay disconnectก่อน/ระหว่าง/final sendผ่านโดย store healthไม่เปลี่ยน
- [ ] PBX UDP/TCP/auth/NAT/inbound/outbound/DTMF/missing-ACK matrixผ่าน
- [ ] DB/log/socket/audio fault matrixและ shutdownทุก scenario ≤10 วินาที
- [ ] replay 250/1,000/10,000 events ordered/completeและอยู่ใน memory/time budget
- [ ] idle/active/2-hour 50-call soakไม่มี unbounded resource trend
- [ ] privacy scanของ logs, diagnostic ZIP, metricsและ evidenceผ่าน
- [ ] release metadata/capability/artifact checksum parityผ่าน verifier
- [ ] approved security exceptionsมี owner, expiry/review date และ compensating controlsครบ
- [ ] rollout abort threshold, kill switchและbinary rollback rehearsalผ่าน

## Deliberately deferred evidence-driven work

ไม่เพิ่ม SQLite index, parallel connection, source-generated JSON, trimming, ReadyToRun หรือ single-file publish
เพราะ planกำหนดให้เปลี่ยนได้ต่อเมื่อ Windows profile/query-planยืนยัน bottleneck. การทำล่วงหน้าจะเพิ่ม migration,
compatibility และ rollback riskโดยไม่มีหลักฐาน. Component splitขนาดใหญ่ของ transport/media/audio/persistenceก็ยังไม่ทำ
ในรอบเดียวกับ behavior change; รอบนี้แยกเฉพาะ pure lifecycle policyและ task supervisionที่มี characterization testsแล้ว.

## Release decision

ยังไม่ประกาศ production ready. Source candidateพร้อมเข้าสู่ Windows/PBX/performance evidence phase.
หากพบ durable/control queue overflow, pumpไม่ running, duplicate dial, double terminal, event gap,
pending ageเกิน 300 วินาที, shutdownเกิน 10 วินาที หรือ PII leakแม้หนึ่งครั้ง ให้หยุด rolloutและหยุดเปิดสายใหม่
โดยไม่ลบ SQLite, clear outbox หรือ force ACK.
