# Legacy PoC notes

เอกสารนี้ถูกแทนที่ด้วย [README.md](README.md).
Legacy `{ action }` / `{ eventType, message }` protocol ถูกถอดออกใน P0 และไม่มี compatibility mode

พฤติกรรม PoC ที่รักษาไว้ผ่าน V1 adapter:

- SIP registration/unregistration
- outbound และ inbound call
- answer/reject/cancel/hangup
- DTMF โดยไม่บันทึก digit
- microphone silence fallback และ no-speaker fallback

raw SDP/debug output และ default PBX credentials ถูกถอดออกตาม production privacy baseline

P1 เปลี่ยนค่าเริ่มต้นเป็น tray application. การใช้ diagnostic page ต้องเริ่ม Agent ด้วย
`dotnet run --project softphone-native-client.csproj -- --console`; development Origins และ
`--allowed-origin` ใช้ได้เฉพาะ console mode
