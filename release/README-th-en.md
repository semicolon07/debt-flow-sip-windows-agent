# Debt Flow SIP Agent 1.0.0

## ภาษาไทย

ชุดนี้เป็น portable แบบ self-contained สำหรับ Windows x64 ไม่ต้องติดตั้ง .NET Runtime/SDK เพิ่มเติม

1. ปิด Debt Flow SIP Agent เวอร์ชันเดิมจาก tray icon
2. แตก ZIP ไปยังโฟลเดอร์เวอร์ชันใหม่ ห้ามรันจากใน ZIP
3. เปิด `DebtFlow.SipAgent.Host.exe` จากโฟลเดอร์ใหม่
4. ตรวจว่า tray icon แสดงสถานะพร้อมใช้งานและทดสอบโทรหนึ่งสาย
5. เมื่อยืนยันแล้วจึงลบโฟลเดอร์โปรแกรมเวอร์ชันเดิม

ข้อมูลตั้งค่า, SQLite outbox และ log อยู่ที่ `%LOCALAPPDATA%\DebtFlow\SipAgent` และไม่ถูกลบเมื่อเปลี่ยนโฟลเดอร์โปรแกรม หากต้อง rollback รุ่นแรก ให้ปิด Phone ของ Collection จาก Admin แล้ว Exit Agent โดยเก็บโฟลเดอร์ AppData ไว้

ก่อนส่งไฟล์ให้ผู้ใช้ ให้ตรวจ SHA-256 กับไฟล์ `.sha256` ทุกครั้ง ชุด 1.0.0 ยังไม่มี Authenticode signature ตามข้อยกเว้นความเสี่ยงที่ได้รับอนุมัติ

## English

This is a self-contained portable Windows x64 package. The target computer does not need a separate .NET Runtime or SDK.

1. Exit the previous Debt Flow SIP Agent from its tray icon.
2. Extract the ZIP into a new versioned folder; do not run it inside the ZIP.
3. Start `DebtFlow.SipAgent.Host.exe` from the new folder.
4. Confirm the tray status is ready and complete one test call.
5. Delete the old program folder only after the check succeeds.

Configuration, the SQLite outbox, and logs remain under `%LOCALAPPDATA%\DebtFlow\SipAgent`. For the first 1.0.0 rollback, disable Phone for the Collection in Admin and exit the Agent while preserving AppData.

Verify the SHA-256 checksum before distribution. Version 1.0.0 is intentionally unsigned under the approved production risk exception.
