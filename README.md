# Debt Flow SIP Windows Agent

P0 production baseline สำหรับ per-user SIP/audio agent ของ Debt Flow Portal.
P0 ยังเป็น console host; tray/start-at-login อยู่ P1

## Runtime baseline

- .NET 10 LTS
- Windows 10 1809 หรือใหม่กว่า
- SIPSorcery 10.0.16, UDP transport
- NAudio 3.1.0
- Local WebSocket V1: `ws://localhost:8443/agent/v1`
- SQLite durable event store: `%LOCALAPPDATA%\DebtFlow\SipAgent\agent-v1.db`

Agent รองรับหนึ่ง controlling browser client และหนึ่ง active call.
`call.start` ใช้ได้เมื่อ SIP registration เป็น `registered` เท่านั้น

## Build และ test

บน Windows:

```powershell
dotnet restore softphone-native-client.sln --locked-mode
dotnet build softphone-native-client.sln -c Release --no-restore
dotnet test tests/DebtFlow.SipAgent.Core.Tests/DebtFlow.SipAgent.Core.Tests.csproj -c Release --no-build
```

บน non-Windows สามารถ build เพื่อตรวจ source ได้ด้วย:

```bash
dotnet restore softphone-native-client.sln -p:EnableWindowsTargeting=true --locked-mode
dotnet build softphone-native-client.sln -c Release --no-restore -p:EnableWindowsTargeting=true
dotnet test tests/DebtFlow.SipAgent.Core.Tests/DebtFlow.SipAgent.Core.Tests.csproj -c Release --no-build
```

## V1 diagnostic client

หน้า `poc.html` เป็น development diagnostic เท่านั้น ไม่มี default PBX credential และไม่ใช่ Portal production

1. Start agent ด้วย `dotnet run --project softphone-native-client.csproj`
2. ที่ repository root รัน `python3 -m http.server 8765`
3. เปิด `http://localhost:8765/poc.html`
4. Connect แล้ว configure/register ด้วย test credential ผ่าน UI

ห้ามเปิดหน้าโดย `file://`; Agent ปฏิเสธ `Origin: null`.
Origin development ที่อนุญาตโดย defaultคือ `http://localhost:8765` และ `http://127.0.0.1:8765`.
เพิ่ม origin ชั่วคราวได้ด้วย `--allowed-origin https://example.test`

Checkbox “Simulate backend commit and ACK durable events” ปิดโดย default.
เปิดเฉพาะเมื่อต้องการทดสอบ ACK หลังจำลองว่า backend commit สำเร็จแล้ว

## Security and privacy

- Agent bind ผ่าน `localhost` และตรวจ exact Origin allowlist
- SIP password อยู่ memory เท่านั้นและถูกล้างเมื่อ unregister/shutdown
- password, raw destination, DTMF digit, SDP และ SIP Authorization ไม่อยู่ใน event/outbox/log
- durable `call.*` event ถูกเขียน SQLite ก่อนส่ง WebSocket
- หาก SQLite ใช้งานไม่ได้ Agent เริ่มในสถานะ degraded และ block SIP registration/call
- raw SIP debug logging ปิดโดย default

`AcceptRtpFromAny=true` ยังคงไว้เพื่อรักษา PoC behavior และเป็น P2 security review gate

## P0 limitation

- ยังไม่มี tray UI, single-instance mutex, installer หรือ start-at-login
- ยังไม่มี TCP certification
- ยังไม่มี Portal/Web API integration
- outbox capacity, maintenance และ recovery tooling เชิงลึกอยู่ P2
- ต้องรัน Windows/PBX manual smoke matrix ก่อนประกาศว่า P0 complete

