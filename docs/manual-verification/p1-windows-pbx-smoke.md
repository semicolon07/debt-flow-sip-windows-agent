# P1 Windows/PBX verification checklist

สถานะ phase: **P1 complete by owner acceptance on 01/10/2026** หลังยืนยันว่า Agent ทำงานปกติ
บน Windows กับ PBX Sandbox. ไม่มี granular result attachment ถูกส่งเข้า repository จึงไม่เติม
PASS/FAIL ราย case โดยสมมติ; checklist นี้คงไว้เป็น regression source สำหรับ P2 และ release gate.

ใช้กับ self-contained `win-x64` artifact บน Windows 10 1809+ และ Windows 11 โดยใช้ test PBX account เท่านั้น.
ห้ามใส่ password, เบอร์เต็ม, SDP หรือ SIP headers ใน screenshot/result document

ก่อนเริ่มให้ copy [result template](p1-windows-pbx-result-template.md), กรอก run identity/environment
และอ้าง case ID ด้านล่างกับ evidence ทุกชิ้น. `SKIPPED` ถือว่ายังไม่ผ่าน completion gate

## 1. Automated Windows gate

```powershell
./scripts/verify-p1-windows.ps1
```

- `AUTO-01` รัน verifier จบด้วย exit code 0
- `AUTO-02` แนบทั้งโฟลเดอร์ `artifacts/p1-windows/<run-id>` ซึ่งมี OS/runtime metadata,
artifact SHA-256, test totals/TRX, vulnerability JSON และ command logs.
Automated gate ผ่านต่อเมื่อ script exit code เป็น 0, tests อย่างน้อย 67 cases
(46 core + 21 Windows host ณ baseline นี้). Script บังคับ clean Git worktree ก่อนเริ่มและ scan
NuGet vulnerability ทุก project ใน solution; dirty tree หรือ vulnerable package ใด ๆ ต้อง fail gate

## 2. Tray/non-admin gate

- `TRAY-01` รัน EXE จาก stable extracted folder ด้วย standard user; ต้องไม่มี UAC/URL ACL prompt
- `TRAY-02` ตรวจ tray status ไทยและอังกฤษตาม Windows UI language
- `TRAY-03` ตรวจ `%LOCALAPPDATA%\DebtFlow\SipAgent\agentsettings.json` valid/invalid/missing
- `TRAY-04` เปิด instance ที่สอง; ต้องไม่เปิด listener/SIP ซ้ำและ tray เดิมแจ้งเตือน
- `TRAY-05` toggle Start with Windows, sign out/in และตรวจหนึ่ง process ต่อ user
- `TRAY-06` Open log folder ต้องเปิด `%LOCALAPPDATA%\DebtFlow\SipAgent\Logs`
- `TRAY-07` port 8443 ถูกใช้งานอยู่ต้องแสดง actionable error โดยไม่ crash/เปิด listenerอื่น
- `TRAY-08` clean user profile แสดง consent ครั้งเดียวและติดตั้ง certificate โดยไม่ใช้ UAC
- `TRAY-09` trust policy denial เปิด degraded recovery tray โดยไม่ initialize SIP/listener; Repair แล้วเริ่ม runtime ใน process เดิม
- `TRAY-10` Remove certificate ลบ tracked My/Root/CNG key/metadata แต่คง config, SQLite และ logs

## 3. WebSocket V1 gate

- `WS-01` เริ่ม `--console`, serve `poc.html` ที่ HTTP localhost:8765 และ connect WSS สำเร็จ
- `WS-02` unknown/missing Origin ได้ HTTP 403; second client ได้ `client_already_connected`
- `WS-03` request ที่มี query string ทุกชนิดถูกปฏิเสธ และ query value ไม่ปรากฏใน log
- `WS-04` welcome มาก่อน snapshot และ replay; heartbeat 15/45 วินาทีไม่ disconnectผิดพลาด
- `WS-05` message เกิน 64 KiB และมากกว่า 60 messages/10 seconds ถูกปฏิเสธด้วย safe code
- `WS-06` disconnect/reconnectภายใน 60 วินาทีรักษา registration และ replay pending events
- `WS-07` disconnectเกิน 60 วินาทีเมื่อไม่มีสายต้อง unregister/clear credential
- `WS-08` plaintext WS บน 8443 ล้มเหลว และ shipping artifact ไม่มี plaintext endpoint/fallback
- `WS-09` HTTPS Portal เชื่อมสำเร็จบน Edge/Chrome current enterprise; ตรวจ IPv4/IPv6 loopback และ Local Network Access policy

## 4. PBX/audio gate

- `PBX-01` register/unregister และ registration failure ใช้ normalized state/code
- `PBX-02` configure/register กับ canonical IPv4 และ IPv6 test PBX; IPv6 registrar ต้องใช้ bracketed URI ถูกต้อง
- `PBX-03` outbound: answer, busy, no-answer, cancel, local hangup, remote hangup
- `PBX-04` inbound: answer, reject, remote cancel, remote hangup
- `PBX-05` inbound ขณะ Portal disconnected ต้องตอบ SIP 480 และจบ `rejected/portal_unavailable`
- `PBX-06` active call ขณะ Portal disconnected ต้องคงสาย; reconnectเห็น snapshotเดิม
- `PBX-07` disconnectเกิน grace ระหว่าง active call ต้อง cleanup registrationหลัง terminal event
- `PBX-08` DTMF ส่ง/รับได้โดย digitไม่อยู่ใน event/log
- `PBX-09` microphone/speaker พร้อมและ fallback ไม่มี process crash
- `PBX-10` ทุก call มี `call.ended` หนึ่งครั้ง; replay/ACK ไม่สร้าง terminal/eventซ้ำ
- `PBX-11` Exit จาก tray ระหว่างสายต้องถามยืนยันและ graceful shutdownไม่เกิน 10 วินาที

## 5. Privacy/evidence gate

- `SEC-01` scan JSONL และ SQLite ไม่พบ password, raw number, DTMF digit, SDP, Authorization หรือ raw SIP message
- `SEC-02` เก็บเฉพาะ masked/redacted extracts, agent version, agentInstanceId แบบจำกัด และ event counts
- `SEC-03` ระบุ PASS/FAIL/SKIPPED พร้อมเหตุผลทุกข้อ; FAIL หรือ SKIPPED ใด ๆ ทำให้ P1 ยังไม่ complete
