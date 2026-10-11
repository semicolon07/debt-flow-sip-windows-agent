# SIP Windows Agent Inno Setup installer implementation plan

วันที่จัดทำ: 11/10/2026  
Repository scope: `sip-windows-agent` เท่านั้น  
สถานะ: **Confirmed plan implemented in source — Windows/Inno/PBX DoD evidence pending**  
Target toolchain: **Inno Setup 7.1.0 x64**, `.NET 10`, `win-x64`, per-user installation

Confirmed owner decisions 11/10/2026:

- กลุ่มเป้าหมายรุ่นแรกคือพนักงานภายใน.
- เมื่อมี active call ให้ abort setup/uninstall และให้ผู้ใช้ลองใหม่หลังจบสาย.
- silent installไม่เปิด Agent; ผู้ใช้เปิดครั้งแรกเอง.
- normal uninstallเก็บทั้ง user dataและ localhost certificate.
- rollbackเป็น operator-only workflowและไม่มีเมนูสำหรับผู้ใช้ทั่วไป.

Implementation/evidence statusหลังดำเนินการอยู่ที่
[`plan-results/2026-10-11-sip-agent-inno-setup-installer-result.md`](plan-results/2026-10-11-sip-agent-inno-setup-installer-result.md).
หัวข้อ baseline ด้านล่างบันทึกสภาพ repository ณ เวลาก่อนเริ่ม implementationตามลำดับงานที่ ownerกำหนด.

## 1. บทสรุปการตัดสินใจ

รอบนี้จะเพิ่ม Windows installer แบบไฟล์เดียวด้วย Inno Setup โดยยังคง ZIP portable เป็น release และ
recovery artifact คู่กัน. Installer ต้องไม่เปลี่ยน protocol, SIP behavior, SQLite schema หรือ security model
ของ Agent และต้องไม่สร้าง certificate, credential หรือ configuration ที่ควรอยู่ภายใต้ consent/runtime ownership
ของ Agent.

แนวทางหลักมีดังนี้:

1. ใช้ Inno Setup 7.1.0 รุ่น x64 และ pin compiler version ใน release pipeline.
2. ติดตั้งแบบ per-user โดยไม่ขอ Administrator/UAC.
3. ใช้ install root คงที่ `%LOCALAPPDATA%\Programs\DebtFlow\SipAgent`.
4. เก็บ binary แต่ละ release แบบ side-by-side ใต้ `versions\<semver>` และ activate รุ่นใหม่หลัง copy สำเร็จ.
5. เก็บ application data เดิมที่ `%LOCALAPPDATA%\DebtFlow\SipAgent` แยกจาก program filesตลอดเวลา.
6. เพิ่ม maintenance handshake ระหว่าง installer กับ Agent เพื่อ query state และ shutdown แบบ graceful.
7. ห้าม force-kill Agent เป็น normal path โดยเฉพาะเมื่อมี active call.
8. รัน `--storage-preflight` หลัง Agent หยุดและก่อน activate binary รุ่นใหม่.
9. Block downgrade โดย default; rollback ใช้ previous retained version และ compatibility procedure ที่ระบุชัด.
10. Installer, ZIP, SBOM, manifests และ checksums ต้องสร้างจาก verified publish directory ชุดเดียวกัน.
11. Authenticode signing เป็น optional external gate ในระยะแรก; unsigned status ต้องถูกประกาศตรงไปตรงมา.
12. Installer release ไม่ได้ปิด Windows/PBX/soak gates ที่ยัง pending ใน production-readiness plan เดิม.

## 2. เหตุผลที่เลือก Inno Setup และข้อกำกับด้าน license

Inno Setup รองรับ single-EXE installer, non-administrative install, silent mode, uninstall, multilingual UI,
command-line compiler และ Pascal scripting ซึ่งเหมาะกับ per-user WinForms tray application นี้. Inno Setup 7
มี x64 compiler/installer และ extended-path support; release ที่ pin ในแผนนี้คือ 7.1.0.

เอกสารอ้างอิงหลัก:

- Inno Setup 7 downloads และ current version: <https://jrsoftware.org/isdl.php>
- Compiler command line: <https://jrsoftware.org/ishelp/topic_compilercmdline.htm>
- Non-administrative install mode: <https://jrsoftware.org/ishelp/topic_admininstallmode.htm>
- `PrepareToInstall`: <https://jrsoftware.org/ishelp/topic_scriptevents.htm>
- Silent install switches: <https://jrsoftware.org/ishelp/topic_setupcmdline.htm>
- `AppId` upgrade identity: <https://jrsoftware.org/ishelp/topic_setup_appid.htm>
- Code signing integration: <https://jrsoftware.org/ishelp/topic_setup_signtool.htm>

Inno Setup เป็น open-source และ installer ที่สร้างโดยไม่มี commercial key ไม่ถูกทำเครื่องหมายว่า unlicensed,
แต่เจ้าของโครงการร้องขอให้ commercial users ซื้อ license. ก่อน production distribution ต้องบันทึก owner/legal
decision ว่าจะใช้แบบไม่มีค่าใช้จ่ายต่อภายใต้เงื่อนไขปัจจุบัน หรือซื้อ commercial license ภายหลัง. ห้าม commit
license key ลง repository หรือ GitHub Actions secrets ที่ expose ให้ pull request.

อ้างอิง: <https://jrsoftware.org/isorder.php>

## 3. Baseline ปัจจุบัน

### 3.1 Release artifact

`scripts/verify-p5-release.ps1` ปัจจุบันทำสิ่งต่อไปนี้แล้ว:

- บังคับรันบน Windows.
- บังคับ clean exact semantic tag `v<major>.<minor>.<patch>`.
- รัน P2 verification ก่อน publish.
- publish self-contained multi-file `win-x64`.
- ตรวจ `--print-release-metadata` กับ version, RID, protocol และ SQLite schema.
- สร้าง SPDX SBOM และ `release-manifest.json` พร้อม file hashes.
- scan ไม่ให้มี private key/certificate material หรือ plaintext WebSocket endpoint.
- สร้าง deterministic ZIP และ SHA-256 checksum.

Installer ต้อง consume `package` directory ชุดนี้โดยตรง ห้าม publish source ซ้ำเป็นรอบที่สอง เพราะอาจได้
binary คนละชุดกับ ZIP, SBOM และ evidence.

### 3.2 Runtime และ user data

- Executable: `DebtFlow.SipAgent.Host.exe`.
- Runtime: `.NET 10` self-contained `win-x64`.
- Data root: `%LOCALAPPDATA%\DebtFlow\SipAgent`.
- SQLite: `agent-v1.db`, schema v4.
- Certificate stores: `CurrentUser\My` และ `CurrentUser\Root`.
- Startup: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DebtFlowSipAgent`.
- Startup preference: `HKCU\Software\DebtFlow\SipAgent`.
- Current upgrade preflight: `--storage-preflight`, exits `0`, `20` หรือ `21`.
- Current metadata probe: `--print-release-metadata`.
- Current certificate removal: `--remove-local-certificate`.

### 3.3 Gap ที่ต้องปิดก่อน installer production gate

`SingleInstanceCoordinator` ปัจจุบันรองรับ activation signal แบบ one-byte และไม่มี response. Maintenance command
จะถูกปฏิเสธด้วย `agent_already_running` เมื่อ primary instance ทำงานอยู่. Installer จึงยังไม่สามารถ:

- ตรวจว่า Agent idle หรือมี active call.
- ขอ shutdown แบบ graceful.
- รอผล shutdown อย่างมี timeout.
- แยก active-call refusal ออกจาก IPC failure.
- ทำ unattended upgrade อย่างปลอดภัย.

Gap นี้เป็น P0 prerequisite; ห้ามแก้ด้วย `taskkill`, `Process.Kill`, Inno `force` close หรือ delayed replacement
หลัง reboot เป็น normal workflow.

## 4. เป้าหมายและ non-goals

### 4.1 เป้าหมาย

- ผู้ใช้ติดตั้ง/อัปเกรด/ถอน Agent ผ่าน EXE เดียวได้.
- การติดตั้งไม่ต้องใช้ Administrator และไม่เขียน HKLM.
- Upgrade ระหว่าง active call ถูก block โดยไม่ตัดสาย.
- Upgrade เมื่อ idle ทำ graceful shutdown และ storage preflight ก่อนสลับรุ่น.
- Failure ก่อน activation ไม่ทำลายรุ่นเดิม.
- Config, SQLite, logs, audio preferences และ certificate ownership ไม่สูญหายระหว่าง upgrade.
- Startup preference เดิมคงอยู่และ path ถูก retarget อย่างปลอดภัย.
- Uninstall ปกติลบ program files/shortcuts/startup entry แต่เก็บ user data.
- Clean uninstall ต้องเป็น explicit choice และลบเฉพาะ path/certificate ที่ Agent เป็น owner.
- Interactive และ silent behavior มี contract และ exit behavior ที่ทดสอบได้.
- Release artifacts มี checksum, manifest, compiler provenance และ evidence.

### 4.2 Non-goals

- ไม่สร้าง Windows Service, driver, firewall rule หรือ machine-wide installation.
- ไม่เปลี่ยน SIP/WebSocket Protocol V1.
- ไม่เพิ่ม SQLite migration เพียงเพื่อ installer.
- ไม่ฝัง SIP credential, Portal credential หรือ private certificate ใน installer.
- ไม่ติดตั้ง .NET Runtime เพิ่ม เพราะ package เป็น self-contained.
- ไม่ทำ online/bootstrap installer; installer ต้องทำงานแบบ offline.
- ไม่ทำ automatic background updater ในรอบนี้.
- ไม่รองรับ x86 หรือ ARM64 native artifact ในรอบนี้.
- ไม่อ้างว่า Authenticode trust/SmartScreen ถูกแก้ หาก artifact ยัง unsigned.

## 5. Target filesystem และ registry layout

```text
%LOCALAPPDATA%\Programs\DebtFlow\SipAgent\
├── versions\
│   ├── 1.0.0\
│   │   ├── DebtFlow.SipAgent.Host.exe
│   │   ├── release-manifest.json
│   │   ├── README.md
│   │   └── ... self-contained files
│   └── 1.1.0\
│       └── ...
└── installer-state.json          # optional; registry remains authority

%LOCALAPPDATA%\DebtFlow\SipAgent\ # runtime-owned, never under installer version dir
├── agent-v1.db
├── agent-v1.db-wal
├── agentsettings.json
├── audio-preferences.json
├── tls-certificate.json
├── Logs\
└── Diagnostics\
```

Installer state registry:

```text
HKCU\Software\DebtFlow\SipAgent\Installer
  InstallRoot       REG_SZ
  InstalledVersion  REG_SZ
  ActivePath        REG_SZ
  PreviousVersion   REG_SZ (optional)
  PreviousPath      REG_SZ (optional)
  GitCommit         REG_SZ
  InstallerSchema   REG_DWORD = 1
```

Rules:

1. สร้าง `AppId` GUID หนึ่งค่าใน implementation แรกและห้ามเปลี่ยนใน release ถัดไป.
2. `AppId`, uninstall registry identity และ install root ต้องคงที่ข้าม version.
3. ห้ามเก็บ credential, phone number, Origin หรือ certificate private data ใน installer registry.
4. `ActivePath` และ `PreviousPath` ต้องอยู่ภายใต้ canonical install root เท่านั้น.
5. Version directory ต้องมาจาก normalized three-part semantic version ห้ามรับ path separator.
6. เก็บอย่างน้อย active version และ previous version; ลบ version เก่ากว่านั้นหลัง activation สำเร็จเท่านั้น.
7. ห้ามลบ runtime data root ใน normal install, repair, upgrade หรือ normal uninstall.

### เหตุผลที่ใช้ side-by-side version directories

Inno Setup ไม่ใช่ full transactional package manager และ installation error หลังบางขั้นอาจไม่สามารถ restore ทุกอย่าง
กลับแบบ atomic ได้. การ copy binary รุ่นใหม่ไป directory ใหม่ก่อนสลับ pointer ทำให้:

- รุ่นเดิมไม่ถูกเขียนทับระหว่าง copy.
- startup path ยังชี้รุ่นเดิมจนกว่าจะผ่าน preparation gates.
- installation failure ก่อน activation สามารถหยุดโดยไม่แตะรุ่นเดิม.
- มี N-1 binary สำหรับ controlled rollback.
- ลดโอกาส mixed-version DLL set ของ self-contained multi-file publish.

## 6. Inno Setup configuration contract

ไฟล์หลักที่วางแผนเพิ่ม: `installer/DebtFlow.SipAgent.iss`.

ค่าหลักที่ต้องกำหนด:

```ini
[Setup]
AppId={{476433C5-E4DF-4CD6-9E42-29F01E4BC339}
AppName=Debt Flow SIP Agent
AppPublisher=Debt Flow
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\DebtFlow\SipAgent
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
SetupArchitecture=x64
ArchitecturesAllowed=x64os
MinVersion=10.0.17763
WizardStyle=modern dynamic
Uninstallable=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
ChangesEnvironment=no
OutputBaseFilename=debt-flow-sip-agent-{#AppVersion}-win-x64-setup
```

หมายเหตุ:

- ใช้ `ArchitecturesAllowed=x64os` ให้ตรงกับ supported release manifest ปัจจุบัน ไม่ขยาย support ไป ARM64
  emulation โดยไม่มี evidence.
- ใช้ `PrivilegesRequired=lowest` และไม่ให้ command line override เป็น admin mode.
- ปิด generic close/restart behavior เพราะ SIP Agent ต้องผ่าน maintenance handshake.
- ห้ามใช้ `restartreplace`; locked binary คือ gate failure ไม่ใช่เหตุผลให้เปลี่ยนไฟล์หลัง reboot.
- `DefaultDirName` คงที่; production script ไม่เปิดให้ผู้ใช้เลือก directory เพื่อลด path ownership ambiguity.
- `UsePreviousAppDir` ต้องไม่ทำให้รับ legacy path นอก canonical root; validate และ override ใน `[Code]`.
- รองรับ English และ Thai installer UI; strings ของ custom messages ต้องอยู่ใน language-specific entries.
- Desktop shortcut เป็น optional task และ default off.
- Start Menu shortcut และ Add/Remove Programs entry ต้องสร้างเสมอ.
- ใช้ owner-approved multi-resolution ICO ชุดเดียวกันกับ Host executable, tray, setup EXE, shortcuts และ
  Add/Remove Programs; build manifestบันทึก icon SHA-256 เพื่อ trace branding input.
- Finish page launch checkbox default on สำหรับ interactive fresh install/upgrade และ `skipifsilent`.

## 7. Installer-to-Agent maintenance protocol

### 7.1 Design

ขยาย named-pipe protocol เดิมโดยรักษา activation behavior:

```text
Installer/secondary CLI
  -> current-user-only named pipe
  -> versioned bounded request
  -> primary Agent UI context
  -> AgentCoordinator snapshot/shutdown
  -> bounded response
```

ข้อกำหนด:

- ใช้ `PipeOptions.CurrentUserOnly` ต่อไป.
- request/response มี protocol version, command และ status; payload จำกัดขนาดเล็ก เช่นไม่เกิน 4 KiB.
- มี connect/read/write timeout ทุกช่วง.
- ไม่ส่ง credential, call ID, phone number หรือ detailed runtime snapshot.
- รองรับ command เดิม `activate`.
- เพิ่ม `query-maintenance` และ `prepare-update`.
- unknown version/command ต้อง fail closed.
- server loop ต้องทน malformed/aborted client และรับ request ถัดไปได้.
- callback ต้อง marshal เข้า WinForms UI context และไม่ทำ mutable coordinator workจาก pipe thread.

Response status ขั้นต่ำ:

| Status | ความหมาย | Installer action |
| --- | --- | --- |
| `ready` | ไม่มี primary instance หรือ shutdown สำเร็จ | ไป preflight |
| `active_call` | มีสายที่ยังไม่ terminal | abort; ให้ผู้ใช้จบสายก่อน |
| `shutdown_in_progress` | กำลังหยุด | wait bounded แล้ว query ซ้ำ |
| `shutdown_failed` | cleanup ไม่จบใน budget | abort; เก็บ log/evidence |
| `protocol_unsupported` | Agent เก่าไม่รองรับ handshake | interactive manual-exit path เท่านั้น |
| `ipc_unavailable` | connect/read/write ล้มเหลว | abort; ห้ามเดาว่า Agentหยุดแล้ว |

### 7.2 CLI contract

เพิ่มคำสั่ง:

```text
DebtFlow.SipAgent.Host.exe --prepare-update
```

Exit code proposal:

| Exit | Code | ความหมาย |
| ---: | --- | --- |
| 0 | `update_ready` / `already_stopped` | ไปต่อได้ |
| 2 | `maintenance_arguments_invalid` | command invalid |
| 30 | `update_blocked_active_call` | ผู้ใช้ต้องจบสาย |
| 31 | `update_shutdown_failed` | shutdownไม่ครบใน budget |
| 32 | `update_ipc_unavailable` | primary stateไม่แน่นอน |
| 33 | `update_protocol_unsupported` | legacy/manual-exit required |

Command ต้องรอจน primary mutex/process resources ถูกปล่อย หรือ timeout หลัง coordinator shutdown budgetพร้อม
small grace period. Success ห้ามหมายถึงเพียง “ส่ง request สำเร็จ”.

### 7.3 Legacy first-install transition

Portable build เก่าที่ไม่มี handshake อาจกำลังทำงานจาก directory อื่น. Installer ต้อง:

1. ตรวจ owned mutex/process state.
2. ถ้า protocol ไม่รองรับ ให้ interactive installerแสดงข้อความให้ Exit จาก tray แล้วกด Retry.
3. Silent installer ต้อง abort ด้วย non-zero exit; ห้ามเปิด message loopรอไม่สิ้นสุด.
4. ห้าม kill portable process.
5. หลัง process หยุดจึงรัน preflightจาก executable รุ่นที่ติดตั้งอยู่หรือ target packageตาม compatibility rule.

## 8. Installation lifecycle

### 8.1 Fresh install

1. Validate OS >= Windows 10 build 17763 และ x64 OS.
2. Validate canonical install/data paths.
3. Validate embedded build version และ manifest version ตรงกัน.
4. Copy package ไป `versions\<target-version>`.
5. ตรวจ executable exists และ `release-manifest.json` exists.
6. Run `--print-release-metadata` จาก installed target; version/RID/protocol/schema ต้องตรง build defines.
7. เขียน installer registry state.
8. สร้าง Start Menu shortcut และ optional desktop shortcut.
9. Interactive: ให้ผู้ใช้เลือก launch หลัง finish.
10. Silent: ไม่ launch โดย default; IT ต้อง launchเองหรือใช้ documented opt-in task/parameter.
11. เมื่อ Agent รันครั้งแรก ให้ Agent เองจัดการ origin setup, TLS consent/certificate และ startup preference.

### 8.2 Upgrade

1. อ่าน installed/active version จาก owned registry และ validate path ownership.
2. เปรียบเทียบ semantic version:
   - target > active: upgrade.
   - target == active: repair mode.
   - target < active: block downgrade.
3. เรียก installed active executable `--prepare-update`.
4. หาก active call ให้ abort ก่อน installation phase; ห้ามเปลี่ยนไฟล์/registry.
5. หาก shutdown success ให้รัน active executable `--storage-preflight`.
6. ไปต่อเฉพาะ exit `0`; exit `20/21` ต้อง abortและแสดง actionable code.
7. Copy target package ไป version directory ใหม่.
8. Run target `--print-release-metadata` และ validate.
9. Atomically update installer registry pointers (`Previous*` ก่อน `Active*`).
10. Retarget Start Menu/desktop shortcut.
11. หาก userเปิด Start with Windows อยู่ ให้ retargetเฉพาะ owned Run valueไป target path:
    - ห้าม enable startup หาก preferenceปิดอยู่.
    - ห้าม overwrite Run valueที่ไม่ได้ชี้ owned Agent executable.
12. Launch targetเมื่อ interactive userเลือก.
13. เก็บ previous version; pruneเฉพาะ versionที่เก่ากว่า N-1 หลัง pointer updateสำเร็จ.

### 8.3 Repair

- อนุญาต target versionเท่ากับ active version.
- ยังต้อง prepare-update และ storage preflight.
- Re-copy filesจาก verified embedded package.
- ไม่ reset preferences, user data, certificate หรือ origin config.
- Recreate missing owned shortcuts/uninstall metadata.
- ไม่สร้าง startup preferenceใหม่ หากผู้ใช้เคยปิด.

### 8.4 Downgrade และ rollback

Automatic downgrade ถูก block เพราะ target binaryเก่าอาจไม่เข้าใจ SQLite schema/configใหม่.

Controlled rollback ทำได้เมื่อ:

1. previous version directory ยังครบและ manifest hashผ่าน.
2. current/previous manifest ระบุ protocolและSQLite schema compatible.
3. Agent current version shutdownแบบ graceful.
4. previous executable `--storage-preflight` คืน exit `0`.
5. operator retarget `ActivePath`, shortcuts และ owned Run valueแบบ atomic.
6. launch previous versionและทำ smoke test.

ต้องเพิ่ม `scripts/invoke-installer-rollback.ps1` สำหรับ operator workflow โดย scriptต้อง:

- รับ explicit install root หรือ resolveจาก exact owned registry key.
- reject pathนอก canonical per-user root.
- ไม่ใช้ wildcard destructive delete.
- log version/from/to, exit codes และ timestamps แต่ไม่ log sensitive data.
- หยุดทันทีเมื่อ schema/protocol/preflightไม่ compatible.
- ไม่ rollback user data.

Rollback scriptไม่ถูกเรียกอัตโนมัติจาก setup failureหลัง target appเริ่มทำงาน. ก่อน app activation หาก installer
ล้มเหลว pointerเดิมต้องยังไม่ถูกเปลี่ยน จึงไม่ต้อง rollback runtime state.

## 9. Uninstall contract

### 9.1 Normal uninstall

- ใช้ maintenance handshake และ blockเมื่อ active call.
- ลบ shortcuts, installer registry, uninstaller และทุก version directoryที่เป็น owned path.
- ลบ owned `HKCU\...\Run\DebtFlowSipAgent` value.
- เก็บ `HKCU\Software\DebtFlow\SipAgent` runtime preferences โดย default.
- เก็บ `%LOCALAPPDATA%\DebtFlow\SipAgent` ทั้งหมดโดย default.
- ไม่ลบ certificateโดยเงียบ.
- ไม่ลบ path parent เช่น `%LOCALAPPDATA%\Programs\DebtFlow` หรือ `%LOCALAPPDATA%\DebtFlow` แบบ recursive.

### 9.2 Certificate removal

Interactive uninstall ต้องถามแยกว่าจะถอน localhost certificateหรือไม่ โดยอธิบายว่าเป็น certificateที่ Agent
สร้างและ trustใน CurrentUser stores. เมื่อผู้ใช้ยืนยัน:

1. Agent ต้องหยุดแล้ว.
2. เรียก active/retained executable `--remove-local-certificate` ก่อนลบ binary.
3. ไปต่อเมื่อ exit `0`; failure ต้องแสดง codeและไม่อ้างว่าถอน certificateสำเร็จ.

Silent uninstall ต้อง preserve certificate เว้นแต่ใช้ documented explicit parameter เช่น
`/PURGECERT=1`. Parameter นี้ต้องไม่รวม `/PURGEDATA` โดยปริยาย.

### 9.3 User-data purge

Interactive uninstall อาจเสนอ “ลบข้อมูล Agent ภายในเครื่อง” เป็น separate destructive choice และ default off.
Silent uninstallต้องใช้ `/PURGEDATA=1` แบบ explicit.

ก่อน purge ต้อง validate exact expanded pathเท่ากับ `%LOCALAPPDATA%\DebtFlow\SipAgent`, ไม่เป็น empty/root,
ไม่เป็น reparse point และอยู่ภายใต้ expected current-user profile. Purge failureต้องรายงานและเก็บ pathไว้ ไม่พยายาม
ขยาย scopeการลบ.

## 10. Security และ privacy requirements

1. Installer embedded filesต้องมาจาก P5 verified packageเท่านั้น.
2. Build scriptต้อง verify `release-manifest.json` hashesก่อนเรียก ISCC.
3. Installerต้อง offline; ห้าม download executable/runtimeระหว่าง install.
4. ห้าม include `.pfx`, `.p12`, `.pem`, `.key`, SIP credentialหรือ production config.
5. ห้าม installerสร้าง/trust certificateแทน Agent consent flow.
6. Custom installer logsห้ามมี environment dump, phone number, credential, raw Originหรือ certificate private data.
7. Named pipeใช้ current-user-only, bounded messageและtimeouts.
8. Registry/pathจากเครื่องผู้ใช้ถือเป็น untrusted inputและต้อง canonicalizeก่อนใช้.
9. ห้าม follow reparse pointในการ cleanup/purge.
10. Silent installห้าม auto-accept TLS consent.
11. Installer checksumใช้ SHA-256 และ release artifact manifestระบุ unsigned/signed stateตามจริง.
12. Authenticode signingในอนาคตต้อง sign:
    - `DebtFlow.SipAgent.Host.exe` และ shipped executableที่เกี่ยวข้อง.
    - setup EXE.
    - uninstallerผ่าน `SignTool`/`SignedUninstaller`.
13. Self-signed Agent localhost certificateไม่ใช่ Authenticode certificateและห้ามนำมาใช้ sign installer.
14. หากยัง unsigned ต้องคง documented risk acceptanceและห้าม claimว่า SmartScreen warningหมดไป.

## 11. Versioning และ artifact contract

Source of version truthยังเป็น exact Git tag `vX.Y.Z`.

Build-time definesขั้นต่ำ:

```text
AppVersion=X.Y.Z
GitCommit=<40-hex SHA>
PackageDirectory=<absolute verified directory>
OutputDirectory=<absolute release run directory>
ExpectedProtocolVersion=1
ExpectedSqliteSchemaVersion=4
AuthenticodeSigned=false|true
```

Release outputs:

```text
debt-flow-sip-agent-X.Y.Z-win-x64.zip
debt-flow-sip-agent-X.Y.Z-win-x64.zip.sha256
debt-flow-sip-agent-X.Y.Z-win-x64-setup.exe
debt-flow-sip-agent-X.Y.Z-win-x64-setup.exe.sha256
installer-manifest.json
verification\...
logs\publish.log
logs\sbom.log
logs\inno-compile.log
logs\installer-verify.log
```

`installer-manifest.json` ต้องมี:

- schema version.
- application version.
- Git commit/tag.
- Inno Setup version/architecture.
- setup filename, bytes และ SHA-256.
- source package manifest SHA-256.
- protocol/SQLite schema/RID.
- install scope `current-user`.
- minimum OS.
- signed stateของ setup/uninstaller/app.
- build timestamp UTC.

## 12. Planned files และ ownership

| File | Action | Responsibility |
| --- | --- | --- |
| `installer/DebtFlow.SipAgent.iss` | Add | Setup/uninstall lifecycle, UI, version/path gates |
| `installer/languages/Thai.isl` | Add only if custom strings cannot use built-in language entries | Thai custom installer messages |
| `scripts/build-installer.ps1` | Add | Validate package/compiler and invoke ISCC |
| `scripts/verify-installer-windows.ps1` | Add | Automated install/repair/uninstall checks |
| `scripts/invoke-installer-rollback.ps1` | Add | Controlled N-1 rollback for operators |
| `Host/SingleInstanceCoordinator.cs` | Modify | Versioned request/response maintenance IPC |
| `Host/AgentCommandLine.cs` | Modify | `--prepare-update` contract |
| `Host/TrayApplicationContext.cs` | Modify | UI-thread maintenance shutdown without prompt |
| `Host/TlsRecoveryApplicationContext.cs` | Modify | Maintenance shutdown while degraded |
| `Program.cs` | Modify | Maintenance client dispatch and exit codes |
| `Resources/TrayStrings*.resx` | Modify | User-facing maintenance/update messages if needed |
| `tests/DebtFlow.SipAgent.Host.Tests/*` | Modify/Add | IPC, CLI, timeout and state regression tests |
| `scripts/verify-p5-release.ps1` | Modify | Build installer from verified package |
| `.github/workflows/release.yml` | Modify | Pin/provision Inno and upload installer artifacts |
| `release/README-th-en.md` | Modify | Installer install/upgrade/uninstall/rollback guide |
| `README.md` | Modify after implementation | Build/use status and evidence links |
| `docs/manual-verification/installer-windows-checklist.md` | Add | Windows installer manual matrix |
| `docs/plan-results/2026-10-11-sip-agent-inno-setup-installer-result.md` | Add during implementation | Actual evidence, deviations and DoD closure |

Implementationต้องตรวจ file ownershipกับ working treeก่อนแก้ และห้าม overwrite unrelated user changes.

## 13. Build pipeline design

### 13.1 `build-installer.ps1`

Scriptต้อง:

1. รันได้เฉพาะ Windows.
2. เปิด strict modeและ stop on error.
3. รับ explicit package/output/version/commit; ห้ามเดาจาก broad glob.
4. validate semantic versionและ 40-hex commit.
5. resolve canonical pathsและ reject packageนอก release run rootเมื่อเรียกจาก P5.
6. parse release manifestและเทียบ version/RID/protocol/schema.
7. recompute SHA-256ทุก package fileและ reject missing/extra fileตาม policy.
8. locate `ISCC.exe` จาก explicit parameterหรือ pinned install path.
9. verify PE x64 และ Authenticode trustก่อน execute จากนั้น verify compiler engine versionจาก canonical
   `ISCC.exe --version` เท่ากับ `7.1.0`; ห้ามใช้ PE `ProductVersion` placeholder และ mismatchคือ failure.
10. invoke ISCCด้วย `--define` valuesและ capture log.
11. verify setup filename/version resource/size/hash.
12. หาก signed build ให้ verify Authenticode status/publisher/timestamp.
13. หาก unsigned build ให้ assert manifestระบุ `authenticodeSigned=false`.
14. เขียน checksumและ installer manifestแบบ UTF-8/ASCIIตาม contract.

### 13.2 P5 integration

ปรับลำดับ `verify-p5-release.ps1` เป็น:

```text
P2 gates
  -> dotnet publish once
  -> metadata validation
  -> README + SBOM
  -> release manifest + forbidden-material scan
  -> portable ZIP + checksum
  -> build Inno installer from same package directory
  -> installer structural verification + checksum/manifest
  -> release summary
```

หาก installer build fail ให้ release job failทั้งหมด แต่ package/log/evidenceที่สร้างก่อนหน้าควรถูกเก็บเป็น diagnostic
artifactโดย CIเมื่อทำได้ และห้าม publish releaseบางส่วน.

### 13.3 CI provisioning

- Pin Inno Setup 7.1.0 x64.
- Downloadจาก official release sourceหรือใช้ pre-provisioned release runner.
- Verify installer Authenticode publisherและ pinned SHA-256ก่อนติดตั้ง toolchain.
- ห้ามใช้ unpinned `latest` ใน release job.
- Cacheได้เฉพาะ artifactที่ผ่าน hash verification.
- PR CIไม่รับ signing secret.
- Release signingในอนาคตใช้ protected environmentและไม่ expose certificate/private keyให้ fork PR.

## 14. Test strategy

### 14.1 Unit tests

เพิ่ม deterministic testsสำหรับ:

- parse/serialize maintenance request/response.
- unknown version/command/payload too large.
- partial read, disconnect, timeoutและ malformed request.
- activation command backward behavior.
- `prepare-update` เมื่อไม่มี primary returns ready.
- idle primaryทำ shutdownและตอบ successหลัง shutdownจริง.
- active call returns exit 30โดยไม่เรียก shutdown/hangup.
- shutdown timeout/failure returns exit 31.
- IPC unavailable returns exit 32.
- concurrent activation/update requestsไม่ race mutable state.
- pipe serverรับ requestถัดไปหลัง client failure.
- CLI argument exclusivityกับ maintenance commandsเดิม.
- no sensitive fieldsใน maintenance response/log.

### 14.2 Automated Windows installer tests

`verify-installer-windows.ps1` ต้องรันด้วย dedicated clean Windows user profile เท่านั้น เพราะ Inno contractใช้
canonical current-user pathsจริง. Read-only profile preflightต้องรันก่อน P2/build, ระบุ conflict IDsและ evidence
โดยไม่ลบ ย้าย หรือ backup/restore program data, registry, shortcut หรือ processของ profileนั้น แล้วจึงครอบคลุม:

1. Silent fresh install.
2. Filesอยู่ใต้ expected version directoryเท่านั้น.
3. Add/Remove Programs entryเป็น per-user.
4. ไม่มี UAC/admin requirement.
5. Installed metadataตรง tag/manifest.
6. Start Menu shortcut targetถูกต้อง.
7. Optional desktop task behavior.
8. Same-version repair.
9. Higher-version upgrade fixture.
10. Downgrade rejectionก่อน mutation.
11. User data sentinelคงอยู่หลัง repair/upgrade/normal uninstall.
12. Startup preference disabledยัง disabledหลัง upgrade.
13. Enabled owned Run valueถูก retarget.
14. Unrelated/tampered Run valueไม่ถูก overwriteหรือลบ.
15. Active-call simulation blocks installก่อน file copy.
16. Preflight exit 20/21 blocks installก่อน pointer switch.
17. Corrupt/mismatched release manifest blocks installer build.
18. Normal uninstallลบ program filesและ owned startup value.
19. Silent uninstall preserves certificate/dataโดย default.
20. Explicit purge deletesเฉพาะ exact owned paths.
21. Installer/uninstaller return non-zeroและ log actionable codeเมื่อ gate fail.

Testsต้อง cleanupเฉพาะ test AppId/pathที่สร้างไว้และต้อง failหาก target path resolutionไม่ตรง expected sandbox.

### 14.3 Manual Windows matrix

อย่างน้อย:

| Matrix | Cases |
| --- | --- |
| OS | Windows 10 Enterprise LTSC x64, Windows 11 x64 |
| User | Standard user, local adminแต่ติดตั้ง non-elevated |
| Path/profile | English username, Thai/non-ASCII username, spaces/long path |
| Install | Fresh interactive, fresh silent, repair |
| Upgrade | Idle tray, active outbound, ringing inbound, active inbound, TLS degraded tray |
| Storage | no DB, healthy schema v4, WAL present, exit 20 fixture, corrupt/unavailable exit 21 |
| Startup | never configured, enabled, disabled, tampered Run value |
| Certificate | absent, healthy, degraded, explicit remove, policy-blocked trust |
| Uninstall | preserve data, purge cert only, purge data only, full explicit purge |
| Browser/PBX | Edge + Chrome Portal smoke, inbound/outbound one callหลัง upgrade |
| Failure | disk full/copy failure, locked file, installer cancelก่อน install, power/process interruption simulation |

ห้ามทำ active-call testด้วย production number/credential. Evidenceต้อง redactตาม repository privacy rules.

### 14.4 Upgrade fixture policy

- Release qualificationต้องทดสอบ upgradeจาก production versionก่อนหน้าอย่างน้อยหนึ่งรุ่น.
- Previous installer/packageต้อง verify checksumก่อนใช้เป็น fixture.
- PR CIอาจใช้ synthetic N-1 fixture; release CI/manual gateต้องใช้ real previous release artifact.
- เมื่อ schema versionเปลี่ยนในอนาคต ต้องเพิ่ม N-1/N-2 compatibility matrixก่อน release.

## 15. Observability และ supportability

Installer logต้องมีเฉพาะ safe fields:

- installer version/tag/commit.
- Inno version.
- install modeและ OS architecture.
- from/to application version.
- maintenance/preflight symbolic resultและ numeric exit.
- phase timestamps/durations.
- file countและ aggregate bytes; ไม่ dump file contents.
- final outcomeและ setup exit code.

Default interactive logใช้ Inno standard logเมื่อ supportร้องขอ; automated deploymentใช้ explicit `/LOG=<path>`.
เอกสาร supportต้องบอกตำแหน่ง logและวิธีส่งโดยไม่รวม `%LOCALAPPDATA%\DebtFlow\SipAgent` ทั้ง directory.

Agent JSONL logเพิ่ม eventแบบ bounded/safe:

- `installer_update_requested`.
- `installer_update_blocked_active_call`.
- `installer_shutdown_started`.
- `installer_shutdown_completed`.
- `installer_shutdown_failed`.

ห้าม log call ID, command ID, phone numberหรือ raw exception message.

## 16. Implementation phases และ gates

### Phase 0 — Contract freeze และ characterization

- ยืนยัน scope, path layout, AppId, version sourceและ exit codes.
- เพิ่ม failing testsสำหรับ maintenance IPC/CLI.
- บันทึก Inno 7.1.0 toolchain provenanceและ commercial-license decision owner.
- ยืนยัน current portable release artifactเป็น baseline.

Gate P0: testsแสดง gapเดิมได้, contractไม่มี ambiguity และไม่มี source behaviorเปลี่ยนโดยยังไม่ทดสอบ.

### Phase 1 — Graceful maintenance handshake

- Implement versioned named-pipe request/response.
- Implement `--prepare-update`.
- Wire trayและTLS recovery contexts.
- Preserve activation command.
- เพิ่ม timeout/concurrency/malformed-client tests.

Gate P1: active callถูก block, idle shutdown bounded, no force-kill, all Host/Core testsผ่านบน Windows.

### Phase 2 — Inno installer core

- เพิ่ม `.iss`, English/Thai stringsและ fixed AppId.
- Implement per-user versioned layout.
- Implement fresh install/repair/upgrade/downgrade gates.
- Implement shortcuts, startup retargetและuninstall choices.
- เพิ่ม package/manifest validation functions.

Gate P2: fresh install, repair, normal uninstallและfailure-before-activationผ่านบน clean Windows VM.

### Phase 3 — Release pipeline และ automated verification

- เพิ่ม build/verify scripts.
- Integrate P5 publish once -> ZIP + setup.
- Pin/provision Inno compilerใน release workflow.
- Upload setup/checksum/manifest/log evidence.
- เพิ่ม CI safety checksสำหรับ unsigned/signed modes.

Gate P3: exact clean tagสร้าง ZIPและsetupจาก packageชุดเดียวกัน พร้อม hashesและprovenanceครบ.

### Phase 4 — Upgrade, rollback และ destructive-path qualification

- ทดสอบ real N-1 -> N upgrade.
- Implement/verify controlled rollback script.
- ทดสอบ active-call refusal, preflight 20/21 และ locked-file behavior.
- ทดสอบ certificate/data preserve/purge ทุก combination.
- ทำ path traversal/reparse/tampered-registry negative tests.

Gate P4: upgrade/rollbackไม่ทำข้อมูลหาย, destructive actionsจำกัด exact owned targets และ evidenceครบ.

### Phase 5 — Windows/PBX acceptance และ documentation closure

- รัน manual installer matrixบน supported Windows.
- ทำ Edge/Chrome + inbound/outbound PBX smokeหลัง fresh installและupgrade.
- อัปเดต README/release guide/manual checklist/result document.
- ตรวจ DoDและระบุ exceptions/pending gatesตาม evidenceจริง.

Gate P5: installerพร้อม releaseได้เมื่อ installer DoDผ่าน; การประกาศ Agent production-readyยังต้องผ่าน
production-readiness/PBX/soak gatesเดิมทั้งหมด.

## 17. Rollout plan

1. Internal lab: fresh install/repair/uninstallบน clean VM.
2. Engineering pilot: 2–3 เครื่องที่ไม่มี production calls.
3. PBX sandbox pilot: inbound/outboundและactive-call upgrade block.
4. Limited user pilot: กลุ่มเล็กหนึ่ง collection พร้อม ZIP fallbackและ previous version retained.
5. Staged rollout: 10% -> 25% -> 50% -> 100% หลัง observation windowแต่ละช่วง.

Pause/rollback triggers:

- Agentเปิดไม่ได้หลัง install.
- unexpected certificate prompt/ownership failureบน existing user.
- config/SQLite/startup preferenceหาย.
- incoming/outgoing call regression.
- Agentถูกตัดระหว่าง active callจาก installer.
- storage preflightถูก bypass.
- mixed-version DLLหรือ release metadata mismatch.
- uninstallลบ user dataโดยไม่ได้เลือก purge.
- SmartScreen/AV behaviorต่างจาก accepted unsigned/signed release policyอย่างมีนัยสำคัญ.

## 18. Definition of Done checklist

### Toolchain และ source

- [ ] Pin Inno Setup 7.1.0 x64 และ verify provenance/digestใน release environment.
- [ ] Fixed `AppId` ถูก commitและมี regression guardไม่ให้เปลี่ยน.
- [ ] `.iss` compileแบบ warnings-as-reviewed; ไม่มี ignored unexpected warnings.
- [ ] Installerใช้ verified publish directoryเดียวกับ ZIP.
- [ ] ไม่มี certificate/private key/credentialฝังใน setup.
- [ ] Owner/legal decisionเรื่อง Inno commercial licenseถูกบันทึกโดยไม่มี keyใน repo.

### Maintenance safety

- [ ] Versioned current-user-only maintenance IPC implemented.
- [ ] Activation behaviorเดิมไม่ regression.
- [ ] `--prepare-update` exit contract implemented/documented.
- [ ] Active call blocks upgrade/uninstallโดยไม่ hangup.
- [ ] Idle Agent shutdownครบภายใน bounded budget.
- [ ] IPC ambiguity/failure fail closed.
- [ ] Installerไม่มี normal-path force kill/reboot replacement.

### Install/upgrade/repair

- [ ] Fresh per-user installผ่านโดยไม่ UAC.
- [ ] OS/architecture gatesตรง supported manifest.
- [ ] Versioned side-by-side layoutทำงาน.
- [ ] Metadata/manifest validationผ่านก่อน activation.
- [ ] Same-version repair preserve state.
- [ ] N-1 upgrade preserve config/DB/log/audio/certificate/startup preference.
- [ ] Downgradeถูก blockก่อน mutation.
- [ ] Preflight exit 20/21ถูก blockและแสดง actionable result.
- [ ] Failureก่อน pointer switchยังเปิดรุ่นเดิมได้.
- [ ] Startup retargetแตะเฉพาะ owned enabled value.

### Rollback/uninstall

- [ ] Previous version retainedและcontrolled rollbackผ่านเมื่อ compatible.
- [ ] Rollback script reject unsafe path/schema/protocol.
- [ ] Normal uninstall preserve user dataและcertificateตาม contract.
- [ ] Certificate purgeต้อง explicitและเรียก Agent-owned removal.
- [ ] Data purgeต้อง explicit, exact-path validatedและไม่ follow reparse point.
- [ ] Owned startup value/shortcuts/program filesถูกลบครบ.
- [ ] Unrelated registry/filesไม่ถูกแตะ.

### Automation และ artifacts

- [ ] P5 releaseสร้าง ZIP + setup + checksums + manifests.
- [ ] Installer manifestบันทึก Inno version, tag, commit, hashesและsigned state.
- [ ] CI upload installer logs/evidenceแม้ gate failเมื่อปลอดภัย.
- [ ] Silent install/repair/uninstall contractsทดสอบและเอกสารครบ.
- [ ] Real previous release upgrade fixtureผ่านก่อน production rollout.

### Windows/PBX evidence

- [ ] Windows 10 Enterprise LTSC x64 fresh/upgrade/uninstallผ่าน.
- [ ] Windows 11 x64 fresh/upgrade/uninstallผ่าน.
- [ ] Standard-userและnon-elevated-admin flowsผ่าน.
- [ ] Thai/non-ASCII profile/pathผ่าน.
- [ ] Active inbound/outbound call block behaviorผ่าน.
- [ ] TLS degraded/certificate preserve/remove flowsผ่าน.
- [ ] Edge/Chrome Portal reconnectหลัง upgradeผ่าน.
- [ ] PBX inbound/outbound smokeหลัง fresh installและupgradeผ่าน.
- [ ] Logs/evidenceผ่าน privacy review.

### Documentation และ release decision

- [ ] `README.md` อัปเดต install/upgrade/uninstall commandsและstatusตามจริง.
- [ ] `release/README-th-en.md` อัปเดต installer + portable fallback.
- [ ] Manual checklistและimplementation resultถูกสร้าง.
- [ ] Known limitationsระบุ unsigned/SmartScreen, supported OS/archและrollback limits.
- [ ] ไม่มีการประกาศ Agent production-readyจาก installer evidenceเพียงอย่างเดียว.
- [ ] Existing post-hardening Windows/PBX/release gatesถูก cross-referenceและไม่ถูกปิดโดยไม่มี evidence.

## 19. Acceptance commands ที่คาดหวัง

ตัวอย่าง commandsหลัง implement; ชื่อ/parametersสุดท้ายต้องตรง scriptsจริง:

```powershell
./scripts/verify-p2-windows.ps1
./scripts/verify-installer-windows.ps1 -InstallerPath <setup.exe>
./scripts/verify-p5-release.ps1 -OutputRoot artifacts/p5-release
```

Silent install:

```powershell
./debt-flow-sip-agent-1.0.0-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="installer.log"
```

Silent uninstall default-preserve:

```powershell
& "$env:LOCALAPPDATA\Programs\DebtFlow\SipAgent\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="uninstall.log"
```

คำสั่ง purge certificate/data ต้องถูก documentแยกและไม่ควรปรากฏใน default deployment command.

## 20. Completion rule

แผนนี้ถือว่า implementedเมื่อ source changes, installer scripts, automated tests, Windows installer matrix,
upgrade/rollback evidence, documentation และ DoDที่เกี่ยวข้องผ่านบน exact release commitเดียวกันเท่านั้น.

การ compile setupสำเร็จหรือทดลองติดตั้งหนึ่งเครื่องไม่เพียงพอ. หากบาง gateต้องใช้ production signing,
Windows/PBX environment หรือ owner/legal decisionที่ยังไม่มี ต้องระบุเป็น pending/accepted exceptionพร้อม owner,
เหตุผล, compensating controlและวันหมดอายุ; ห้ามเปลี่ยนเป็น PASSจาก source inspection.
