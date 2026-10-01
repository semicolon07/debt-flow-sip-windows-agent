# P1 Windows/PBX verification result template

สร้างสำเนาไฟล์นี้ต่อหนึ่ง verification run. ห้ามบันทึก password, SIP Authorization, SDP,
เบอร์เต็ม, DTMF digit, raw SIP message หรือ unredacted log/screenshot

## 1. Run identity

| Field | Value |
| --- | --- |
| Result status | `INCOMPLETE` |
| Started at UTC | `<YYYY-MM-DDTHH:mm:ssZ>` |
| Completed at UTC | `<YYYY-MM-DDTHH:mm:ssZ or pending>` |
| Tester/owner | `<team or role>` |
| Git commit | `<full SHA>` |
| Git worktree clean | `<yes/no>` |
| Artifact file | `<debt-flow-sip-agent-win-x64.zip>` |
| Artifact SHA-256 | `<lowercase SHA-256>` |
| Automated evidence directory | `<artifacts/p1-windows/run-id>` |
| Manual evidence directory | `<redacted relative path>` |

Result status เปลี่ยนเป็น `PASS` ได้เมื่อทุก mandatory case เป็น PASS, ไม่มี FAIL/SKIPPED,
artifact hash ตรงกับ automated evidence และ sign-off ครบเท่านั้น

## 2. Environment

| Field | Value |
| --- | --- |
| Windows edition/version/build | `<value>` |
| Process architecture | `<value>` |
| User type | `standard user` |
| UAC prompt observed | `<yes/no>` |
| Windows UI culture(s) | `<th-TH/en-US>` |
| PBX product/version | `<safe value>` |
| SIP transport tested | `UDP` |
| PBX address family | `<IPv4/IPv6/both>` |
| Agent origin | `<scheme + host + port only>` |
| Microphone class | `<generic/redacted>` |
| Speaker class | `<generic/redacted>` |
| Relevant environment notes | `<safe notes>` |

## 3. Automated gate

| Case | Result | Evidence reference | Safe notes |
| --- | --- | --- | --- |
| AUTO-01 | `NOT_RUN` | `<relative path>` | `<exit/test totals>` |
| AUTO-02 | `NOT_RUN` | `<relative path>` | `<TRX/report/ZIP/evidence JSON present>` |

## 4. Tray/non-admin gate

| Case | Result | Evidence reference | Safe notes |
| --- | --- | --- | --- |
| TRAY-01 | `NOT_RUN` |  |  |
| TRAY-02 | `NOT_RUN` |  |  |
| TRAY-03 | `NOT_RUN` |  |  |
| TRAY-04 | `NOT_RUN` |  |  |
| TRAY-05 | `NOT_RUN` |  |  |
| TRAY-06 | `NOT_RUN` |  |  |
| TRAY-07 | `NOT_RUN` |  |  |

## 5. WebSocket V1 gate

| Case | Result | Evidence reference | Safe notes |
| --- | --- | --- | --- |
| WS-01 | `NOT_RUN` |  |  |
| WS-02 | `NOT_RUN` |  |  |
| WS-03 | `NOT_RUN` |  |  |
| WS-04 | `NOT_RUN` |  |  |
| WS-05 | `NOT_RUN` |  |  |
| WS-06 | `NOT_RUN` |  |  |
| WS-07 | `NOT_RUN` |  |  |

## 6. PBX/audio gate

| Case | Result | Evidence reference | Safe notes |
| --- | --- | --- | --- |
| PBX-01 | `NOT_RUN` |  |  |
| PBX-02 | `NOT_RUN` |  |  |
| PBX-03 | `NOT_RUN` |  |  |
| PBX-04 | `NOT_RUN` |  |  |
| PBX-05 | `NOT_RUN` |  |  |
| PBX-06 | `NOT_RUN` |  |  |
| PBX-07 | `NOT_RUN` |  |  |
| PBX-08 | `NOT_RUN` |  |  |
| PBX-09 | `NOT_RUN` |  |  |
| PBX-10 | `NOT_RUN` |  |  |
| PBX-11 | `NOT_RUN` |  |  |

## 7. Privacy/evidence gate

| Case | Result | Evidence reference | Safe notes |
| --- | --- | --- | --- |
| SEC-01 | `NOT_RUN` |  |  |
| SEC-02 | `NOT_RUN` |  |  |
| SEC-03 | `NOT_RUN` |  |  |

Allowed result values: `PASS`, `FAIL`, `SKIPPED`, `NOT_RUN`.
ทุก FAIL/SKIPPED/NOT_RUN ต้องระบุเหตุผลและทำให้ run status เป็น `INCOMPLETE` หรือ `FAIL`

## 8. Findings and remaining gates

| Finding ID | Severity | Case | Description | Owner | Disposition |
| --- | --- | --- | --- | --- | --- |
| `<P1-001>` | `<critical/high/medium/low>` | `<case ID>` | `<safe description>` | `<owner>` | `<open/fixed/accepted>` |

- Remaining gates: `<list or none>`
- Redaction review completed by: `<role>`
- Evidence integrity reviewed by: `<role>`

## 9. Sign-off

| Role | Name/team | Decision | Timestamp UTC |
| --- | --- | --- | --- |
| Desktop/telephony engineering |  | `<approve/reject>` |  |
| QA |  | `<approve/reject>` |  |
| Security/privacy reviewer |  | `<approve/reject>` |  |

Final declaration: `<P1 complete / P1 remains pending>`
