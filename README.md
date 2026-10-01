# Debt Flow SIP Windows Agent

Per-user SIP/audio tray agent สำหรับ Debt Flow Portal บน Windows 10 1809 ขึ้นไป.
สถานะปัจจุบันคือ **P1 source implemented — Windows/PBX verification pending**

## Runtime

- .NET 10 LTS, self-contained `win-x64`
- WinForms tray + .NET Generic Host
- Kestrel loopback WebSocket V1: `ws://localhost:8443/agent/v1`
- SIPSorcery 10.0.16 ผ่าน UDP; TCP certification อยู่ P2
- SQLite outbox: `%LOCALAPPDATA%\DebtFlow\SipAgent\agent-v1.db`
- JSONL logs: `%LOCALAPPDATA%\DebtFlow\SipAgent\Logs`, 10 MB ต่อไฟล์/7 rolling files
- หนึ่ง Agent ต่อ Windows user, หนึ่ง Portal controller และหนึ่ง active call

Tray แสดง Agent, Portal, SIP, Call และ Audio status พร้อม Open log folder,
Start with Windows และ Exit. SIP host/account/password รับจาก Portal ผ่าน WebSocket เท่านั้น
และไม่ถูกบันทึกใน config, registry, SQLite หรือ log

## Production Origin configuration

Tray mode อ่าน exact allowlist จาก:

```text
%ProgramData%\DebtFlow\SipAgent\agentsettings.json
```

ใช้รูปแบบเดียวกับ [agentsettings.example.json](agentsettings.example.json):

```json
{
  "agent": {
    "allowedOrigins": ["https://portal.example.com"]
  }
}
```

ไฟล์นี้ห้ามมี SIP credential. Unknown field, URL ที่มี path/query/user-info หรือ allowlist ว่าง
ทำให้ Agent เริ่มแบบ degraded และไม่รับ Portal connection

## Build, test และ publish

Windows:

```powershell
./scripts/verify-p1-windows.ps1
```

Script นี้รัน locked restore, Release build, tests ทั้ง solution, vulnerability gate,
self-contained publish, ZIP integrity/checksum และสร้าง `p1-windows-evidence.json` ใน `artifacts/p1-windows/<run-id>`.

macOS/Linux ตรวจ core และ cross-build Windows source ได้ แต่รัน WinForms/Kestrel host tests ไม่ได้:

```bash
dotnet restore softphone-native-client.sln -p:EnableWindowsTargeting=true --locked-mode
dotnet build softphone-native-client.sln -c Release --no-restore -p:EnableWindowsTargeting=true
dotnet test tests/DebtFlow.SipAgent.Core.Tests/DebtFlow.SipAgent.Core.Tests.csproj -c Release --no-build
```

CI เรียก script เดียวกันและอัปโหลด ZIP, TRX, checksum/evidence และ logs ในนาม
`debt-flow-sip-agent-win-x64`; ยังไม่ใช่ signed installer

## V1 diagnostic client

หน้า `poc.html` เป็น development diagnostic เท่านั้น ไม่มี default PBX credential:

1. บน Windows รัน `dotnet run --project softphone-native-client.csproj -- --console`
2. อีก terminal รัน `python -m http.server 8765`
3. เปิด `http://localhost:8765/poc.html`
4. Connect แล้ว configure/register ด้วย test credential

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
- listener bind loopback, exact Origin, reject query string, one owner, 64 KiB และ 60 messages/10 seconds
- durable `call.*` ถูกเขียน SQLite ก่อน publish และ replay จนได้รับ contiguous ACK
- outbox runtime failure ทำ Agent degraded/fail closed; shutdown ยัง hang up/unregister แบบ best effort
- structured logger เก็บเฉพาะ template/safe properties และ redacts sensitive values

`AcceptRtpFromAny=true` ยังคงไว้เพื่อรักษา PoC behavior และเป็น P2 security review gate

## Remaining gates

- ใช้ [P1 Windows/PBX verification checklist](docs/manual-verification/p1-windows-pbx-smoke.md) เป็นหลักฐานกลาง
- รัน Windows host tests และ tray/startup/single-instance QA บน Windows 10 1809+/Windows 11
- รัน combined PBX smoke: registration, outbound/inbound, DTMF, audio และ reconnect/replay/ACK
- P2: TCP certification, retry/backoff, device reselection, outbox capacity/corruption และ soak
- phase หลัง: Portal/Web API/Collection DB call-history, installer, signing และ staged rollout
