# ผล implementation: SIP hostname สำหรับ Multi-profile Windows Agent

วันที่: 27/10/2026

## ขอบเขตที่ implement

- คง WebSocket protocol V1และ `ConfigureCommand` shapeเดิม.
- ยอมรับ canonical IPv4 literalหรือ ASCII hostname/FQDN; normalize hostnameเป็น lowercaseและตัด terminal dot.
- Reject IPv6, IPv6 zone/bracket, URL, embedded port, non-ASCII, underscore/SRV-like nameและ invalid numeric host.
- Resolveเฉพาะ IPv4 Aใหม่ทุก registration/retry แล้วใช้ resolved IPv4กับ registrar, outbound callและ inbound peer check.
- AAAA-only/no-A fail closedและเข้าสู่ registration retry policyเดิม.

## หลักฐานอัตโนมัติ

```text
dotnet test tests/DebtFlow.SipAgent.Core.Tests/DebtFlow.SipAgent.Core.Tests.csproj --no-restore --no-build
# Passed 114

dotnet build softphone-native-client.sln --no-restore -p:EnableWindowsTargeting=true
# Build succeeded, 0 warnings, 0 errors
```

Host test projectและ hostname test source compileผ่าน แต่รันบน macOSไม่ได้เพราะไม่มี
`Microsoft.WindowsDesktop.App 10.0`; targeted Host test executionต้องทำบน Windows.

## Gateที่ยังค้าง

- รัน Host tests, installer/release verificationและ manual registration/reconnectบน Windowsจริง.
- ทดสอบ DNS A rotation, NXDOMAIN/timeout, AAAA-onlyและ Issabel/Asteriskทั้ง UDP/TCP.
