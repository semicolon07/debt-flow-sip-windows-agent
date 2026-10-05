# Debt Flow SIP Windows Agent

Per-user SIP/audio tray agent สำหรับ Debt Flow Portal บน Windows 10 1809 ขึ้นไป.
สถานะปัจจุบันคือ **P5 source implemented — release-gated**

## Runtime

- .NET 10 LTS, self-contained `win-x64`
- WinForms tray + .NET Generic Host
- Kestrel loopback secure WebSocket V1: `wss://localhost:8443/agent/v1` (ไม่มี plaintext listener/fallback)
- SIPSorcery 10.0.16: UDP first และ automatic TCP fallback เมื่อเกิด transport-level temporary failure
- SQLite schema v3 outbox/call journal: `%LOCALAPPDATA%\DebtFlow\SipAgent\agent-v1.db`
- JSONL logs: `%LOCALAPPDATA%\DebtFlow\SipAgent\Logs`, 10 MB ต่อไฟล์/7 rolling files
- หนึ่ง Agent ต่อ Windows user, หนึ่ง Portal controller และหนึ่ง active call

Tray แสดง Agent, Portal, SIP, Call และ Audio status พร้อม Open log folder, safe diagnostic export,
Start with Windows และ Exit. SIP host/account/password รับจาก Portal ผ่าน WebSocket เท่านั้น
และไม่ถูกบันทึกใน config, registry, SQLite หรือ log

ครั้งแรก Agent ขอ consent ไทย/อังกฤษแล้วสร้าง self-signed certificate สำหรับ `localhost`,
`127.0.0.1` และ `::1` ด้วย CNG key แบบ non-exportable เฉพาะ Windows user. Certificate อยู่ใน
`CurrentUser\My` และ public trust copy อยู่ใน `CurrentUser\Root`; metadata อยู่ที่
`%LOCALAPPDATA%\DebtFlow\SipAgent\tls-certificate.json`. Agent rotate เมื่อเหลืออายุไม่เกิน 30 วัน.
หาก policy บล็อกการติดตั้ง trust Agent จะเปิด degraded recovery tray โดยไม่เปิด SIP หรือ listener.

## Production Origin configuration

Tray mode อ่าน allow-all flag หรือ exact allowlist จาก:

```text
%LOCALAPPDATA%\DebtFlow\SipAgent\agentsettings.json
```

ไฟล์ config, SQLite และ Logs อยู่ใต้โฟลเดอร์ข้อมูลรายผู้ใช้เดียวกัน และไม่ต้องใช้สิทธิ์
Administrator เพื่อสร้างหรือแก้ไข configuration.

ใช้รูปแบบเดียวกับ [agentsettings.example.json](agentsettings.example.json):

```json
{
  "agent": {
    "isAllowAllOrigins": true,
    "allowedOrigins": []
  }
}
```

ไฟล์นี้ห้ามมี SIP credential. `isAllowAllOrigins=true` bypass allowlistตาม accepted P5 exception.
หากตั้ง false ต้องมี exact `http/https` originอย่างน้อยหนึ่งค่า. เมื่อ configไม่มี Agent copy packaged example
แบบ atomicโดยไม่ overwrite; example/target invalidจะเปิด setup dialogก่อน listener. Legacy configที่มี listไม่ว่าง
inferเป็น exact mode ส่วน listว่าง/หาย inferเป็น allow-all.

## Build, test และ publish

Windows:

```powershell
./scripts/verify-p2-windows.ps1
```

Script นี้รัน locked restore, Release build, tests ทั้ง solution (ขั้นต่ำ 109 cases ตาม source ปัจจุบัน),
vulnerability gate, self-contained publish, ZIP integrity/checksum และสร้าง `p2-windows-evidence.json`
ใน `artifacts/p2-windows/<run-id>`. P1 verifier เดิมยังใช้ได้และ default เป็น P1 evidence.

macOS/Linux ตรวจ core และ cross-build Windows source ได้ แต่รัน WinForms/Kestrel host tests ไม่ได้:

```bash
dotnet restore softphone-native-client.sln -p:EnableWindowsTargeting=true --locked-mode
dotnet build softphone-native-client.sln -c Release --no-restore -p:EnableWindowsTargeting=true
dotnet test tests/DebtFlow.SipAgent.Core.Tests/DebtFlow.SipAgent.Core.Tests.csproj -c Release --no-build
```

CI เรียก P2 verifierและอัปโหลด ZIP, TRX, checksum/evidence และ logs. P5 releaseจาก clean exact tag
`v1.0.0` ใช้:

```powershell
./scripts/verify-p5-release.ps1
```

ผลลัพธ์คือ unsigned self-contained multi-file `debt-flow-sip-agent-1.0.0-win-x64.zip` พร้อม
SPDX SBOM, release manifestและ SHA-256 checksum; target machineไม่ต้องติดตั้ง .NET Runtime/SDK.

## V1 diagnostic client

หน้า `poc.html` เป็น development diagnostic เท่านั้น ไม่มี default PBX credential:

1. บน Windows รัน `dotnet run --project softphone-native-client.csproj -- --console`
2. อีก terminal รัน `python -m http.server 8765`
3. เปิด `http://localhost:8765/poc.html`
4. Connect แล้ว configure/register ด้วย test credential

หน้า HTTP localhost สำหรับ diagnostic สามารถเปิด `wss://localhost:8443` ได้เมื่อ certificate พร้อม.
Production รองรับ Portal ผ่าน HTTPS บน Microsoft Edge และ Google Chrome. หาก production edge กำหนด
CSP ภายนอก ต้องอนุญาต `connect-src wss://localhost:8443`.

Certificate maintenance แบบ explicit consent:

```powershell
DebtFlow.SipAgent.Host.exe --repair-local-certificate
DebtFlow.SipAgent.Host.exe --remove-local-certificate
```

flags นี้ใช้ร่วมกับ `--console`, `--background` หรือ `--allowed-origin` ไม่ได้ และจะปฏิเสธเมื่อ Agent
instance หลักกำลังทำงาน. Exit codeคือ `0` success, `2` invalid arguments, `4` TLS operation failureและ
`5` Agent already running. การลบโฟลเดอร์โปรแกรมไม่ลบ certificate; ให้ใช้ tray หรือ remove flag.

`--console` เท่านั้นที่อนุญาต `http://localhost:8765`, `http://127.0.0.1:8765`
และ `--allowed-origin`. Tray mode ไม่รับ command-line Origin override

## Lifecycle และ security

- Startup เปิดอัตโนมัติครั้งแรกผ่าน HKCU Run และผู้ใช้ปิด/เปิดได้จาก tray
- instance ที่สองส่งสัญญาณให้ tray เดิมแล้วปิด โดยไม่เปิด SIP/WebSocket ซ้ำ
- command, SIP callback, disconnect lease และ shutdown เข้า bounded single-reader queue เดียวกัน
- Portal disconnect มี grace 60 วินาที; reconnect ยกเลิก cleanup
- active call ไม่ถูกตัดเมื่อ Portal หลุด; หลังจบสายจึง unregister/ล้าง credential
- สายเข้าขณะไม่มี Portal ถูกตอบ 480 และจบด้วย `portal_unavailable`
- Exit ระหว่างสายต้องยืนยัน จากนั้น hangup/unregister/flush ภายใน 10 วินาที
- WSS listener bind loopback, configured allow-all/exact Origin, reject query string, one owner, 64 KiB และ 60 messages/10 seconds
- durable `call.*` ถูกเขียน SQLite ก่อน publish และ replay จนได้รับ contiguous ACK
- active-call journal ทำให้ process restart ปิด lifecycle เดิมด้วย `agent_restarted` เพียงครั้งเดียว
- command journal มี `executing/completed/failed`; command ที่ crash ค้างจะไม่ dial ซ้ำจาก commandId เดิม
- outbox warning 80%, block สายใหม่ 90%, hard ceiling 10,000 pending events หรือ DB+WAL 100 MiB
- registration retry ใช้ exponential full jitter 2–60 วินาที, cancel ด้วย generation และ reset หลัง registered 5 นาที
- audio default-device change ถูก debounce; device loss ระหว่างสายแสดง degraded โดยไม่ตัด SIP call
- outbox runtime failure ทำ Agent degraded/fail closed; shutdown checkpoint/hangup/unregister แบบ best effort
- structured logger เก็บเฉพาะ template/safe properties และ redacts sensitive values
- diagnostic ZIP มี safe summary และ bounded sanitized logs; ไม่รวม DB/config/credential/raw Origin

`AcceptRtpFromAny=true` ยังคงไว้เพื่อรักษา PBX compatibility และเป็น P2 acceptance residual:
ต้องทดสอบ direct/NAT PBX matrix ก่อนปิด P2; Portal ไม่มี command สำหรับเปลี่ยนนโยบายนี้

## Verification record และ next phase

- Owner ยืนยันเมื่อ 01/10/2026 ว่า Agent ทำงานปกติบน Windows กับ PBX Sandbox และปิด P1
- ใช้ [P1 Windows/PBX verification checklist](docs/manual-verification/p1-windows-pbx-smoke.md)
  เป็น regression source ต่อไป; repository ไม่สมมติ granular case evidence ที่ไม่ได้รับ
- P2 source implementation และ macOS cross-validation บันทึกใน
  [P2 result](docs/plan-results/2026-10-01-p2-hardening-result.md)
- การปิด P2 ยังรอ [P2 Windows/PBX checklist](docs/manual-verification/p2-windows-pbx-soak.md):
  Windows host tests, UDP/TCP matrix, device change, diagnostic privacy review และ 2 ชั่วโมง/50 calls soak
- P5 sourceเพิ่ม portable release pipelineและ additive outbox health snapshotแล้ว; Windows release runner,
  PBX/privacy matrix, owner migration readback, alert/rollback rehearsalและ full-target rolloutยัง pending
