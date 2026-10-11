# Debt Flow SIP Agent — Windows deployment guide

## ภาษาไทย

ชุด release มี 2 รูปแบบจาก publish เดียวกัน:

- `debt-flow-sip-agent-X.Y.Z-win-x64-setup.exe` — แนะนำสำหรับติดตั้ง/อัปเกรดภายในองค์กร
- `debt-flow-sip-agent-X.Y.Z-win-x64.zip` — portable fallback สำหรับ operator

รองรับ Windows 10 build 17763 ขึ้นไปและ Windows 11 แบบ x64, ติดตั้งเฉพาะผู้ใช้ปัจจุบันโดยไม่ใช้ UAC
และไม่ต้องติดตั้ง .NET Runtime/SDK เพิ่ม. ตรวจไฟล์ `.sha256` และ `installer-manifest.json` ก่อนแจกจ่ายเสมอ.

### ติดตั้งหรืออัปเกรด

เปิด setup EXE แล้วทำตาม wizard. โปรแกรมอยู่ที่
`%LOCALAPPDATA%\Programs\DebtFlow\SipAgent\versions\X.Y.Z`; ข้อมูลใช้งานอยู่แยกที่
`%LOCALAPPDATA%\DebtFlow\SipAgent`.

Installer จะขอให้ Agent หยุดอย่างปลอดภัยและตรวจ storage ก่อนเปลี่ยน version. หากมีสายอยู่จะยกเลิกด้วย
`update_blocked_active_call`/exit 30 โดยไม่ตัดสาย; ให้จบสายแล้วรัน installer ใหม่. หาก Agent รุ่น portable เก่าไม่รองรับ
maintenance protocol ให้ Exit จาก tray แล้วเลือก Retry. Downgrade ด้วย setup ถูกปิดโดยออกแบบ.

Silent install/repair สำหรับ IT:

```powershell
./debt-flow-sip-agent-X.Y.Z-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="installer.log"
```

Silent mode จะไม่เปิด Agent และไม่ยอมรับ TLS consent แทนผู้ใช้. ผู้ใช้ต้องเปิด Agent เองครั้งแรกจาก Start Menu.

### ถอนการติดตั้ง

Normal uninstall เก็บ settings, SQLite/outbox, logs, audio preferences และ localhost certificate ทั้งหมด.

```powershell
& "$env:LOCALAPPDATA\Programs\DebtFlow\SipAgent\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="uninstall.log"
```

ตัวเลือก destructive ต้องระบุแยกอย่างชัดเจนและไม่มีค่าใด imply อีกค่า:

```powershell
# ถอนเฉพาะ Agent-owned localhost certificate เพิ่มเติม
& "$env:LOCALAPPDATA\Programs\DebtFlow\SipAgent\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PURGECERT=1

# ลบข้อมูล Agent ของผู้ใช้ปัจจุบันอย่างถาวรเพิ่มเติม
& "$env:LOCALAPPDATA\Programs\DebtFlow\SipAgent\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PURGEDATA=1
```

Interactive uninstall จะถาม certificate และ data แยกกัน โดยค่าเริ่มต้นเป็นไม่ลบ. หากมี active call uninstall จะหยุด.

### Rollback และ portable fallback

Rollback เป็นงานของ operator เท่านั้นและทำได้เมื่อ previous version ที่ installer เก็บไว้ผ่าน hash/schema/protocol check:

```powershell
./scripts/invoke-installer-rollback.ps1 -TargetVersion X.Y.Z
```

ห้ามนำ setup รุ่นเก่ามาทำ downgrade. หากจำเป็นต้องใช้ ZIP fallback ให้ Exit Agent, รัน
`DebtFlow.SipAgent.Host.exe --storage-preflight`, แตก ZIP ลงโฟลเดอร์ version ใหม่และเปิด executable จากโฟลเดอร์นั้น.
ไปต่อเฉพาะ preflight exit 0; exit 20/21 ให้หยุดและติดต่อ owner.

Agent ใช้ `wss://localhost:8443/agent/v1` เท่านั้น. การเปิดครั้งแรกจะขอ consent ก่อนสร้าง/trust certificate.
Artifact ยัง unsigned ตาม approved exception จึงอาจมี SmartScreen warning; self-signed localhost certificate
ไม่ใช่ Authenticode signature. SQLite/WAL ยังเป็น plaintext ตาม accepted exception.

## English

Each release provides two artifacts produced from the same verified publish directory:

- `debt-flow-sip-agent-X.Y.Z-win-x64-setup.exe` — recommended internal install/upgrade artifact
- `debt-flow-sip-agent-X.Y.Z-win-x64.zip` — operator portable fallback

The Agent supports x64 Windows 10 build 17763 or later and Windows 11. Installation is per-user,
non-elevated, and self-contained. Always verify the `.sha256` file and `installer-manifest.json` before distribution.

### Install or upgrade

Run the setup EXE and follow the wizard. Program files are versioned under
`%LOCALAPPDATA%\Programs\DebtFlow\SipAgent\versions\X.Y.Z`; runtime data remains separate under
`%LOCALAPPDATA%\DebtFlow\SipAgent`.

Setup requests a graceful Agent shutdown and runs storage preflight before changing versions. An active call aborts
with `update_blocked_active_call`/exit 30 without hanging up; finish the call and rerun setup. For an older portable
Agent without the maintenance protocol, exit it from the tray and choose Retry. Setup downgrade is intentionally blocked.

IT silent install/repair:

```powershell
./debt-flow-sip-agent-X.Y.Z-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="installer.log"
```

Silent mode does not launch the Agent or accept TLS consent. The user starts it from the Start Menu on first use.

### Uninstall

Normal uninstall preserves settings, SQLite/outbox, logs, audio preferences, and the localhost certificate:

```powershell
& "$env:LOCALAPPDATA\Programs\DebtFlow\SipAgent\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="uninstall.log"
```

Destructive choices are separate and explicit; neither option implies the other:

```powershell
# Also remove the Agent-owned localhost certificate
& "$env:LOCALAPPDATA\Programs\DebtFlow\SipAgent\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PURGECERT=1

# Also permanently remove current-user Agent data
& "$env:LOCALAPPDATA\Programs\DebtFlow\SipAgent\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PURGEDATA=1
```

Interactive uninstall asks about certificate and data separately, defaulting both to preserve. An active call blocks uninstall.

### Rollback and portable fallback

Rollback is operator-only. It requires a retained previous version that passes file hash, schema, and protocol checks:

```powershell
./scripts/invoke-installer-rollback.ps1 -TargetVersion X.Y.Z
```

Do not use an older setup EXE to downgrade. For ZIP fallback, exit the Agent, run
`DebtFlow.SipAgent.Host.exe --storage-preflight`, extract into a new version folder, and launch from that folder.
Continue only for exit 0; stop and contact the owner for exit 20/21.

The Agent exposes only `wss://localhost:8443/agent/v1`. First launch requests consent before creating/trusting its
localhost certificate. Artifacts remain unsigned under the approved exception and may trigger SmartScreen; the localhost
self-signed certificate is not an Authenticode signature. SQLite/WAL remain plaintext under the accepted exception.
