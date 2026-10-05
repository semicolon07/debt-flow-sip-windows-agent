# P2 Windows/PBX hardening and soak checklist

สถานะ: **pending execution on Windows/PBX Sandbox**

ใช้ checklist นี้หลัง commit source แล้วและก่อนประกาศ P2 complete. ห้ามกรอก PASS จาก cross-build,
source inspection หรือผล P1 เดิมแทนการทดสอบจริง.

## Automated gate

จาก clean worktree บน Windows 10 1809+ x64 ที่มี .NET 10 SDK:

```powershell
./scripts/verify-p2-windows.ps1
```

ต้องได้ Release build 0 warning/error, core + Windows host testsอย่างน้อย 109 casesผ่านทั้งหมด,
vulnerability count 0, self-contained multi-file ZIP, SHA-256 และ `p2-windows-evidence.json`.

## PBX and failure matrix

| ID | Scenario | Expected | Result/evidence |
| --- | --- | --- | --- |
| P2-TLS-01 | clean standard-user profile + first launch | consentครั้งเดียว, My/Root/CNG metadataครบและ WSS trusted | pending |
| P2-TLS-02 | HTTPS Portalใน Edge/Chrome + HTTP localhost PoC | ทั้งสองเชื่อม WSS; plaintext WSล้มเหลว | pending |
| P2-TLS-03 | enterprise policyบล็อก CurrentUser Root | degraded recovery tray; ไม่มี listener/SIP; safe error | pending |
| P2-TLS-04 | restart/reuse, <=30-day rotationและ rotation failure | reuse/rotateถูกต้อง; valid-old fallbackมี warning | pending |
| P2-TLS-05 | Repair และ Removeจาก tray/CLI | ownership guard, key/store/metadata cleanup; config/DB/logคงอยู่ | pending |
| P2-SIP-01 | UDP register + outbound/inbound/audio/DTMF | P1 behavior ยังผ่าน; terminal ต่อ call หนึ่งรายการ | pending |
| P2-SIP-02 | UDP no-response/transport failure | state `retrying`, jittered retry และเปลี่ยนไป TCP | pending |
| P2-SIP-03 | TCP register + outbound/inbound/audio/DTMF | ใช้งานได้โดยไม่เพิ่ม Admin transport field | pending |
| P2-SIP-04 | SIP auth/policy failure | state `failed`; ไม่ fallback/retry loop | pending |
| P2-SIP-05 | reconfigure/unregister ระหว่าง retry | timer/callback เก่าไม่ register ซ้ำ | pending |
| P2-DB-01 | restart ระหว่าง active call | ได้ `call.ended`/`agent_restarted` ครั้งเดียวและ replay ต่อ sequence เดิม | pending |
| P2-DB-02 | ACK ลด backlog จาก critical | Agent recover เป็น ready; ไม่มี event gap | pending |
| P2-DB-03 | newer/corrupt/read-only/full DB | fail closed, safe code, ไฟล์เดิมไม่ถูกลบ/แทน | pending |
| P2-AUD-01 | startup ไม่มี capture/playback | signaling ทำงาน; audio degraded; ไม่มี crash | pending |
| P2-AUD-02 | ถอด default device ระหว่างสาย | state degraded; SIP callไม่ถูกตัด; resource cleanupได้ | pending |
| P2-AUD-03 | ต่อ device ใหม่ | inventory refresh และสายถัดไปใช้ default ปัจจุบัน | pending |
| P2-RTP-01 | direct PBX กับ `AcceptRtpFromAny=false` test build | บันทึกผลรับ/ส่งเสียงและ source validation | pending |
| P2-RTP-02 | NAT/symmetric RTP matrix | ระบุนโยบายขั้นต่ำที่ providerต้องใช้และ threat residual | pending |
| P2-DIAG-01 | Export safe diagnostics | ZIP ไม่มี password/user/raw number/Origin/SIP header/SDP/DTMF/DB | pending |
| P2-SHUT-01 | exit/restart ระหว่าง retry/call/device callback | shutdown ≤10s, ไม่มี crash/double terminal | pending |

## Soak gate

- ระยะเวลาอย่างน้อย 2 ชั่วโมงและอย่างน้อย 50 sequential completed call cycles
- เก็บ baseline/ทุก 10 calls/ท้าย run: private bytes, handle, thread, socket และ DB/WAL bytes
- ต้องไม่มี duplicate dial, double terminal, sequence gap, credential/PII leak หรือ unbounded upward trend
- แนบ redacted CSV/log/evidence path, OS/PBX version, agent checksum และผู้ทดสอบ

P2 ปิดได้เมื่อทุก required row เป็น PASS, automated evidenceครบ และ residual risk
(`AcceptRtpFromAny`, PBX-specific TCP/NAT behavior) ได้รับ owner acceptance.
