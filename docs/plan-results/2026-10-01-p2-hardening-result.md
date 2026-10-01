# P2 durable delivery and SIP/audio hardening result

วันที่: 01/10/2026

สถานะ: **Source implemented — Windows/PBX acceptance pending**

## Implemented source

- SQLite schema v2 transactional migration, newer/corrupt/invariant fail-closed checks, active-call journal,
  command execution state, retention, WAL checkpoint และ capacity policy 80/90/100%
- startup reconciliation ปิด non-terminal call ด้วย `agent_restarted` ครั้งเดียวโดยไม่เปลี่ยน identity/sequence
- registration retry แบบ exponential full jitter 2–60 วินาที, generation cancellation และ stable reset 5 นาที
- UDP first + TCP channel/fallback เฉพาะ temporary transport failure; terminal SIP failureไม่ fallback
- outbound/answer deadlines, serialized idempotent media cleanup และ late registration callback suppression
- Windows Core Audio device notification debounce; active call device loss degraded โดยไม่บังคับ hangup
- tray safe diagnostic ZIP ที่มี aggregate state/health/audio counts และ bounded sanitized logsเท่านั้น
- Windows P2 verifier และ PBX/device/RTP/diagnostic/2-hour soak checklist

## Evidence executed in this environment

Environment: macOS arm64, .NET SDK 10.0.401; Windows Desktop runtimeไม่มีใน host นี้.

| Command | Result |
| --- | --- |
| `dotnet build softphone-native-client.sln -c Release --no-restore -p:EnableWindowsTargeting=true` | PASS, 0 warnings, 0 errors |
| `dotnet test tests/DebtFlow.SipAgent.Core.Tests/DebtFlow.SipAgent.Core.Tests.csproj -c Release` | PASS, 58/58 |
| Windows host-test project compile | PASS, 23 source cases |
| Windows host-test execution | NOT RUN: macOS ไม่มี `Microsoft.WindowsDesktop.App` |
| self-contained multi-file `win-x64` publish | PASS, 447 files |
| P2 verification ZIP | PASS, 76,642,675 bytes; SHA-256 `d36a7728d251a0f660cafa5171a82eb70aa64ffba10d1ca218a752264dbd703b` |
| NuGet vulnerability scan | Portable projects + Windows host-test dependency graph report 0 known vulnerable packages; direct host/solution command on macOS exitsด้วย .NET CLI `Sequence contains no matching element`, จึงต้องยืนยันซ้ำด้วย Windows verifier |

Core suite รวม migration v1→v2, newer schema preservation, call journal restart reconciliation,
capacity reserve/block, deterministic retry/cancel race และ fake 1,000 sequential call cycles
(5,000 durable events, exactly 1,000 terminal events).

## Acceptance still required

- รัน `scripts/verify-p2-windows.ps1` จาก clean Windows worktreeและเก็บ TRX/evidence/checksum
- รัน UDP/TCP/auth/retry/PBX matrix, audio removal/reappearance และ diagnostic artifact privacy review
- รัน 2 ชั่วโมง/50 calls soak พร้อม resource trend
- ทดสอบ `AcceptRtpFromAny=false` กับ direct/NAT PBX; source production ยังใช้ `true` จนมี evidence

P2 ยังไม่ complete และเอกสารนี้ไม่สมมติผล Windows/PBX/soak ที่ยังไม่ได้รับ.
