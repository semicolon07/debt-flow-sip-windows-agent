# SIP Windows Agent Production Readiness Review

วันที่ตรวจ: 06/10/2026  
Baseline commit: `012e15e42cc4302972375100af19cec3e3849432`  
สถานะ: **Mandatory source hardening implemented 09/10/2026 — full DoD and external release gates pending**

## ข้อสรุปสำหรับการตัดสินใจ

SIP Windows Agent มีพื้นฐานด้าน durable event, recovery journal, protocol validation, TLS และ deterministic build
ที่ดี การปรับปรุงตาม review นี้ถูก implement ใน source แล้วเมื่อ 06/10/2026 โดยแก้ deadlock และ call-correlation
เป็นหลัก แต่ยังไม่ควรประกาศ production ready จนกว่า Windows/PBX/soak/privacy/release rehearsal จะมี evidence จริง.

รายการเดิมที่เป็น release blocker มีผลดังนี้:

1. inbound answer deadlock ปิดใน source และมี regression test ผ่าน
2. local WSS allow-all โดยไม่มี application-level client authentication ยังคงอยู่ตาม owner-approved exception

stale SIP callback, `async void` processing, payload-insensitive idempotency, journal growth, blocking logging/tray
และ release metadata drift ได้รับการแก้ใน source. SQLite/WAL ยังเก็บ PII แบบ plaintext ตาม decision ที่ยืนยันแล้ว;
จึงเพิ่ม per-user ACL, `secure_delete`, clean-shutdown WAL truncate และ read-only upgrade preflight แทน encryption.

Owner risk acceptance เดิมเรื่อง allow-all Origin, unsigned artifact และ rollout โดยไม่มี canary
ยังคงเป็น residual risk และไม่ทำให้รายการเหล่านี้ผ่านเกณฑ์ production-grade โดยอัตโนมัติ

## ขอบเขตการตรวจ

การตรวจครอบคลุมส่วนต่อไปนี้:

- coordinator serialization, cancellation, shutdown และ call state lifecycle
- SIPSorcery callback, registration, inbound/outbound call และ media lifecycle
- local WSS protocol, Origin policy, command dispatch และ ownership
- SQLite outbox, active-call journal, command journal, migration และ capacity policy
- tray lifecycle, diagnostics และ logging
- release packaging, dependency audit และ automated/manual acceptance gates

## ผลการตรวจสอบอัตโนมัติ

| รายการ | ผล |
| --- | --- |
| Release build | PASS, 0 warnings, 0 errors |
| Locked restore | PASS; ไม่มี `NU1510` |
| Core tests ณ implementation ล่าสุด | PASS, 92/92 |
| Inbound answer regression | PASS: connected callback ก่อน `AnswerAsync` return ไม่ deadlock |
| Locked restore พร้อม `NuGetAuditMode=all` | PASS โดยไม่มี vulnerability advisory warning |
| Windows host tests | COMPILE PASS; NOT RUN บน macOS เนื่องจากไม่มี Windows Desktop runtime |
| Cross-build ล่าสุด | PASS, 0 warnings, 0 errors (`net10.0-windows`, EnableWindowsTargeting) |

คำสั่ง `dotnet package list` สำหรับ Windows host โดยตรงบน macOS จบด้วย SDK error
`Sequence contains no matching element`; Windows verifier ต้องตรวจ dependency graph ซ้ำก่อน release
แม้ locked restore พร้อม audit จะผ่านแล้ว. ผล build/test ในเอกสารนี้ไม่แทน Windows/PBX acceptance.

## Implementation update — 06/10/2026

- coordinator แยก SIP/media I/O ออกจาก state actor และใช้ correlated `SipCallHandle` + generation
- runtime callbacks เข้าสู่ bounded channel, ไม่มี `async void`, stale signals ถูก discard และ overflow fail closed
- inbound accept/busy เป็น atomic, มี ring-timeout/missing-ACK path, PBX source IP validation และ strict RTP default
- destination เดียวกันถูก canonicalize ก่อน persist/dial; compatibility `acceptRtpFromAny` ต้อง opt in ชัดเจน
- command journal ใช้ canonical payload HMAC-SHA256 key ที่ DPAPI protect, recovery `executing → abandoned`, hard cap/TTL/prune
- async bounded JSON logger, tray/coordinator timeout, bounded shutdown และ tracked background task cleanup
- SQLite เปิด `secure_delete`, truncate WAL ตอน clean shutdown และจำกัด AppData ACL ให้ current user + SYSTEM
- `--storage-preflight` อ่าน SQLite แบบ read-only พร้อม exit code 0/20/21 โดยไม่เริ่ม TLS/UI/WSS/SIP
- `--print-release-metadata` เป็น source เดียวของ version/protocol/schema/capabilities สำหรับ welcome และ release manifest
- release workflow pin action SHA และสร้าง artifact name จาก semantic tag; binary metadata ต้องตรงก่อน package
- P5 D cache storage/audio state, batch ACK checkpointและ reconnect replay, ลด WebSocket/logging copies และเพิ่ม
  bounded non-PII performance metrics; Windows baseline/benchmarkยัง pending

ข้อยกเว้นที่ยืนยันและยังคงอยู่: unauthenticated allow-all WSS, unsigned ZIP, full-target rollout ไม่มี canary
และ plaintext SQLite/WAL. สถานะสุดท้ายจึงใช้คำว่า `released with approved exceptions` เท่านั้น.

## Comprehensive re-audit update — 09/10/2026

การตรวจ baseline `150241ee0f85` หลัง meticulous follow-up พบ source gaps ที่ต้องปิดก่อน production เพิ่มเติม:

- SIP signal pump หยุดอ่าน callback ทั้งหมดเมื่อ signal handling หนึ่งรายการโยน exception แต่ processยังรันต่อ
- WebSocket disconnect ระหว่าง replay บาง path ถูกจำแนกเป็น `outbox_unavailable` และ Agentไม่ recoverเอง
- multi-event call transition commitแยก transaction ทำให้ lifecycle half-commitได้
- background operations, cancellation taxonomy, WebSocket QoS และ operational healthยังต้องทำให้มี owner/policyครบ

confirmed scope, target architecture, implementation sequence, tests, performance budgets, migration/rollback
และ definition of done อยู่ที่
[`2026-10-09-sip-windows-agent-comprehensive-improvement-plan.md`](2026-10-09-sip-windows-agent-comprehensive-improvement-plan.md).
ข้อความ `Implementation complete` ใน historical phase resultหมายถึง scopeของ phaseนั้นเท่านั้น และไม่แทน
production-ready decision หลัง re-audit นี้.

### Implementation result — 09/10/2026

P0/P1 source blockersถูกแก้แล้วโดยเพิ่ม per-signal supervision, atomic multi-event call transition,
store/transport replay isolation, supervised background lifecycle และ WebSocket QoS lanes. Architecture hardening
เพิ่ม pure call lifecycle planner, prepared command definition, async command maintenance และ local operational-health
snapshotสำหรับ diagnostics. Logger/tray caller pathถูก isolateและ diagnosticsไม่ enumerate WinMMบน UI threadแล้ว.

ผลบน macOS cross-target environment: locked restore PASS, Release build 0 warnings/errors, core tests 92/92,
Host/Host.Tests compile PASS และ formatting/whitespace PASS. Windows Host execution, vulnerability scanner,
PBX/device/fault/performance/soak/privacy/release rehearsalยัง pending จึงยังไม่ production ready.

รายละเอียด authoritative อยู่ที่
[`2026-10-09-sip-agent-comprehensive-improvement-result.md`](plan-results/2026-10-09-sip-agent-comprehensive-improvement-result.md).

## Meticulous follow-up — 07/10/2026

การ recheck หลัง P5 D ปิด source gaps เพิ่มเติมโดยไม่เปลี่ยน protocol/schema/version:

- coordinator reserve audio preference/test ตลอด operation; outbound ได้ `audio_device_busy` และ inbound
  ตอบ unavailable แทนการชนกับ device I/O
- tray/snapshot อ่าน immutable audio inventory cache; Windows notification หรือ bounded 30-second fallback
  refresh นอก coordinator hot path
- playback cache มี positive/negative TTL, duplicate device ID ถูกแยกใน inventory เดียวกัน และ endpoint
  initialization retry/fallback เมื่ออุปกรณ์หายระหว่าง enumeration
- command-journal mutation ทำ cached DB+WAL size dirty ก่อน capacity health; oldest-pending ใช้เวลาที่เก่าจริง
  พร้อม canonical UTC persistence
- call start rollback in-memory state เมื่อ durable append พบ capacity/store failure ก่อน SIP side effect
- logger writer เก็บ pending line, retry 100 ms–5 s และเพิ่ม `sip_agent.logging.writer_failures`
- Windows sampler schema v2 เก็บ resource growth และเปิด `dotnet-counters` ได้ด้วย
  `-CollectRuntimeCounters`

รายละเอียด plan/result และ residual Windows gates อยู่ที่
[`2026-10-07-sip-agent-meticulous-hardening-result.md`](plan-results/2026-10-07-sip-agent-meticulous-hardening-result.md).

## ระดับความรุนแรง

| ระดับ | ความหมาย | การตัดสินใจ |
| --- | --- | --- |
| P0 | อาจหยุดบริการ เปิดสิทธิ์ควบคุม Agent หรือทำให้ call control ใช้งานไม่ได้ | ต้องแก้ก่อน release |
| P1 | อาจทำให้สายผิดพลาด ข้อมูลผูกผิดสาย สูญเสียความพร้อมใช้งาน หรือไม่ผ่าน production security/privacy | ต้องแก้ก่อน rollout วงกว้าง |
| P2 | Hardening ด้านปฏิบัติการ การบำรุงรักษา และ supply chain | ปิดก่อนประกาศ production complete หรือรับ residual risk อย่างชัดเจน |

## รายการความเสี่ยง

| ID | ระดับ | หัวข้อ | สถานะ |
| --- | --- | --- | --- |
| SIP-PRR-001 | P0 | Reentrant coordinator deadlock ตอน answer inbound call | Remediated in source; regression PASS |
| SIP-PRR-002 | P0 | Local WSS ไม่มี client authentication และ default allow-all Origin | Approved exception; residual risk open |
| SIP-PRR-003 | P1 | SIP signal ไม่มี call generation และ stale callback อาจกระทบสายใหม่ | Remediated in source; Windows race evidence pending |
| SIP-PRR-004 | P1 | SIPSorcery event handlers กลายเป็น `async void` และ lifecycle ordering ไม่ถูกควบคุม | Remediated in source; PBX evidence pending |
| SIP-PRR-005 | P1 | SIP และ RTP trust boundary เปิดเกินความจำเป็น | Source hardened; NAT/direct PBX matrix pending |
| SIP-PRR-006 | P1 | Command idempotency ไม่ผูก payload และ journal มี unbounded `executing` rows | Remediated in source |
| SIP-PRR-007 | P1 | Shutdown, tray และ logging สามารถค้างหรือรบกวน call control | Remediated in source; fault tests pending |
| SIP-PRR-008 | P1 | Full remote party อยู่ใน SQLite แบบ plaintext และ migration มี rollout trap | Plaintext approved; mitigations implemented; preflight rehearsal pending |
| SIP-PRR-009 | P1 | Real SIP runtime ไม่มี automated lifecycle coverage เพียงพอ | Open external release gate |
| SIP-PRR-010 | P2 | Release manifest, versioning และ artifact trust เริ่ม drift จาก runtime | Drift remediated; unsigned artifact approved exception |
| SIP-PRR-011 | P2 | Control-plane ทำ SQLite scan, audio probe และ replay wait ถี่เกินจำเป็น | Remediated in source; Windows benchmark pending |

| Follow-up ID | ระดับ | สถานะหลัง implementation 09/10/2026 |
| --- | --- | --- |
| SIP-NEXT-001 | P0 | Remediated; signal continuation regression PASS |
| SIP-NEXT-002 | P0 | Remediated in source; live replay disconnect matrix pending |
| SIP-NEXT-003 | P1 | Remediated; atomic rollback regression PASS |
| SIP-NEXT-004 | P1 | Remediated with shared task supervisor; Windows shutdown/runtime fault matrix pending |
| SIP-NEXT-005 | P1 | Remediated in runtime/audio wrappers; shutdown-race matrix pending |
| SIP-NEXT-006 | P1 | Remediated with QoS lanes; saturation test compile PASS, Windows execution pending |
| SIP-NEXT-007 | P2 | Prepared command definition consolidated; Host architecture/idempotency execution pending on Windows |
| SIP-NEXT-008 | P2 | Prune removed from request path; retry/metric implemented |
| SIP-NEXT-009 | P2 | Logger/tray isolation implemented; Windows disk/UI fault evidence pending |
| SIP-NEXT-010 | P2 | Partially remediated: lifecycle planner/task supervisor extracted; large runtime/store split deferred behind characterization |
| SIP-NEXT-011 | P2 | Coordinator/WebSocket health snapshot and bounded metrics implemented; operational evidence pending |
| SIP-NEXT-012 | P2 | Mandatory batching/prune/UI optimization implemented; Windows profiling/query-plan evidence pending |

รายละเอียด “ผลกระทบ/การแก้ไขที่ต้องการ” ด้านล่างบันทึก finding ณ baseline commit; ตารางสถานะและ
Implementation update ด้านบนเป็น authority ของผลหลังแก้ไข.

## SIP PRR 001 Reentrant coordinator deadlock ตอนรับสาย

### ผลกระทบ

เมื่อ Portal ส่งคำสั่ง answer coordinator เปลี่ยน call state เป็น `answering` แล้วรอ `SipRuntime.AnswerAsync` ภายใน
single-reader queue ขณะเดียวกัน runtime ส่ง `CallConnected` หรือ `CallFailed` กลับมาผ่าน handler ที่รอ enqueue
เข้า queue เดิม ทำให้ทั้งสองฝั่งรอกันถาวร

ผลคือสายค้างที่ `answering`, command ถัดไปไม่ทำงาน, tray snapshot ไม่ตอบ และ Exit อาจค้างตามไปด้วย
กลไกเดียวกันต้องถูกตรวจใน configure, register, unregister, reject, hangup, DTMF, audio control และ shutdown
เพราะหลาย operation รอ external I/O ขณะถือ coordinator queue

### หลักฐานใน source

- [`AgentCoordinator.AnswerCoreAsync`](../src/DebtFlow.SipAgent.Application/AgentCoordinator.cs#L367) รอ runtime ขณะทำงานใน queue
- [`SipRuntime.AnswerAsync`](../Host/SipRuntime.cs#L197) ส่ง terminal/connected signal ก่อนคืน control
- [`AgentCoordinator.OnSipSignalAsync`](../src/DebtFlow.SipAgent.Application/AgentCoordinator.cs#L636) รอ queue เดียวกัน
- [`AgentCoordinator.DisposeAsync`](../src/DebtFlow.SipAgent.Application/AgentCoordinator.cs#L142) ยังรอ processor หลัง shutdown timeout

Regression harness ที่ให้ runtime ส่ง `CallConnected` ก่อน `AnswerAsync` คืนค่า reproduce timeout ได้

### การแก้ไขที่ต้องการ

- จำกัด coordinator queue ให้ทำเฉพาะ state transition, persistence และ operation intent
- ทำ SIP/network/media I/O นอก queue
- ส่งผลกลับด้วย `operationId` และ `callGeneration`
- discard completion ที่หมดอายุหรือไม่ตรงกับ call ปัจจุบัน
- แยก timeout ของ queue wait, operation execution และ shutdown
- หลีกเลี่ยงการแก้ด้วย fire-and-forget ที่ไม่มี ownership เพราะจะเปลี่ยน deadlock เป็น race condition

### เกณฑ์ยอมรับ

- inbound answer regression ผ่านโดย command และ callback เสร็จภายใน timeout
- callback แบบ synchronous, delayed และ duplicated ไม่ทำให้ queue ค้าง
- shutdown ระหว่าง answer/register/media cleanup เสร็จภายใน 10 วินาที
- snapshot และ diagnostics ยังตอบได้เมื่อ SIP operation timeout

## SIP PRR 002 Local WSS ไม่มี client authentication

### ผลกระทบ

ค่าเริ่มต้นใน [`agentsettings.example.json`](../agentsettings.example.json#L3) เปิด `isAllowAllOrigins=true`
และ [`LocalWebSocketServer`](../Host/LocalWebSocketServer.cs#L42) ใช้ Origin เป็น control หลักโดยไม่มี session assertion
หรือ pairing secret

เว็บไซต์ที่ผู้ใช้เปิดสามารถเชื่อม localhost WSS, แย่ง controller slot, configure SIP, โทรหรือรับสาย,
วางสาย, ควบคุมเสียง และ ACK durable events ได้ Native process สามารถปลอม Origin ได้แม้เปลี่ยนเป็น exact allowlist

### การแก้ไขที่ต้องการ

- เปลี่ยน default เป็น fail closed และกำหนด exact production origins
- ให้ backend ออก short-lived signed assertion ผูกกับ user, collection, agent instance, origin, expiry และ nonce
- ใช้ challenge-response และ replay protection ระหว่าง WebSocket handshake
- ตรวจ authorization ซ้ำเมื่อ configure collection binding หรือเปลี่ยน owner session
- rate-limit authentication failure และไม่บันทึก token/Origin ลง log

### เกณฑ์ยอมรับ

- unknown website และ null/spoofed Origin ควบคุม Agent ไม่ได้
- token หมดอายุ, replay, wrong collection, wrong agent และ wrong user ถูกปฏิเสธ
- valid Portal session reconnect และ replay durable events ได้ตามเดิม
- security test ต้องทำทั้ง Edge, Chrome และ native WebSocket client

## SIP PRR 003 SIP signal ไม่มี call generation

### ผลกระทบ

[`ApplySipSignalCoreAsync`](../src/DebtFlow.SipAgent.Application/AgentCoordinator.cs#L639) นำ signal ไปใช้กับ `_call`
ปัจจุบันโดยไม่มี call identity การมาถึงล่าช้าของ `CallFailed`, `CallRemoteEnded`, `IncomingCancelled`,
`MediaReady` หรือ DTMF จากสายเก่าจึงอาจจบหรือแก้ event ของสายใหม่

Inbound handling ยังตรวจ busy และกำหนด `_pendingIncomingCall` โดยไม่ atomic และไม่ครอบคลุม dialing/ringing
ทุกสถานะ เมื่อ coordinator พบสายที่ active แล้ว
[`StartIncomingCallCoreAsync`](../src/DebtFlow.SipAgent.Application/AgentCoordinator.cs#L710) จะ return โดยไม่สั่ง runtime
reject INVITE ที่รับไว้แล้ว

### การแก้ไขที่ต้องการ

- สร้าง immutable runtime call context ต่อสายพร้อม generation และ opaque SIP handle
- ใส่ generation ในทุก SIP/media signal
- serialize pending inbound accept/reject/cancel ภายใน runtime actor หรือ lock เดียว
- reject INVITE ใหม่อย่าง explicit เมื่อ dialing, ringing, answering, connected หรือ ending
- clear pending state เฉพาะเมื่อ callback ตรงกับ call handle เดิม

### เกณฑ์ยอมรับ

- delayed terminal callback จากสายก่อนหน้าไม่กระทบสายใหม่
- simultaneous INVITE ได้หนึ่ง accepted call และรายการอื่นตอบ Busy
- incoming ระหว่าง outbound dialing ไม่ทิ้ง pending call
- cancel/ring-timeout ของ INVITE เก่าไม่ยกเลิก INVITE ใหม่

## SIP PRR 004 SIPSorcery callbacks เป็น async void

### ผลกระทบ

Handlers ใน [`SipRuntime.WireUserAgentEvents`](../Host/SipRuntime.cs#L348) ใช้ async lambda กับ event delegates
ของ SIPSorcery 10.0.16 ที่คืน `void` จึง compile เป็น `async void` รวมถึง registration callbacks

Library ไม่สามารถรอ handler ให้เสร็จ ลำดับ callback ไม่ถูกควบคุม และ exception จาก media cleanup หรือ logic
ก่อนเข้า `EmitAsync` สามารถกลายเป็น unhandled exception ได้ นอกจากนี้ runtime ยังไม่ได้ subscribe
`ServerCallRingTimeout`

### การแก้ไขที่ต้องการ

- ให้ library callbacks ทำงานแบบ synchronous และ bounded โดย `TryWrite` เข้า internal channel
- ใช้ tracked worker ประมวลผล event ตามลำดับพร้อม top-level exception boundary
- เพิ่ม handler สำหรับ server ring timeout, missing ACK และ network-loss reconciliation
- หยุด worker และ drain/cancel callbacks แบบมี timeoutตอน dispose

### เกณฑ์ยอมรับ

- ไม่มี `async void` callback ใน SIP และ registration lifecycle
- exception ทุกชนิดถูก log แบบ redacted และไม่ crash process
- duplicated/out-of-order callbacks ให้ผล deterministic
- inbound 200 OK ที่ไม่ได้ ACK ถูก cleanup และสร้าง terminal outcome เพียงครั้งเดียว

## SIP PRR 005 SIP และ RTP trust boundary

### ผลกระทบ

[`SipEndpointFormatter.FormatDestination`](../Host/SipEndpointFormatter.cs#L23) นำ raw destination ไปต่อเป็น SIP URI
โดยตรง Transport listen ทุก interface และ incoming INVITE ไม่มี allowlist check ว่ามาจาก configured PBX/account
ส่วน media session ตั้ง [`AcceptRtpFromAny=true`](../Host/SipRuntime.cs#L434)

ค่าที่บันทึกถูก normalize แต่ค่าที่ส่งจริงยังอาจมี SIP URI parameter หรือ control character ที่ไม่ต้องการ
และ host บน network ที่ไม่ถูกควบคุมอาจรับ SIP/RTP จาก peer อื่นได้

### การแก้ไขที่ต้องการ

- canonicalize destination ก่อนทั้ง persist และ dial จากค่าเดียวกัน
- อนุญาตเฉพาะ dialable character/extension grammar ที่กำหนด
- validate Request-URI, account และ remote endpoint ของ initial INVITE
- กำหนด Windows Firewall guidance และ network exposure policy
- ให้ symmetric RTP เป็น explicit PBX compatibility mode พร้อม source pinning หลัง packet แรกที่ผ่าน validation

### เกณฑ์ยอมรับ

- malformed URI, CR/LF, header/parameter injection และ overlong destination ถูกปฏิเสธก่อน SIP call
- INVITE จาก peer ที่ไม่อนุญาตถูก reject และไม่สร้าง call journal
- direct และ NAT PBX matrix ผ่านเมื่อใช้ policy ที่เข้มที่สุดที่รองรับ
- RTP spoof/source-switch test ไม่สามารถ inject media หลัง source ถูก pin

## SIP PRR 006 Command idempotency และ journal growth

### ผลกระทบ

[`ComputeCommandIdentityHash`](../src/DebtFlow.SipAgent.Protocol/ProtocolCodec.cs#L114) hash เฉพาะ command type,
command ID และ call ID แต่ไม่รวม destination, DTMF, mute, volume หรือ configure payload
Command ID เดิมที่มากับ payload คนละค่าจึงคืน cached result แทน `command_duplicate_conflict`

Dispatcher บันทึก command เป็น `executing` ก่อนเริ่มงานที่
[`V1CommandDispatcher`](../Host/V1CommandDispatcher.cs#L107) แต่ pruning ไม่ลบ `executing` rows ที่
[`SqliteAgentEventStore.PruneCommandsAsync`](../src/DebtFlow.SipAgent.Persistence/SqliteAgentEventStore.cs#L369)
Crash หรือ cancellation จึงทิ้งแถวถาวรและอาจทำให้ database ถึง capacity limit

### การแก้ไขที่ต้องการ

- สร้าง canonical semantic digest จาก payload ทั้งหมด
- ใช้ keyed HMAC สำหรับ credential และค่า low-entropy โดยเก็บ key ด้วย DPAPI
- validate command type และ payload ให้ครบก่อนสร้าง journal row
- เพิ่ม state สำหรับ abandoned/reconciled execution พร้อม startup recovery
- เพิ่ม hard cap, TTL, periodic pruning และ metric สำหรับ command journal

### เกณฑ์ยอมรับ

- command ID เดิมกับ payload ต่างกันคืน `command_duplicate_conflict`
- replay payload เดิมให้ผลเดิมโดยไม่ execute ซ้ำ
- restart ระหว่าง executing command ได้ผล deterministic และไม่ค้างตลอดไป
- command flood ไม่ทำให้ durable call-event reserve ถูกใช้หมด

## SIP PRR 007 Shutdown tray และ logging resilience

### ผลกระทบ

[`TrayApplicationContext`](../Host/TrayApplicationContext.cs#L155) ขอ snapshot ด้วย `CancellationToken.None`
ทั้ง status refresh และ Exit เมื่อ coordinator ค้าง ผู้ใช้จึงไม่สามารถปิด Agent ผ่าน tray ได้

[`SafeJsonLoggerProvider`](../Host/SafeJsonLoggerProvider.cs#L80) เขียนและ flush ด้วย `WriteThrough`
แบบ synchronous ทุกบรรทัดภายใต้ lock Disk ที่ช้า เต็ม หรือถูกปฏิเสธสิทธิ์จึงสามารถเพิ่ม latency หรือโยน exception
เข้า call-control path

### การแก้ไขที่ต้องการ

- ใช้ bounded timeout สำหรับ tray, diagnostics, certificate maintenance และ Exit
- เพิ่ม out-of-band health heartbeat กับ forced runtime teardown
- ทำ shutdown ให้ cancel processor/runtime ก่อนรอ task และมี hard upper bound
- เปลี่ยน logger เป็น bounded asynchronous writer พร้อม drop counter และ non-throwing fallback
- ทำ disk-full/read-only/slow-I/O fault injection

### เกณฑ์ยอมรับ

- tray ยัง responsive เมื่อ coordinator หรือ SIP runtime ค้าง
- Exit จบภายใน 10 วินาทีหรือเข้าสู่ controlled force-stop path
- logging failure ไม่เปลี่ยน call state และไม่ crash process
- shutdown ระหว่าง callbacks ไม่สร้าง double terminal event

## SIP PRR 008 PII protection และ migration safety

### ผลกระทบ

Full remote party ถูกเก็บใน durable event JSON และ
[`ActiveCallJournal.RemoteParty`](../src/DebtFlow.SipAgent.Persistence/SqliteAgentEventStore.cs#L882)
แบบ plaintext ข้อมูลอาจคงอยู่ใน database, WAL หรือ free pages หลัง ACK

Migration guard ที่
[`EnsureCollectionBindingMigrationSafeAsync`](../src/DebtFlow.SipAgent.Persistence/SqliteAgentEventStore.cs#L655)
ปฏิเสธการ migrate เมื่อมี durable event หรือ active call journal เครื่องที่อัปเกรดระหว่างมี backlog
จึงอาจเข้า degraded state จนกว่าจะมีการดำเนินการด้วยมือ

### การแก้ไขที่ต้องการ

- ใช้ DPAPI envelope encryption สำหรับ remote party และ sensitive event fields
- กำหนด retention, secure deletion limitation และ diagnostic export boundary
- เพิ่ม migration preflight, drain/quarantine path และ rollback-safe tooling
- แยก capacity ของ durable events ออกจาก command journal และ telemetry ที่ไม่ critical

### เกณฑ์ยอมรับ

- raw number อ่านจาก SQLite/WAL โดยตรงไม่ได้
- upgrade ที่มี pending outbox ให้ผล deterministic และไม่ลบ event
- rollback/recovery ไม่ต้อง purge AppData โดยปริยาย
- privacy review ยืนยันว่า log, diagnostics, metrics และ crash data ไม่มี raw PII

## SIP PRR 009 Real SIP runtime test coverage

### ผลกระทบ

Core tests ครอบคลุม reducers, protocol, coordinator และ SQLite ได้ดี แต่ host tests ใช้ fake runtime เป็นหลัก
จึงไม่ตรวจ behavior จริงของ SIPSorcery event ordering, pending inbound state, missing ACK, media cleanup,
network loss และ simultaneous callbacks

ปัญหา inbound answer deadlock ผ่านชุดทดสอบเดิมได้เพราะ fake runtime ไม่ส่ง reentrant signal ระหว่าง operation

### การแก้ไขที่ต้องการ

- เพิ่ม contract harness ที่จำลอง callback ระหว่าง runtime method ก่อน method คืนค่า
- เพิ่ม controllable SIP runtime adapter และ local SIP peer integration tests บน Windows
- เพิ่ม race, chaos และ soak scenarios ลง Windows verifier
- เก็บ TRX, agent checksum, OS/PBX version และ redacted evidence ต่อ release

### เกณฑ์ยอมรับ

- ครอบคลุม reentrant, duplicate, delayed และ stale callbacks
- ครอบคลุม simultaneous INVITE, outbound/inbound crossover และ server ring timeout
- ครอบคลุม disk full, socket loss, device removal และ shutdown in flight
- Windows/PBX soak อย่างน้อย 2 ชั่วโมงและ 50 calls ไม่มี duplicate dial, double terminal, sequence gap
  หรือ resource growth แบบไม่จำกัด

## SIP PRR 010 Release contract และ artifact trust

### ผลกระทบ

[`verify-p5-release.ps1`](../scripts/verify-p5-release.ps1#L8) hard-code version และ tag เป็น `1.0.0/v1.0.0`
ทำให้ release ถัดไปต้องแก้ script และเสี่ยง contract drift Manifest ที่
[`capabilities`](../scripts/verify-p5-release.ps1#L94) ยังไม่รวม `call.mute`, `audio.output.volume` และ
`audio.input.volume` ที่ runtime ประกาศ

Artifact ระบุ `authenticodeSigned=false` SHA-256 ที่ส่งพร้อม ZIP ตรวจความครบถ้วนได้แต่ไม่ยืนยัน publisher
และ GitHub Actions ใช้ mutable major tags

### การแก้ไขที่ต้องการ

- derive SemVer จาก exact signed tag และตรวจให้ตรง Assembly/File/Informational version
- generate capability manifest จาก source of truth เดียวกับ welcome payload
- คง unsigned artifact ตาม approved exception และบังคับ checksum/SBOM/provenance evidence
- pin Actions ด้วย commit SHA และเก็บ provenance/SBOM/checksum เป็น immutable release evidence
- ทำ full-target rollout หลัง staging ตาม approved no-canary exception พร้อม kill switch

### เกณฑ์ยอมรับ

- release tag ใหม่ไม่ต้องแก้ hard-coded version ใน script
- runtime welcome, documentation และ release manifest มี capability set ตรงกัน
- owner ลงนามยอมรับ unsigned publisher risk และ support ตรวจ checksum ก่อนส่ง/ติดตั้ง
- compatibility matrix และ disable+exit rollback ผ่านการทดสอบก่อน rollout

## จุดแข็งที่ควรรักษาไว้

- durable call event ถูก persist ก่อน publish และ replay ตาม sequence
- active-call journal กับ call event update ใช้ transaction และมี startup reconciliation
- SQLite ใช้ WAL, synchronous FULL, integrity checks และ capacity thresholds
- protocol parser ปฏิเสธ unknown fields, malformed UUID และ oversized/rate-limited traffic
- TLS certificate มี SAN, EKU, key-usage และ non-exportable key validation
- build ใช้ warnings as errors, deterministic build และ locked dependency files
- sensitive SIP credential ไม่ถูกเขียนลง agentsettings หรือ safe diagnostic bundle

การแก้ architecture ต้องรักษาคุณสมบัติเหล่านี้ โดยเฉพาะ event ordering, exactly-once command behavior,
terminal event เพียงหนึ่งรายการต่อ call และ outbox recovery หลัง restart

## ลำดับการดำเนินงาน

### ระยะที่หนึ่ง ปิด release blockers

1. [x] แยก external SIP operation ออกจาก coordinator queue
2. [x] เพิ่ม call handle/generation พร้อม regression tests
3. [exception] คง unauthenticated allow-all WSS ตาม owner decision
4. [x] เพิ่ม bounded shutdown/tray path; external health evidenceยังเป็น release gate

### ระยะที่สอง ปิด call correctness และ availability

1. [x] สร้าง runtime event channel แทน `async void` processing
2. [x] ทำ pending inbound state ให้ atomic และ handle ring timeout/missing ACK
3. [x] canonicalize destination, validate PBX peer และ strict RTP default
4. [x] แก้ command payload HMAC, recovery state และ journal limits
5. [x] ทำ logging/tray timeout ให้ bounded; disk-full fault evidenceยัง pending

### ระยะที่สาม ปิด privacy และ release engineering

1. [exception] คง plaintext SQLite/WAL; ใช้ ACL + secure-delete + WAL truncate + storage preflight
2. [ ] เพิ่ม real SIP integration, race, chaos และ soak tests บน Windows/PBX
3. [x] sync capabilities/version/schema จาก binary source เดียว
4. [x] pin CI dependencies; unsigned/full-target ยังคงเป็น approved exceptions

## Production release gates

ห้ามเปลี่ยนสถานะเป็น production ready จนกว่ารายการต่อไปนี้จะผ่าน:

- [x] SIP-PRR-001 ปิดใน sourceพร้อม inbound answer regression
- [ ] SIP-PRR-002 มี owner sign-off/evidence ยอมรับ unauthenticated allow-all control risk
- [x] ทุก call-scoped SIP signal มี call generation และ stale terminal regression ผ่าน
- [x] ไม่มี `async void` ใน SIP/registration event processing
- [ ] command payload conflict test รันผ่านบน Windows; crash recovery core testผ่านแล้ว
- [ ] unknown-origin/native-client acceptance testยืนยัน behaviorตาม allow-all exceptionและบันทึก security sign-off
- [ ] Windows host tests ผ่านทั้งหมดจาก clean worktree
- [ ] PBX matrix ครอบคลุม UDP, TCP, auth failure, NAT RTP, inbound/outbound, DTMF และ missing ACK
- [ ] shutdown, disk-full, socket-loss และ device-removal fault tests ผ่าน
- [ ] 2-hour/50-call soak ไม่มี duplicate dial, double terminal, event gap, PII leak หรือ unbounded growth
- [ ] migration preflight/readback/rollback evidence ผ่านโดยไม่สูญเสีย outbox
- [ ] release verifierบน Windowsยืนยัน runtime welcome/binary metadata/manifest/documentation capabilities ตรงกัน
- [ ] production artifact ผ่าน publisher verification หรือมี owner-approved residual risk ที่ระบุผลกระทบชัดเจน
- [ ] rollout และ kill-switch runbook ผ่าน rehearsal

## Kill switch conditions

Operations ต้องหยุดการเปิดสายใหม่และปิด Phone ของ Collection ที่ได้รับผลกระทบทันทีเมื่อพบเหตุใดเหตุหนึ่ง:

- duplicate dial, double terminal, event sequence gap หรือ cross-call/cross-Collection linkage แม้หนึ่งครั้ง
- Agent ค้างใน `answering`, `ending` หรือ shutdown เกิน operational timeout
- client ฝ่าฝืน one-owner/protocol/rate/query-string boundary หรือขยายผลเกิน allow-all exception ที่ลงนามไว้
- raw credential หรือหมายเลขโทรศัพท์ปรากฏใน log, diagnostics, metrics หรือ crash artifact
- pending event เกิน 300 วินาทีหรือ outbox อยู่สถานะ critical/full ตาม rollout threshold
- restart/forced termination มีอัตราสูงกว่าค่าที่ operations กำหนด

Rollback ต้องไม่ clear SQLite, force ACK, downgrade schema หรือฆ่า active call โดยปริยาย
ต้องหยุดสายใหม่ รอหรือ reconcile สายปัจจุบัน เก็บ AppData/outbox และรักษาหลักฐานที่ redacted สำหรับวิเคราะห์เหตุการณ์
