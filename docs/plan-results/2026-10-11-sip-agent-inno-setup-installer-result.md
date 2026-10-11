# SIP Windows Agent Inno Setup installer implementation result

วันที่เริ่มดำเนินการ: 11/10/2026  
Repository scope: `sip-windows-agent` เท่านั้น  
Authority: [`2026-10-11-sip-agent-inno-setup-installer-plan.md`](../2026-10-11-sip-agent-inno-setup-installer-plan.md)  
สถานะ: **Source implementation complete — Windows/Inno/PBX evidence pending**

## Confirmed decisions

- Internal employee distribution.
- Abort upgrade/uninstallเมื่อมี active call; ไม่รอและไม่ตัดสาย.
- Silent installไม่เปิด Agent; first runเป็นการกระทำของผู้ใช้.
- Normal uninstallเก็บ user dataและ localhost certificate.
- Rollbackจำกัดเฉพาะ operator workflow.
- Per-user, non-elevated, offline Inno Setup 7.1.0 x64 installer.
- Fixed AppId: `{476433C5-E4DF-4CD6-9E42-29F01E4BC339}`.

## Baseline before implementation

- Existing P5 pipelineสร้าง unsigned self-contained multi-file ZIP, SBOM, manifestและ SHA-256.
- Existing Agentมี `--storage-preflight`, `--print-release-metadata` และ certificate maintenance commands.
- Existing single-instance pipeรองรับ activationแบบ one-wayเท่านั้น.
- Existing source production statusยังรอ Windows/PBX/release evidenceตาม README.
- Working treeมีเฉพาะ plan/README documentation changesของ installerงานนี้ก่อนเริ่ม source implementation.

## Implemented scope

- เพิ่ม versioned current-user-only named-pipe request/response, `--query-maintenance` และ `--prepare-update`
  exit 0/30/31/32/33 โดยคง legacy activation byteไว้.
- Coordinatorรวม active-call check + shutdownไว้ใน serialized atomic operation; active callคืน `active_call`
  โดยไม่ hangup. Idle pathใช้ bounded graceful shutdown, verify quiescent stateและ installerรอจน primary mutexถูกปล่อย.
- เพิ่ม Inno Setup 7.1.0 x64 scriptพร้อม fixed AppId, per-user fixed root, x64/OS gate, English/Thai messages,
  side-by-side version directory, downgrade/preflight/metadata gates, shortcut/startup ownershipและ safe uninstall choices.
- Fresh install fail-closedเมื่อพบ portable/unmanaged Agent process; successful activation retainเฉพาะ active + previous
  versionและ cleanupไม่ traverse reparse point.
- เพิ่ม manifest/hash-validating `build-installer.ps1`, clean-profile `verify-installer-windows.ps1` และ
  operator-only `invoke-installer-rollback.ps1`.
- P5 release pipeline build ZIP และ setupจาก publish directoryเดียวกัน, สร้าง checksums/installer manifest,
  รัน installer automation และ upload logs/evidenceด้วย `if: always()`.
- แก้ P5-to-P2 verifier forwarding regression: `verify-p2-windows.ps1` ใช้ named-parameter hashtable
  splatting เพื่อส่ง `Phase=p2` และ optional `OutputRoot`; array splattingเดิมส่ง literal `-Phase` เป็นค่า
  ของ `Phase` และทำให้ release verificationหยุดก่อนเริ่ม P2.
- อัปเดต README, Thai/English deployment guide และ manual Windows/PBX installer checklist.

## Evidence executed on this workspace

Environment: macOS arm64; Windows runtime, Inno compilerและ PBXไม่อยู่ใน environmentนี้.

```text
dotnet restore softphone-native-client.sln --locked-mode -p:EnableWindowsTargeting=true --disable-build-servers -m:1
PASS — all six projects restored from lock files

dotnet build softphone-native-client.sln -c Release -p:EnableWindowsTargeting=true --disable-build-servers -m:1
PASS — 0 warnings, 0 errors

dotnet test tests/DebtFlow.SipAgent.Core.Tests/DebtFlow.SipAgent.Core.Tests.csproj -c Release --no-build --disable-build-servers -m:1
PASS — 97/97

dotnet test tests/DebtFlow.SipAgent.Host.Tests/DebtFlow.SipAgent.Host.Tests.csproj ...
NOT RUNNABLE HERE — testhost requires Microsoft.WindowsDesktop.App 10.0.0, unavailable on macOS
```

Host/installer test source compileผ่านใน solution build. ห้ามเปลี่ยน Windows/Inno/PBX checklistเป็น PASSจาก
cross-buildหรือ source inspection; ต้องรัน P2/P5 และ manual matrixบน exact release commit.

Follow-up note: parameter-forwarding regressionใน P2 wrapperถูกแก้จาก array splattingเป็น hashtable splatting.
Environmentนี้ไม่มี `pwsh` และไม่ใช่ Windows จึงยังไม่ถือว่า P2/P5 Windows executionผ่าน; ต้อง rerunคำสั่ง P5
บน clean exact-tagged Windows commitเพื่อปิด evidence gate.

Windows P5 core-test logที่ได้รับภายหลังแสดง 37/97 ผ่านและ 60/97 ล้มระหว่าง fixture cleanupด้วย
`The process cannot access the file 'agent.db' because it is being used by another process.` Root causeคือ
Microsoft.Data.Sqlite connection poolingยัง retain file handleหลัง logical connection disposalบน Windows ขณะที่ store
ใช้ connectionเดียวตลอด process lifetimeและ preflightเป็น one-shot จึงไม่มีประโยชน์จาก pooling. Sourceแก้โดยกำหนด
`Pooling=false` ใน event storeและ storage preflight รวมถึง test database helpers. หลังแก้ cross-platform Release build
ผ่าน 0 warnings/0 errorsและ Core testsผ่าน 100/100 หลังเพิ่ม regression coverage; Windows P5 rerunยังเป็น pending gate.

Windows P5 host-test logที่ได้รับภายหลังพบ failuresอีก 6 กลุ่มและแก้ source/test contractดังนี้:

- Protocol writerเคย omit nullable positional-record members แต่ strict readerถือ constructor membersเหล่านั้นเป็น required;
  เปลี่ยน wire JSONให้เขียน explicit `null` และเพิ่ม command-result/durable-event round-trip regression tests.
- SQLite store disposalเคย throwเมื่อ cleanupซ้ำ; ทำ `DisposeAsync` ให้ idempotentและเพิ่ม regression test.
- Logger recovery testเปิดอ่าน active logด้วย incompatible Windows share mode; เปลี่ยน readerเป็น read/write/delete sharing.
- TLS fake certificateเคยผูกกับ ephemeral RSA ownerที่ถูก dispose; cloneผ่าน exportable ephemeral PKCS#12ก่อนคืนค่า.
- Diagnostic assertion mapค่า queue high-waterผิดตำแหน่ง; แก้ expected realtime valueจาก 6 เป็น 8.
- Unsupported-command testเคย deserialize protocol-level `ErrorPayload` เป็น `CommandResultPayload`; แก้ให้ตรวจ
  error envelope, correlation ID และไม่เกิด journal insertionตาม protocol contractจริง.

Sourceทั้งหมด compileผ่าน แต่ Host testsต้อง rerunบน Windows เพราะ workspaceนี้ไม่มี Windows Desktop runtime;
ดังนั้น Host/P5 DoDยังคง pendingจนกว่าจะมี Windows logใหม่ที่ผ่าน.

Windows rerunถัดมาผ่าน Host tests 98/100 และยืนยันว่า 15 failuresเดิมถูกปิด เหลือ test-harness portability
2 จุด: Kestrel test certificateสูญเสีย private-key ownerก่อน TLS handshakeบน Windows และ logger recovery test
อาศัย global telemetry signalที่อาจมาจาก parallel testพร้อม timeoutสั้นกว่า production retry budget. แก้โดย clone
test certificateผ่าน exportable ephemeral PKCS#12 และให้ logger testรอ provider-local failure healthพร้อม recovery budget
10 วินาที. Source compileผ่าน 0 warnings/0 errors; Windows rerunสำหรับ 2 testsนี้และ P5 gateยัง pending.

Windows rerunอีกครั้งยังได้ 98/100 และทำให้ตรวจพบ root causeที่ลึกกว่าเดิม. Standalone logger reproductionยืนยันว่า
writer recoveryทำงานและไฟล์มีข้อมูล แต่ `{OriginalFormat}` ถูก `IsSensitiveKey` จัดเป็น origin propertyเพราะ substring
`origin` ทำให้ safe message templateถูกแทนด้วย `[REDACTED]`; แก้ special-case framework keyแล้วปล่อยค่าผ่าน
`SafeLogSanitizer` ตามเดิม โดย property `Origin` จริงยังถูก redact. Standalone recoveryหลังแก้พบข้อความภายในประมาณ
1 วินาที. WebSocket security/session testไม่ได้มีหน้าที่ทดสอบ TLS และ test-only certificateยัง handshakeไม่เสถียรบน
Windows จึงแยก test concernให้ใช้ loopback HTTP/WS; certificate profile/manager testsและ installed-agent P5 smokeยัง
คง TLS coverage. Release buildผ่าน 0 warnings/0 errorsและ Core testsผ่าน 100/100; Windows Host/P5 rerunยัง pending.

P5 rerunผ่าน restore, P2 และ publishแล้วหยุดที่ release metadataด้วย strict-mode errorว่าไม่มี property `version`.
Root causeคือ Hostเป็น Windows `WinExe` และ `ConsoleSession.EnsureAttached` ไปแทน redirected standard streamsเมื่อ
PowerShellต้อง capture `--print-release-metadata`, ทำให้ callerได้ empty output. แก้ให้ ConsoleSessionรักษา redirected
stdout/stderr และเพิ่ม `read-agent-release-metadata.ps1` ซึ่งเรียก processด้วย `UseShellExecute=false`, redirectทั้งสอง
streams, wait for exit, reject stderr/empty/invalid JSON และตรวจ required schema propertiesก่อนคืน object. P5,
installer verificationและ rollbackใช้ helperเดียวกันเพื่อไม่ให้ behavior drift. Source buildผ่าน 0 warnings/0 errors;
Windows metadata/P5 rerunยัง pending.

P5 rerunถัดมาสามารถอ่าน metadataได้แล้ว แต่ fail ด้วย `release_metadata_mismatch:tag=v1.1.6`. Tag และค่า
`-p:Version=1.1.6` ที่ส่งให้ publishถูกต้อง; root causeคือ `Directory.Build.props` pin `AssemblyVersion`,
`FileVersion` และ `InformationalVersion` ไว้ที่ `1.0.0` ขณะที่ `AgentReleaseMetadata.Current()` อ่าน versionจาก
assembly จึงรายงาน `1.0.0` เสมอ. แก้ให้ทั้งสามค่าคำนวณจาก `$(Version)` และเพิ่มค่าจริงของ metadataทุก field
ใน mismatch error เพื่อให้วิเคราะห์ release failureรอบต่อไปได้ทันที. MSBuild property evaluationด้วย
`Version=1.1.6` ยืนยันผล `AssemblyVersion=1.1.6.0`, `FileVersion=1.1.6.0` และ
`InformationalVersion=1.1.6`; Windows P5 rerunบน clean commitและ semantic tagใหม่ยังเป็น pending gate.
Release cross-buildที่ส่ง `Version=1.1.6` ผ่าน 0 warnings/0 errors, generated Host assembly attributesเป็น
`AssemblyVersion=1.1.6.0` และ `FileVersion=1.1.6.0`, Core testsผ่าน 100/100 และ `git diff --check` ผ่าน.

P5 rerunถัดมาหยุดด้วย `inno_compiler_version_mismatch:expected=7.1.0 actual=0.0.0.0` แม้ติดตั้ง Inno Setup
7.1.0 แล้ว. Root causeคือ build scriptอ่าน Windows `ProductVersion` resourceของ `ISCC.exe` ซึ่งไม่ใช่
compiler engine version contractและคืน placeholder `0.0.0.0` ได้. แก้ให้เรียก canonical Inno 7.1
`ISCC.exe --version` ผ่าน redirected subprocess, จำกัดเวลา 10 วินาที, ตรวจ exit code/stderr/outputแบบ fail-closed
โดยตรวจ PE x64 และ Authenticode trustก่อน execute แล้วบันทึก versionที่ตรวจจริงลง installer manifest.
เพิ่ม host contract regression testเพื่อป้องกันการย้อนกลับไปอ่าน
`$iscc.VersionInfo.ProductVersion`. Release cross-buildผ่าน 0 warnings/0 errorsและ Core testsผ่าน 100/100;
Windows Host testsและ P5 rerunยังเป็น pending gates.

P5 rerunถัดมาเรียก Inno compilerได้แล้วแต่ `.iss` compileหยุดที่ `Duplicate identifier 'Lm'`.
Root causeคือ Pascal Script identifiersเป็น case-insensitive ทำให้ shorthand `LM`/`Lm` และ `RM`/`Rm`
ใน semantic-version comparatorชนกัน. เปลี่ยนเป็นชื่อเต็ม `LeftMajor`/`LeftMinor`/`LeftPatch` และ
`RightMajor`/`RightMinor`/`RightPatch`, ขยาย comparison branchesให้อ่านและตรวจได้ชัดเจน และเพิ่ม host contract
regression assertionสำหรับ collisionนี้; Windows Inno/P5 rerunยังเป็น pending gate.

## Deviations and follow-up

- Setup failureจาก `PrepareToInstall` ใช้ standard Inno non-zero exit 7; exit 30–33เป็น contractของ Agent
  `--prepare-update` และถูกแปลงเป็น localized actionable installer message/log ไม่ใช่ setup process exit code.
- Automated clean-profile testครอบคลุม fresh silent install, metadata, same-version repair, default-preserve uninstall,
  tampered Run valueและ explicit data purge. Real N-1 upgrade/downgrade/active-call/certificate/PBX casesอยู่ใน manual
  release qualification เพราะต้องมี signed-off previous artifact, running callและ CurrentUser certificate stores.
- Workflow pin package/versionผ่าน WinGetและ buildบันทึก SHA-256 ของ `ISCC.exe`; owner/legal commercial-license decision
  และ independent pinned installer digest/provenance evidenceยังต้องบันทึกใน release environment.

## Definition of Done tracking

### P0 — Contract และ documentation baseline

- [x] Confirmed owner decisionsถูกบันทึกก่อน source changes.
- [x] Fixed installer AppIdถูกสร้างและบันทึก.
- [x] Planครอบคลุม maintenance protocol, install/upgrade/uninstall/rollback, CI, testsและ rollout.
- [x] DoD result documentถูกสร้างก่อน implementation.

### P1 — Maintenance safety

- [x] Versioned current-user-only maintenance IPC implemented.
- [x] Activation behaviorเดิมถูก preserveและมี regression test source.
- [x] `--prepare-update` และ exit codes 0/30/31/32/33 implemented.
- [x] Active call blocks upgradeโดยไม่ hangupใน implemented path.
- [x] Idle shutdown uses bounded budgetและ waits for primary release.
- [x] IPC ambiguity/failure fails closed.
- [ ] Automated regression tests pass on Windows.

### P2 — Installer behavior

- [x] Inno Setup script uses fixed AppId, per-user scope, x64 OS gate and fixed install root.
- [x] Versioned side-by-side binary layout implemented.
- [x] Fresh install, same-version repair and N-1 upgrade behavior implemented.
- [x] Downgrade and storage-preflight failures block before activation.
- [x] Startup preference and owned Run value handling preserve user choice.
- [x] Normal uninstall preserves data/certificate.
- [x] Explicit certificate/data purge validates exact owned targets; data purge rejects reparse-point trees.
- [x] Operator-only rollback workflow implemented.

### P3 — Release pipeline และ artifacts

- [x] Installer consumes the same verified publish directory as portable ZIP in P5 source.
- [ ] Inno 7.1.0 x64 package/version is pinned and compiler version/hash recorded; release provenance/digest evidence pending.
- [x] Pipeline source produces setup EXE, SHA-256 and installer manifest.
- [x] Release workflow uploads installer artifacts and logs, including safe failure evidence.
- [x] Unsigned/signed state is validated and recorded truthfully.
- [x] P5-to-P2 wrapper forwards named parameters without treating `-Phase` as a positional value.
- [x] Publish version propagates consistently to assembly, file, informational and runtime release metadata sources.
- [x] Inno compiler engine version is read from canonical `ISCC.exe --version`, not placeholder PE product metadata.

### P4 — Automated verification

- [x] Locked restore and Release cross-build pass (macOS arm64, 0 warnings/errors).
- [x] Core tests pass (100/100), including protocol null round trips, idempotent store disposalและ
  atomic active-call refusal without hangup.
- [x] SQLite connections used by the store, preflight and database tests release file handles deterministically;
  Windows P5 rerun remains required to confirm the platform-specific regression.
- [ ] Windows Host tests pass.
- [ ] Installer fresh/repair/upgrade/uninstall automation passes on Windows.
- [ ] Manifest/hash/tampered-path negative tests pass.
- [x] `git diff --check` passes.

### P5 — Manual Windows/PBX/release evidence

- [ ] Windows 10 Enterprise LTSC x64 installer matrix passes.
- [ ] Windows 11 x64 installer matrix passes.
- [ ] Standard-user and non-elevated-admin flows pass.
- [ ] Thai/non-ASCII profile/path flow passes.
- [ ] Active inbound/outbound call blocks installer safely.
- [ ] Edge/Chrome and PBX inbound/outbound smoke passes after install and upgrade.
- [ ] Certificate preserve/remove and data preserve/purge matrices pass.
- [ ] Logs and evidence pass privacy review.

### Documentation closure

- [x] README reflects implemented source status and actual commands.
- [x] Thai/English release guide covers setup, silent deployment, uninstall and rollback.
- [x] Manual installer checklist is complete.
- [x] This result records exact evidence and remaining external gates.
- [x] Installer completion is not used to close unrelated production-readiness gates.
