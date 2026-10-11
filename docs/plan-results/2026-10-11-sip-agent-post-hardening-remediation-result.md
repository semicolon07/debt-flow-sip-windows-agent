# SIP Windows Agent post-hardening remediation result

วันที่ implement/verify: 11/10/2026  
Repository scope: `sip-windows-agent` เท่านั้น  
Implementation base: `0188fe94b77629b48e5074a748642c67c1e85c12` (`main`)  
Authority: [`2026-10-11-sip-agent-post-hardening-remediation-plan.md`](../2026-10-11-sip-agent-post-hardening-remediation-plan.md)  
สถานะ: **Source remediation implemented; Windows/PBX/release evidence pending — ยังไม่ production ready**

## Executive result

รอบนี้ปิด source failure modes `SIP-POST-001` ถึง `SIP-POST-011` โดยไม่เปลี่ยน Protocol V1,
SQLite schema v4 หรือ Portal contract. จุดเปลี่ยนหลักคือ call/media ownership แบบ generation-aware,
late-answer termination fence, command execution/finalization lifetime ที่ไม่ผูกกับ client request,
projected terminal capacity reserve, physical SIP quiesce เมื่อ signal overload, retryable bounded cleanup,
post-commit isolation, owned maintenance lifetime, explicit WebSocket control failure และ non-fatal exception policy.

ผลที่ยืนยันบน macOS arm64:

- locked restore: PASS
- Release solution cross-build: PASS, 0 warnings, 0 errors
- portable core tests: PASS, 95/95
- Windows Host และ Host.Tests: compile PASS
- Host tests execution: BLOCKED ตามคาด เพราะเครื่องไม่มี `Microsoft.WindowsDesktop.App 10.0`
- whitespace verification และ `git diff --check`: PASS
- NuGet vulnerability query: portable projectsและ Host.Tests graphรายงาน 0 vulnerable packages;
  direct host/solution queryยังชน .NET SDK error `Sequence contains no matching element`

ผลนี้เป็น source-complete checkpoint ไม่ใช่ release approval. Delayed-answer/PBX, audio-device race,
Windows host execution, shutdown timing, 2-hour soak, privacy scan, artifact/rollback rehearsal และ owner sign-off
ยังต้องรันบน exact candidate commit.

## Finding disposition

| ID | Source disposition | Implementation evidence |
| --- | --- | --- |
| SIP-POST-001 | Implemented | `SipRuntime.AnswerAsync` เก็บ pending owner ระหว่าง answer, terminateด้วย Reject/Cancel/Hangup, รอ bounded grace, block callใหม่ระหว่าง late completion และ force hangupเมื่อ late success |
| SIP-POST-002 | Implemented | media stateมี call-generation owner; cleanup acquire gateแล้ว revalidate expected generationก่อน detach/close |
| SIP-POST-003 | Implemented + regression | หลัง durable `executing` marker ใช้ dispatcher lifetimeและ bounded finalization token; request cancellation testยืนยัน journalเป็น `completed` |
| SIP-POST-004 | Implemented + regression | projected count/estimated bytesตรวจใต้ serialized gateและ transaction; reserveสอง terminal slots, hard ceiling, health fieldsสำหรับ reserve |
| SIP-POST-005 | Implemented + regression | overflow reportแนบ active handle; coordinatorเรียก physical `HangupAsync` ก่อน logical terminal/degraded transition |
| SIP-POST-006 | Implemented + regression | `stopping` ไม่เป็น no-op; shutdown invocationถัดไป resume active phaseได้ และ disposeพยายาม cleanupสองรอบภายใน 10 วินาที |
| SIP-POST-007 | Implemented | media gate waitsรับ cancellationและ cleanup pathsใช้ 3-second bound |
| SIP-POST-008 | Implemented + regression | terminal commit/in-memory applyแยกจาก health/registration cleanup; post-commit failureไม่ย้อน command success |
| SIP-POST-009 | Implemented | prune yieldออกจาก request stack, มี dispatcher-owned CTS, timeout, failure backoff และ bounded drain; first commandไม่ trigger pruneซ้ำ startup |
| SIP-POST-010 | Implemented + regression compile | offline control sendคืน `WebSocketException(socket_not_open)`; durable/realtime offline semanticsคงชัดเจน; request abortแยกจาก receive timeout; deferred/activation healthเพิ่มแล้ว |
| SIP-POST-011 | Implemented | shared `OperationalExceptionPolicy` ป้องกัน fatal exceptionถูกกลืนใน continuation/callback boundariesสำคัญ; DI factoryเป็น ownerของ event store disposal |

## Implementation details

### Runtime และ call/media ownership

- pending inbound referenceไม่ถูกทิ้งก่อน answer outcomeชัดเจน
- remote cancel/ring timeout/hangupระหว่าง answerตั้ง termination fenceก่อน schedule cleanup
- answerที่ยังไม่จบภายใน 2-second termination grace emit `answer_termination_pending` runtime failureและ block callใหม่
- media session, endpointและfallback sourceถูก publish/clearพร้อม generation owner
- stale cleanupคืนโดยไม่แตะ leaseของ callใหม่; duplicate cleanup idempotent
- overflow control signalพก active call handleเพื่อให้ coordinator terminate physical legได้

### Durability, capacity และ command lifecycle

- request tokenใช้ถึง durable `executing` commitเท่านั้น
- executionมี Agent-owned lifetimeและ 2-minute upper bound; final journal saveมี independent 5-second bound
- command pruneมี single due schedule, `Task.Yield`, lifetime cancellationและ 5-second drain
- regular/non-terminal appendใช้สอง event slotsสุดท้ายไม่ได้; terminal batchใช้ reserveได้แต่ห้ามเกิน hard max
- byte policyกัน terminal reserveสูงสุด 1 MiBและใช้ conservative encoded-size/page-overhead estimate
- health/diagnostic snapshotเพิ่ม regular slots/storage remainingและ terminal reserve availability

### Shutdown, post-commit และ transport

- shutdownยังเดิน independent unregister/checkpoint phaseแม้ hangup/terminal phaseล้ม และ invocationถัดไป retryได้
- `DisposeAsync` แบ่งสอง attemptรอบละ 5 วินาที
- durable terminalที่ commitแล้วไม่ถูกเปลี่ยนเป็น command failureจาก health read/registration stopภายหลัง
- WebSocket control deliveryไม่รายงาน successเมื่อไม่มี session
- deferred/activation queue depth/high-waterถูกรวมใน diagnostic bundle
- event store registrationเปลี่ยนจาก externally-owned singleton instanceเป็น DI factory-owned disposable

## Regression coverage added

- `Capacity_ReservesTerminalBatchAndChecksProjectedSize`
- `RuntimeFailure_WithPhysicalCall_QuiescesSipBeforeEndingLogicalCall`
- `Shutdown_WhenHangupPhaseFails_CanResumeOnNextInvocation`
- `TerminalCommit_WhenHealthRefreshFails_RemainsSuccessfulAndRunsCleanup`
- `RequestCancellationAfterExecutingMarker_DoesNotStrandCommandJournal`
- `Offline_ControlFailsExplicitlyWhileDurableAndRealtimeRemainNonBlocking`

Host-only regressions compileบน cross-target แต่ต้อง executeบน Windows verifierก่อน release.

## Verification evidence

```text
dotnet restore softphone-native-client.sln --locked-mode -p:EnableWindowsTargeting=true
PASS: all projects up-to-date

dotnet build softphone-native-client.sln -c Release --no-restore -p:EnableWindowsTargeting=true
PASS: 0 warnings, 0 errors

dotnet test tests/DebtFlow.SipAgent.Core.Tests/DebtFlow.SipAgent.Core.Tests.csproj -c Release --no-build
PASS: 95/95

dotnet format whitespace softphone-native-client.sln --verify-no-changes --no-restore
PASS (workspace-load warning only; exit 0)

git diff --check
PASS
```

Host test executionบน macOS abortด้วย missing Windows Desktop runtime ไม่ใช่ test failure:

```text
Framework: Microsoft.WindowsDesktop.App, version 10.0.0
No frameworks were found for osx-arm64
```

Vulnerability scan:

- Protocol/Application/Persistence/Core.Tests/Host.Tests: 0 vulnerable packagesจาก `nuget.org`
- direct Host/solution: SDK CLI error `Sequence contains no matching element`; Windows verifierยังเป็น release gate

## Deviations from proposed structure

พฤติกรรมบังคับของ planถูก implement แต่ใช้โครงสร้างที่เล็กกว่าในสามจุด:

1. answer/media leaseยังอยู่ใน `SipRuntime` แทนการแยก SIPSorcery adapterและ classใหม่; ownership/fence
   ถูกบังคับด้วย generation fieldsและ critical sections. ต้องใช้ Windows/PBX testพิสูจน์ third-party timingจริง.
2. signal ingressยังเป็น bounded laneเดิมและใช้ controlled runtime failureที่ terminate physical leg
   แทน dual priority channel. หาก Windows flood testพบ critical-stop latencyเกิน budget ต้องยกระดับเป็น priority lanes.
3. shutdownใช้ retryable idempotent phase conditionsและ repeated invocation แทน shared shutdown-task objectเต็มรูปแบบ.
   Regressionยืนยัน resumeหลัง hangup phase failure แต่ full per-phase fault matrixยัง pending Windows.

ไม่มี deviationใดเปลี่ยน protocol/schema/rollback compatibility แต่ทั้งหมดเป็นหัวข้อที่ต้อง verifyใน external matrix.

## Remaining release gates / DoD

- [x] source changesสำหรับ SIP-POST-001 ถึง SIP-POST-011
- [x] locked restore และ Release cross-build 0 warnings/errors
- [x] portable core tests 95/95
- [x] Windows Host/Host.Tests compile
- [x] whitespace และ diff check
- [ ] Windows Host tests execute 100%
- [ ] direct host vulnerability scanผ่านใน Windows verifier
- [ ] delayed answer/remote cancel/device loss PBX interleavings
- [ ] signal saturationและshutdown ≤10 s matrix
- [ ] 2-hour/50-call soakและ resource trend
- [ ] privacy scan, artifact checksum/SBOM, rollback rehearsal
- [ ] owner sign-offสำหรับ approved residual exceptions

## Release decision

**No-go for production declaration at this checkpoint.** Source blockersได้รับ remediationแล้ว แต่ต้องรัน
`scripts/verify-p2-windows.ps1`, `scripts/verify-p5-release.ps1` และ manual PBX/audio/soak matrixบน exact
candidate commitก่อนเปลี่ยนสถานะเป็น production ready. หากพบ ghost call, cross-generation cleanup,
silent control loss, double/missing terminal, journalค้าง executing หรือ shutdownเกิน 10 วินาทีให้ abortทันที.
