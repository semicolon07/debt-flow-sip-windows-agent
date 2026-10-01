# P1 production agent core implementation result

วันที่: 01/10/2026

สถานะ: **P1 complete by owner acceptance — superseded by P2 source implementation**

Acceptance record: Owner ยืนยันเมื่อ 01/10/2026 ว่า Agent ทำงานปกติบน Windows กับ PBX Sandbox
และปิด P1. Source/cross-platform evidence ด้านล่างคงไว้ครบ; granular Windows case/TRX,
environment matrix และ redacted screenshots ที่ไม่ได้ส่งเข้า repository จะไม่ถูกสมมติขึ้น.

## Implemented

- WinForms per-user tray app บน Generic Host พร้อมไทย/อังกฤษตาม Windows UI culture
- auto start ผ่าน HKCU Run, user toggle, active-call exit confirmation และ bounded graceful shutdown
- startup preference reconcile HKCU Run กับ executable path ปัจจุบันทุก launch โดยไม่เปิดค่าที่ผู้ใช้ปิดไว้
- one instance ต่อ Windows user ด้วย global named mutex + current-user named-pipe activation
- Kestrel bind IPv4/IPv6 loopback port 8443 แทน HttpListener โดยไม่ใช้ URL ACL
- production exact Origin config จาก `%LOCALAPPDATA%\DebtFlow\SipAgent\agentsettings.json`
  ซึ่งอยู่ใน per-user storage เดียวกับ SQLite/Logs; dev Origins/override จำกัด `--console`
- malformed production Origin config ล้าง allowlist ทั้งชุดและ WebSocket ตอบ 503 แบบ fail closed
- reject query string ทุกชนิดก่อน WebSocket upgrade เพื่อกัน credential ใน URL
- heartbeat `session.ping` ใช้ strict empty payload และ reject unknown field
- protocol error หลัง parse envelope สำเร็จคืน `correlationMessageId` ของ message เดิม
- one controlling client, 64 KiB frame, heartbeat/timeout และ 60 messages ต่อ 10 วินาที
- bounded single-reader coordinator channel สำหรับ command, SIP signal, owner lease และ shutdown
- request cancellation หลัง dequeue ไม่ตัด state transition กลางทาง; shutdown timeout ยัง cancel execution ได้
- invalid registration/answer/reject command state ถูก reject ก่อนเรียก SIP adapter side effect
- validate SIP host ซ้ำที่ Agent เป็น IPv4/IPv6 literal และ format IPv6 registrar แบบ bracketed URI
- 60-second Portal disconnect lease; active call continuation และ delayed credential cleanup
- deterministic lease-expiration test seam ทำให้ reconnect/active-call race tests รอ background work จริง
  โดยไม่พึ่ง arbitrary timing delay
- inbound call ขณะไม่มี Portal ตอบ SIP 480 และสร้าง safe durable `portal_unavailable` lifecycle
- single outbound WebSocket writer และ disconnect slow client เมื่อ queue เต็ม
- handshake buffer บังคับลำดับ `welcome → snapshot → replay → live` พร้อม sequence filter กัน replay race
- `welcome.agentVersion` ใช้ SemVer core สามส่วนสำหรับ Portal compatibility negotiation
- replay รอ socket delivery เพื่อใช้ bounded backpressure แทนการ overflow ซ้ำเมื่อ backlog เกิน queue
- JSONL logger 10 MB/7 files พร้อม structured property/text redaction
- TH/EN tray resources ครอบคลุมทุก agent/registration/call wire state รวม dialing/retrying/unregistering
- outbox failure fail closed โดยไม่สร้าง phantom call และ shutdown ยัง hang up/unregister แบบ best effort
- processed-command fingerprint ใช้เฉพาะ type/commandId/optional callId โดยไม่ hash credential,
  destination, context token หรือ DTMF ซึ่งอาจกลายเป็น offline guessing oracle
- self-contained multi-file `win-x64` publish และ CI ZIP artifact
- reproducible Windows verifier บังคับ clean Git tree, scan vulnerability ทั้ง solution และสร้าง
  TRX, ZIP SHA-256 กับ evidence JSON

## Automated evidence from macOS arm64

| Check | Result |
| --- | --- |
| locked dependency restore | Passed |
| Release Windows cross-build | Passed, 0 warnings/errors |
| Roslyn analyzer verification | Passed across host/core/test projects; 0/57 files changed |
| cross-platform core tests | Passed, 46/46 |
| Windows host test project compile | Passed, 21 test cases discovered by source inventory |
| self-contained `win-x64` publish | Passed |
| multi-file ZIP integrity | Passed; `/tmp/debt-flow-sip-agent-p1-win-x64-20261001-final-v13.zip`, 76,615,806 bytes, 461 entries; SHA-256 `5208462cf67c2bc986d7090fe2678e6a3f12b05421e6155b752f5d5b29fdaeff` |
| NuGet vulnerability scan | Passed; no vulnerable packagesจาก configured source |
| diagnostic JavaScript parse / diff check | Passed |
| Windows verifier syntax/TRX evidence parser | Passed with PowerShell 7.6.6; full script execution still requires Windows |

Windows host testsมี config/redaction และ live Kestrel handshake/welcome/snapshot,
typed second-client rejection, rate limit และ 64 KiB message-size boundary cases แต่ environment นี้ไม่มี
`Microsoft.WindowsDesktop.App` runtime จึงยังไม่ได้ execute test assembly

## Requirement-by-requirement source audit before owner acceptance

ตารางนี้เป็น audit snapshot ก่อน owner acceptance และคง wording เดิมไว้เพื่อเห็นช่องว่างของ
repository evidence. Owner acceptance ด้านบนเป็น phase-closure authority; ช่อง evidence ที่ไม่ได้แนบ
จะถูกนำกลับมาเป็น regression target ใน P2/P5 โดยไม่แต่งผลย้อนหลัง.

| Confirmed P1 requirement | Current evidence | Audit status | Evidence still required |
| --- | --- | --- | --- |
| WinForms/Generic Host per-user tray | composition root + tray source cross-build | Source proved | standard-user Windows launch/tray QA |
| HKCU Run default/toggle/reconciliation | startup manager + 4 host cases compile | Source proved | sign-out/in และ path-update QA บน Windows |
| one instance per user + activation | SID-derived mutex/current-user pipe source | Source proved | second-process/cross-session Windows QA |
| active-call exit + bounded graceful shutdown | tray confirmation + coordinator shutdown tests | Source proved | interactive exit ระหว่างสายจริง |
| IPv4/IPv6 loopback Kestrel ไม่ใช้ URL ACL | `ListenLocalhost` + endpoint source | Source proved | non-admin bind/port-conflict QA |
| exact production Origin + fail closed | strict options tests และ pre-upgrade 503/403 source | Source proved | execute host testsบน Windows |
| one owner/query/64 KiB/rate/heartbeat bounds | live Kestrel host cases compile | Source proved | execute host testsและ 15/45-second observationบน Windows |
| typed V1 handshake/capability/snapshot/error | codec/core tests + live host handshake compile | Source proved | execute Windows host suite |
| bounded single-reader state coordinator | serialized race/invalid/terminal core tests | Proved, 46/46 core suite | PBX callback timing smoke |
| cancellation หลัง dequeue + shutdown cancellation | deterministic cancellation/shutdown tests | Proved, 46/46 core suite | Windows shutdown observation |
| single writer `welcome → snapshot → replay → live` | publisher ordering/backpressure host cases compile | Source proved | execute host suite + reconnect capture |
| SIP callbacks เข้า internal signalsเท่านั้น | adapter/coordinator dependency inspection + compile | Source proved | real PBX callback matrix |
| 60-second lease + active call continuation | deterministic expiry/reconnect/terminal core tests | Proved, 46/46 core suite | real call disconnect/reconnect smoke |
| inbound ไม่มี Portal ตอบ 480 + durable end | fake-runtime lifecycle test + adapter source | Source proved | PBX verifies actual SIP 480 |
| TH/EN tray status | resource coverage host cases compile | Source proved | TH/EN Windows visual QA |
| rolling JSONL + centralized redaction | logger/redaction source + host case compile | Source proved | Windows rotation + log/SQLite privacy scan |
| self-contained multi-file `win-x64` ZIP | publish + 461-entry integrity/SHA-256 | Source proved | Windows-native verifier evidence JSON |
| SIP/audio continuity | adapter cross-build + fake lifecycle tests | Source proved | PBX, microphone/speaker และ DTMF matrix |

## Regression evidence carried into P2/P5

- execute Windows host tests บน Windows CI/session
- tray/status/localization/startup/single-instance/exit QA แบบ non-admin
- P0+P1 combined real-PBX smoke สำหรับ registration, inbound/outbound, DTMF และ audio
- disconnect/reconnect, active-call preservation, replay/ACK และ safe log/outbox inspection
- บันทึกทุก case ID/environment/artifact hash และ redacted evidence ด้วย
  [Windows/PBX result template](../manual-verification/p1-windows-pbx-result-template.md)

P1 ถูกปิดโดย owner acceptance แล้วเมื่อ 01/10/2026. รายการข้างต้นไม่ reopen P1 แต่ยังเป็น
หลักฐานที่ควรเก็บให้ครบก่อน signed installer และ production rollout ใน P5.
