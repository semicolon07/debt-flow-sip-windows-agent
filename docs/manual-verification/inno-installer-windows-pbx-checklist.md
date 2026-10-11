# Inno installer Windows/PBX acceptance checklist

วันที่: __________  Commit/tag: __________  Setup SHA-256: __________  Operator: __________

ใช้กับ setup artifact ที่สร้างจาก exact clean tag ผ่าน `scripts/verify-p5-release.ps1` เท่านั้น. ใช้ PBX sandbox,
test account และข้อมูลที่ไม่มี production credential/number. แนบ Inno log, installer manifest, automated result JSON
และ Agent diagnostic ที่ redact แล้ว; ห้ามแนบ `%LOCALAPPDATA%\DebtFlow\SipAgent` ทั้ง directory.

## 1. Artifact และ toolchain

- [ ] ZIP และ setup มาจาก release run เดียวกัน; version/commit/protocol/schema ตรงกัน.
- [ ] SHA-256 ของ ZIP/setup/manifest ตรงไฟล์ที่เผยแพร่.
- [ ] `installer-manifest.json` ระบุ Inno 7.1.0 x64, per-user, minimum OS และ signed state ตามจริง.
- [ ] Setup ไม่มี `.pfx`, `.p12`, `.pem`, `.key`, credential หรือ production config.
- [ ] บันทึก owner/legal decision เรื่อง Inno commercial license; ไม่มี license key ใน repo/artifact/log.

## 2. Automated Windows gate

```powershell
./scripts/verify-p2-windows.ps1
./scripts/verify-installer-windows.ps1 -InstallerPath <setup.exe> -ExpectedVersion <X.Y.Z>
```

- [ ] Locked restore, Release build, Core/Host tests และ vulnerability scanผ่าน.
- [ ] Silent fresh install/metadata/repair/default uninstall/data purge automationผ่านบน clean current-user profile.
- [ ] `git diff --check` ผ่านบน exact release commit.

## 3. OS/user/profile matrix

รัน fresh install, repair และ normal uninstallครบแต่ละแถว:

| Case | Fresh | Repair | Uninstall | Evidence |
| --- | --- | --- | --- | --- |
| Windows 10 Enterprise LTSC x64, standard user | [ ] | [ ] | [ ] | |
| Windows 10 Enterprise LTSC x64, non-elevated admin | [ ] | [ ] | [ ] | |
| Windows 11 x64, standard user | [ ] | [ ] | [ ] | |
| Windows 11 x64, non-elevated admin | [ ] | [ ] | [ ] | |
| Thai/non-ASCII username + path with spaces | [ ] | [ ] | [ ] | |

ตรวจทุก case:

- [ ] ไม่มี UAC; files อยู่ใต้ `%LOCALAPPDATA%\Programs\DebtFlow\SipAgent\versions\X.Y.Z`.
- [ ] Add/Remove Programs และ Start Menu target version ปัจจุบัน; desktop shortcut default off.
- [ ] Silent installไม่เปิด Agent/TLS consent; interactive finish launchเป็น opt-in checkbox.
- [ ] First runสร้าง/trust localhost certificateเมื่อผู้ใช้ consent และ tray พร้อมใช้งาน.

## 4. Upgrade, repair และ failure safety

- [ ] Real previous production version N-1 -> N ผ่าน; package N-1 checksumได้รับการยืนยัน.
- [ ] Config, SQLite/WAL, logs, audio preferences, TLS metadata/certificate และ startup preferenceคงอยู่.
- [ ] Same-version repairสร้างไฟล์/shortcutที่หายใหม่โดยไม่ reset runtime state.
- [ ] Setup รุ่นต่ำกว่า active versionถูก blockก่อน copy/pointer mutation.
- [ ] Storage preflight fixtures exit 20 และ 21 ถูก blockก่อน pointer switchพร้อมข้อความ actionable.
- [ ] Cancel/copy failure/disk-full/locked-file ก่อน activation ยังเปิด active versionเดิมได้.
- [ ] Startup disabledยัง disabled; owned enabled Run value retarget; tampered/unrelated valueไม่ถูกแตะ.
- [ ] เหลือ active และ previous version; ไม่มี mixed-version DLL set.

## 5. Active-call และ PBX/browser regression

| State | Installer result | Call result | Evidence |
| --- | --- | --- | --- |
| Idle tray | [ ] upgradeผ่าน | n/a | |
| Outbound ringing/active | [ ] exit 30/block | [ ] ไม่ hangup | |
| Inbound ringing/active | [ ] exit 30/block | [ ] ไม่ reject/hangup | |
| TLS degraded tray | [ ] graceful shutdown | n/a | |

- [ ] หลัง fresh install: Edge outbound/inbound ผ่าน.
- [ ] หลัง fresh install: Chrome outbound/inbound ผ่าน.
- [ ] หลัง N-1 -> N: Edge reconnect + outbound/inbound ผ่าน.
- [ ] หลัง N-1 -> N: Chrome reconnect + outbound/inbound ผ่าน.
- [ ] Direct/NAT และ UDP/TCP fallback matrixเดิมไม่ regression.

## 6. Rollback และ uninstall

- [ ] `invoke-installer-rollback.ps1 -WhatIf` แสดง exact N -> N-1 โดยไม่ mutate.
- [ ] Controlled rollbackผ่านเมื่อ manifest hashes/protocol/schema/preflight compatible.
- [ ] Unsafe root, tampered path, hash mismatch และ incompatible schema/protocolถูก rejectก่อน pointer change.
- [ ] Normal uninstallขณะ idleลบ program/owned shortcut/owned Run value แต่เก็บ data+certificate.
- [ ] Active-call uninstallถูก blockและไม่ตัดสาย.
- [ ] `/PURGECERT=1` ลบเฉพาะ Agent-owned certificateและเก็บ data.
- [ ] `/PURGEDATA=1` ลบ exact data rootและเก็บ certificate.
- [ ] ใช้สอง flagsร่วมกันลบทั้งสองเมื่อ explicit เท่านั้น.
- [ ] Data root/reparse-point negative fixtureถูก reject; unrelated registry/filesไม่ถูกแตะ.

## 7. Observation และ sign-off

- [ ] Engineering pilot 2–3 เครื่องผ่าน.
- [ ] Limited-user pilot พร้อม ZIP fallback/retained previous versionผ่าน observation window.
- [ ] Installer/Agent logsไม่มี credential, phone number, raw Origin, call/command ID หรือ private key.
- [ ] SmartScreen/AV behaviorตรง unsigned risk acceptance หรือ signed policyของ releaseนี้.
- [ ] Existing P2/P5 Windows/PBX/soak gatesได้รับการอ้างอิงและไม่ได้ถูกปิดด้วย installer evidenceเพียงอย่างเดียว.

ผล: [ ] PASS  [ ] FAIL  [ ] BLOCKED

ข้อยกเว้น/owner/expiry: ________________________________________________________________
