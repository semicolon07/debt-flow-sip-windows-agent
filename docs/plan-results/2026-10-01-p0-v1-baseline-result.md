# P0 V1 baseline implementation result

วันที่: 01/10/2026

สถานะ: **Source implementation complete — Windows/PBX smoke pending**

## Implemented

- แยก Protocol, Application state/reducers, SQLite Persistence และ Windows Host boundaries
- hard cutover จาก legacy action/eventType messages เป็น WebSocket V1 ที่ `/agent/v1`
- strict envelope/payload JSON, UUID identifiers, 64 KiB limit, hello/welcome/snapshot,
  command results, realtime/durable events, replay, ACK และ heartbeat
- exact development Origin allowlist และ one controlling client
- pure registration/call reducers พร้อม `ended + outcome` และ duplicate/late terminal suppression
- SIP UDP adapter รักษา registration, outbound/inbound, answer/reject/hangup และ DTMF
- SQLite persistent agent identity, monotonic sequence, outbox, acknowledgement watermark
  และ processed-command journal
- write durable call event ก่อน WebSocket publish; unavailable store ทำ Agent degraded
  และ block registration/call
- ตัด hardcoded PBX credential, raw SDP, raw SIP errors, raw destination และ DTMF digit
- V1 diagnostic page ไม่มี credential defaults, เปิดผ่าน localhost origin และ ACK simulation default off
- locked dependency graph, GitHub Actions core/Windows jobs และ patched SQLite native bundle

## Automated verification

รันจาก macOS arm64 ด้วย .NET SDK 10.0.401; Windows target build ใช้
`EnableWindowsTargeting=true`

| Check | Result |
| --- | --- |
| `dotnet restore softphone-native-client.sln -p:EnableWindowsTargeting=true --locked-mode --no-cache` | Passed |
| `dotnet build softphone-native-client.sln -c Release --no-restore -p:EnableWindowsTargeting=true` | Passed, 0 warnings/errors |
| `dotnet test tests/DebtFlow.SipAgent.Core.Tests/DebtFlow.SipAgent.Core.Tests.csproj -c Release --no-build` | Passed, 21/21 |
| NuGet vulnerable packages scan | Passed, no vulnerable packages |
| `dotnet format whitespace ... --verify-no-changes --no-restore` | Exit 0; workspace-load warning only |
| Diagnostic JavaScript parse | Passed |
| `git diff --check` | Passed |

Restore แรกตรวจพบ `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 advisory
`GHSA-2m69-gcr7-jv3q`; implementation pin `SQLitePCLRaw.bundle_e_sqlite3` และ
`SQLitePCLRaw.lib.e_sqlite3` 2.1.13 แล้ว vulnerability scan ผ่าน

## Covered tests

- protocol valid/invalid/version/unknown-field/oversize contracts
- exact Origin allowlist
- outbound state transitions, duplicate connected และ single terminal outcome
- remote-party masking
- persistent identity/sequence/reopen/replay/ACK
- processed-command upsert baseline
- registration gate ก่อนโทร
- duplicate terminal callback suppression
- destination และ DTMF redactionจาก durable payload

## Pending P0 completion gate

ยังไม่ได้รันบน Windows/PBX จริงจาก environment นี้ จึงยังไม่ claim ว่า P0 complete:

- Windows 10 1809+/Windows 11 runtime
- register/unregister กับ PBX test account
- outbound answer/cancel/local/remote hangup และ DTMF
- inbound answer/reject/remote cancel และ DTMF
- microphone/speaker fallback
- browser refresh/reconnect, replay และ simulated ACK
- ยืนยันหนึ่ง `call.ended` ต่อสายและตรวจ safe logs/outboxจริง

## Deferred by confirmed scope

- tray/start-at-login/single-instance/Generic Host และ full serialized actor: P1
- TCP, capacity policy, corrupt-outbox repair, maintenance, soak และ deep recovery: P2
- Portal/Web API/Collection DB call history integration: P3-P4
- installer signing/canary/operations: P5
