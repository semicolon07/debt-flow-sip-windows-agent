# Debt Flow SIP Agent icon assets

- `DebtFlow.SipAgent.png` is the user-approved 1254×1254 RGBA master artwork.
- `DebtFlow.SipAgent.ico` contains 32-bit alpha frames at 16, 24, 32, 48, 64, 128 and 256 pixels.
- The executable embeds the ICO through `ApplicationIcon`; runtime tray and setup UI load the associated icon from
  the executable and do not require a loose deployed icon file.
- Inno Setup consumes the same ICO at compile time and the installer manifest records its SHA-256.

Approved master PNG SHA-256:

```text
20e60573fc3e07339905e8d45c3966328df1b8cf9e12453e59a8ffce33c7bde3
```
