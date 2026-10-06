# SIP Windows Agent meticulous hardening plan and result

วันที่: 07/10/2026  
ฐานการตรวจ: `cc9fe5c30277b8debbd69a1467f63f9f3a45a0d1`  
สถานะ: **Source implementation complete — Windows/PBX evidence pending**

## Confirmed plan

รอบนี้ใช้ invariant ต่อไปนี้เป็นเกณฑ์ก่อนแก้ source:

1. audio inventory enumeration และ device probe ต้องไม่ทำงานบน coordinator snapshot/tray hot path
2. audio preference/test ต้อง reserve idle state จน operation จบ เพื่อไม่ชน inbound/outbound call
3. cache ทุกตัวต้องมี bounded staleness, explicit invalidation และ failure fallback
4. storage capacity preflight ต้องเห็น mutation จาก durable event, active-call journal และ command journal
5. failure หลัง preflight ต้อง rollback in-memory call state ก่อนคืน error
6. logger sink failure ต้องไม่ฆ่า writer task แบบเงียบและต้องมี bounded retry/observable metric
7. performance evidence ต้องเห็น resource growth และ runtime/custom metrics โดยไม่ใส่ identifier ลง log

ไม่มีการเปลี่ยน protocol V1, Agent version 1.0.0, SQLite schema version 4, durability mode
`synchronous=FULL` หรือ approved security exceptions ใน phase นี้.

## Implementation result

| Area | Implementation | Automated evidence |
| --- | --- | --- |
| Audio operation race | coordinator reserve audio preference/test แบบ serialized; outbound ถูกปฏิเสธด้วย `audio_device_busy` และ inbound ตอบ unavailable ระหว่าง reservation | core regression ครอบคลุม inbound/outbound crossover และยืนยันว่า reservation ถูก release |
| Audio snapshot hot path | `AudioDevices` คืน immutable cached snapshot; refresh แบบ background ทุก 30 วินาทีเมื่อ notification ใช้ไม่ได้ และ Windows notification ยัง debounce 500 ms | Release host compilation |
| Audio cache staleness | working playback cache มี positive TTL 5 นาที, negative TTL 10 วินาที, generation invalidation และ retry เมื่อ endpoint create ล้ม | Release host compilation; real device matrix pending |
| Audio degradation | endpoint initialization refresh inventory แล้ว fallback เป็น capture-only, playback-only หรือ no-device media แทนล้มทั้ง SIP operation เมื่อทำได้ | Release host compilation; Windows device-removal evidence pending |
| Device identity | duplicate WinMM capability identifiers ได้ deterministic occurrence suffix เพื่อไม่ส่ง duplicate ID ใน snapshot เดียวกัน | Release host compilation |
| Storage health | command save/recovery/prune ทำ storage cache dirty; health refreshes DB+WAL bytes ก่อน capacity decision | core regression ด้วย command result 512 KiB |
| Oldest pending | cache ใช้ timestamp ที่เก่าจริง ไม่ผูกกับ sequence แรก และ SQL recovery ใช้ `MIN`; timestamp persist เป็น UTC canonical form | core regression ด้วย out-of-order time และต่าง UTC offset |
| Call rollback | outbound/inbound ล้าง in-memory call/handle เมื่อ durable append คืน capacity หรือ store error ก่อน SIP side effect | core regression สำหรับ `outbox_capacity_critical` |
| Logger resilience | writer เก็บ pending line ขณะ sink fail, reopen directory/file ด้วย exponential backoff 100 ms–5 s และ shutdown ยัง bounded | Windows host regression compile pass; execution pending |
| Logger observability | เพิ่ม `sip_agent.logging.writer_failures` counter ที่ tag เฉพาะ bounded exception type; meter version derive จาก assembly | Windows host regression compile pass |
| Performance evidence | sampler schema v2 เพิ่ม thread/handle delta, working/private memory growth และ `-CollectRuntimeCounters` สำหรับ `System.Runtime,DebtFlow.SipAgent` | PowerShell execution pending on Windows |

## Verification

| Check | Result |
| --- | --- |
| Locked restore with all NuGet advisories | PASS; all projects up-to-date, no advisory warning |
| Release cross-build (`EnableWindowsTargeting=true`) | PASS, 0 warnings, 0 errors |
| Core tests | PASS, 86/86 |
| Windows host tests | COMPILE PASS; cannot execute on macOS without `Microsoft.WindowsDesktop.App` |
| `dotnet format whitespace --verify-no-changes` | PASS with existing workspace-load warning |
| Git diff whitespace | PASS |

## Residual release gates

Source work ในแผนนี้เสร็จแล้ว แต่ยังห้ามเปลี่ยนสถานะเป็น production ready จนกว่าจะมี evidence ต่อไปนี้:

- รัน Windows host tests รวม logger recovery test บน Windows Desktop runtime
- ทดสอบ default device, explicit device, duplicate-label device, Bluetooth disconnect/return และ unplug ระหว่าง call
- fault-inject log directory read-only/disk-full และยืนยันว่า call control ไม่ crash พร้อม metric เพิ่ม
- รัน sampler schema v2 พร้อม `-CollectRuntimeCounters` ใน idle, active call และ 2-hour/50-call soak
- ทำ PBX inbound/outbound crossover ระหว่าง audio test และยืนยัน 480/command error ตาม policy
- ผ่าน release gates เดิมเรื่อง PBX matrix, privacy, migration rehearsal, allow-all WSS sign-off และ rollout/kill switch
