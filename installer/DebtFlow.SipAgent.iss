#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1
#endif
#ifndef GitCommit
  #error GitCommit must be supplied by build-installer.ps1
#endif
#ifndef PackageDirectory
  #error PackageDirectory must be supplied by build-installer.ps1
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied by build-installer.ps1
#endif
#ifndef ExpectedProtocolVersion
  #define ExpectedProtocolVersion "1"
#endif
#ifndef ExpectedSqliteSchemaVersion
  #define ExpectedSqliteSchemaVersion "4"
#endif

#define AppExe "DebtFlow.SipAgent.Host.exe"

[Setup]
AppId={{476433C5-E4DF-4CD6-9E42-29F01E4BC339}
AppName=Debt Flow SIP Agent
AppPublisher=Debt Flow
AppVersion={#AppVersion}
AppVerName=Debt Flow SIP Agent {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Debt Flow SIP Agent
VersionInfoCompany=Debt Flow
DefaultDirName={localappdata}\Programs\DebtFlow\SipAgent
DisableDirPage=yes
DisableProgramGroupPage=yes
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
UsePreviousAppDir=no
OutputDir={#OutputDirectory}
OutputBaseFilename=debt-flow-sip-agent-{#AppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
SetupLogging=yes
UninstallDisplayIcon={app}\versions\{#AppVersion}\{#AppExe}
UninstallDisplayName=Debt Flow SIP Agent {#AppVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "th"; MessagesFile: "compiler:Languages\Thai.isl"

[CustomMessages]
en.ActiveCall=Debt Flow SIP Agent is handling a call. End the call and retry the installation.
th.ActiveCall=Debt Flow SIP Agent กำลังมีสายอยู่ กรุณาจบสายแล้วลองติดตั้งอีกครั้ง
en.ShutdownFailed=Debt Flow SIP Agent could not stop safely. No files were changed. Review the Agent log and retry.
th.ShutdownFailed=ไม่สามารถหยุด Debt Flow SIP Agent อย่างปลอดภัยได้ ยังไม่มีการเปลี่ยนไฟล์ กรุณาตรวจ log แล้วลองอีกครั้ง
en.LegacyAgent=The running Agent does not support safe installer maintenance. Exit it from the tray, then choose Retry.
th.LegacyAgent=Agent ที่กำลังทำงานไม่รองรับการบำรุงรักษาอย่างปลอดภัย กรุณา Exit จาก tray แล้วเลือก Retry
en.IpcUnavailable=The running Agent state could not be verified. Exit it from the tray, then choose Retry.
th.IpcUnavailable=ไม่สามารถยืนยันสถานะ Agent ที่กำลังทำงานได้ กรุณา Exit จาก tray แล้วเลือก Retry
en.PortableAgent=A portable or unmanaged Debt Flow SIP Agent is running. Exit it from the tray, then choose Retry.
th.PortableAgent=มี Debt Flow SIP Agent แบบ portable หรือรุ่นที่ installer ไม่ได้จัดการกำลังทำงาน กรุณา Exit จาก tray แล้วเลือก Retry
en.PreflightFailed=Agent storage preflight failed with exit code %1. No files were changed.
th.PreflightFailed=การตรวจพื้นที่จัดเก็บของ Agent ล้มเหลวด้วย exit code %1 ยังไม่มีการเปลี่ยนไฟล์
en.DowngradeBlocked=Downgrade from %1 to %2 is blocked. Use the operator rollback procedure.
th.DowngradeBlocked=ไม่อนุญาตให้ downgrade จาก %1 เป็น %2 กรุณาใช้ขั้นตอน rollback สำหรับ operator
en.InvalidInstallState=Existing installer state is invalid or points outside the owned install directory. Contact support.
th.InvalidInstallState=สถานะ installer เดิมไม่ถูกต้องหรือชี้ออกนอกโฟลเดอร์ที่ระบบเป็นเจ้าของ กรุณาติดต่อผู้ดูแล
en.MetadataFailed=The installed Agent package failed release metadata validation. Installation was rolled back.
th.MetadataFailed=ชุด Agent ที่ติดตั้งไม่ผ่านการตรวจ release metadata ระบบได้ยกเลิกการติดตั้ง
en.RemoveCertificate=Also remove the Agent-owned localhost certificate from the current user certificate stores?
th.RemoveCertificate=ต้องการลบ localhost certificate ที่ Agent สร้างออกจาก certificate store ของผู้ใช้ปัจจุบันด้วยหรือไม่
en.RemoveData=Also permanently delete all local Agent settings, logs, and queued events? This cannot be undone.
th.RemoveData=ต้องการลบการตั้งค่า log และเหตุการณ์ค้างส่งทั้งหมดอย่างถาวรด้วยหรือไม่ การดำเนินการนี้ย้อนกลับไม่ได้
en.PurgeFailed=The requested cleanup did not complete. The preserved data path is: %1
th.PurgeFailed=การล้างข้อมูลที่ร้องขอไม่สำเร็จ ข้อมูลยังคงอยู่ที่: %1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PackageDirectory}\*"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Debt Flow SIP Agent"; Filename: "{app}\versions\{#AppVersion}\{#AppExe}"; WorkingDir: "{app}\versions\{#AppVersion}"
Name: "{autodesktop}\Debt Flow SIP Agent"; Filename: "{app}\versions\{#AppVersion}\{#AppExe}"; WorkingDir: "{app}\versions\{#AppVersion}"; Tasks: desktopicon

[Run]
Filename: "{app}\versions\{#AppVersion}\{#AppExe}"; Description: "{cm:LaunchProgram,Debt Flow SIP Agent}"; Flags: nowait postinstall skipifsilent

[Code]
const
  InstallerStateKey = 'Software\DebtFlow\SipAgent\Installer';
  PreferenceKey = 'Software\DebtFlow\SipAgent';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'DebtFlowSipAgent';
  ExpectedVersion = '{#AppVersion}';
  ExpectedCommit = '{#GitCommit}';
  ExpectedProtocol = '{#ExpectedProtocolVersion}';
  ExpectedSchema = '{#ExpectedSqliteSchemaVersion}';
  UpdateActiveCall = 30;
  UpdateShutdownFailed = 31;
  UpdateIpcUnavailable = 32;
  UpdateProtocolUnsupported = 33;

var
  ExistingVersion: string;
  ExistingActivePath: string;
  ExistingPreviousPath: string;
  ExistingInstallRoot: string;
  HasExistingInstall: Boolean;
  PurgeCertificate: Boolean;
  PurgeData: Boolean;
  CustomExitCode: Integer;

function CanonicalInstallRoot: string;
begin
  Result := RemoveBackslashUnlessRoot(ExpandConstant('{localappdata}\Programs\DebtFlow\SipAgent'));
end;

function TargetDirectory: string;
begin
  Result := CanonicalInstallRoot + '\versions\' + ExpectedVersion;
end;

function TargetExecutable: string;
begin
  Result := TargetDirectory + '\{#AppExe}';
end;

function IsOwnedExecutable(const FileName: string): Boolean;
var
  RootPrefix: string;
begin
  RootPrefix := AddBackslash(CanonicalInstallRoot) + 'versions\';
  Result := (CompareText(Copy(FileName, 1, Length(RootPrefix)), RootPrefix) = 0) and
    (CompareText(ExtractFileName(FileName), '{#AppExe}') = 0) and
    (Pos('..', FileName) = 0);
end;

function ParseVersionPart(const Value: string; var Position: Integer; var Number: Integer): Boolean;
var
  Start: Integer;
  Part: string;
begin
  Start := Position;
  while (Position <= Length(Value)) and (Value[Position] >= '0') and (Value[Position] <= '9') do
    Position := Position + 1;
  Part := Copy(Value, Start, Position - Start);
  Number := StrToIntDef(Part, -1);
  Result := (Part <> '') and (Number >= 0);
end;

function ParseSemVer3(const Value: string; var Major, Minor, Patch: Integer): Boolean;
var
  P: Integer;
begin
  P := 1;
  Result := ParseVersionPart(Value, P, Major) and (P <= Length(Value)) and (Value[P] = '.');
  if not Result then Exit;
  P := P + 1;
  Result := ParseVersionPart(Value, P, Minor) and (P <= Length(Value)) and (Value[P] = '.');
  if not Result then Exit;
  P := P + 1;
  Result := ParseVersionPart(Value, P, Patch) and (P = Length(Value) + 1);
end;

function CompareSemVer3(const LeftValue, RightValue: string; var Valid: Boolean): Integer;
var
  LM, Lm, LP, RM, Rm, RP: Integer;
begin
  Valid := ParseSemVer3(LeftValue, LM, Lm, LP) and ParseSemVer3(RightValue, RM, Rm, RP);
  Result := 0;
  if not Valid then Exit;
  if LM <> RM then begin if LM < RM then Result := -1 else Result := 1; Exit; end;
  if Lm <> Rm then begin if Lm < Rm then Result := -1 else Result := 1; Exit; end;
  if LP <> RP then begin if LP < RP then Result := -1 else Result := 1; Exit; end;
end;

function LoadExistingState: Boolean;
begin
  HasExistingInstall := RegQueryStringValue(HKCU, InstallerStateKey, 'InstalledVersion', ExistingVersion);
  if not HasExistingInstall then begin
    Result := True;
    Exit;
  end;
  Result := RegQueryStringValue(HKCU, InstallerStateKey, 'InstallRoot', ExistingInstallRoot) and
    RegQueryStringValue(HKCU, InstallerStateKey, 'ActivePath', ExistingActivePath) and
    (CompareText(RemoveBackslashUnlessRoot(ExistingInstallRoot), CanonicalInstallRoot) = 0) and
    IsOwnedExecutable(ExistingActivePath) and FileExists(ExistingActivePath);
  if Result and RegQueryStringValue(HKCU, InstallerStateKey, 'PreviousPath', ExistingPreviousPath) then
    Result := IsOwnedExecutable(ExistingPreviousPath) and FileExists(ExistingPreviousPath);
end;

function RunAgent(const ExePath, Arguments: string; var ExitCode: Integer): Boolean;
begin
  Result := Exec(ExePath, Arguments, ExtractFileDir(ExePath), SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

function PrepareExistingAgent(const SilentMode: Boolean): string;
var
  ExitCode: Integer;
  Choice: Integer;
begin
  Result := '';
  if not HasExistingInstall then Exit;
  while True do begin
    if not RunAgent(ExistingActivePath, '--prepare-update', ExitCode) then begin
      CustomExitCode := UpdateIpcUnavailable;
      Result := CustomMessage('IpcUnavailable');
      Exit;
    end;
    if ExitCode = 0 then Break;
    CustomExitCode := ExitCode;
    if ExitCode = UpdateActiveCall then begin
      Result := CustomMessage('ActiveCall');
      Exit;
    end;
    if ExitCode = UpdateShutdownFailed then begin
      Result := CustomMessage('ShutdownFailed');
      Exit;
    end;
    if ExitCode = UpdateProtocolUnsupported then
      Result := CustomMessage('LegacyAgent')
    else
      Result := CustomMessage('IpcUnavailable');
    if SilentMode then Exit;
    Choice := SuppressibleMsgBox(Result, mbError, MB_RETRYCANCEL, IDRETRY);
    if Choice <> IDRETRY then Exit;
  end;

  if not RunAgent(ExistingActivePath, '--storage-preflight', ExitCode) then begin
    CustomExitCode := 21;
    Result := FmtMessage(CustomMessage('PreflightFailed'), ['21']);
    Exit;
  end;
  if ExitCode <> 0 then begin
    CustomExitCode := ExitCode;
    Result := FmtMessage(CustomMessage('PreflightFailed'), [IntToStr(ExitCode)]);
  end;
end;

function EnsureNoUnmanagedAgent(const SilentMode: Boolean): string;
var
  ExitCode, Choice: Integer;
  Output: TExecOutput;
  PowerShellPath, Arguments: string;
begin
  Result := '';
  if HasExistingInstall then Exit;
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Arguments := '-NoLogo -NoProfile -NonInteractive -Command "if (Get-Process -Name ''DebtFlow.SipAgent.Host'' -ErrorAction SilentlyContinue) { exit 30 } else { exit 0 }"';
  while True do begin
    try
      if not ExecAndCaptureOutput(PowerShellPath, Arguments, '', SW_SHOWNORMAL,
        ewWaitUntilTerminated, ExitCode, Output) then begin
        Result := CustomMessage('IpcUnavailable');
        Exit;
      end;
    except
      Result := CustomMessage('IpcUnavailable');
      Exit;
    end;
    if Output.Error then begin
      Result := CustomMessage('IpcUnavailable');
      Exit;
    end;
    if ExitCode = 0 then Exit;
    if ExitCode <> UpdateActiveCall then begin
      Result := CustomMessage('IpcUnavailable');
      Exit;
    end;
    Result := CustomMessage('PortableAgent');
    if SilentMode then Exit;
    Choice := SuppressibleMsgBox(Result, mbError, MB_RETRYCANCEL, IDRETRY);
    if Choice <> IDRETRY then Exit;
    Result := '';
  end;
end;

function InitializeSetup: Boolean;
var
  Comparison: Integer;
  Valid: Boolean;
begin
  CustomExitCode := 0;
  Result := LoadExistingState;
  if not Result then begin
    SuppressibleMsgBox(CustomMessage('InvalidInstallState'), mbCriticalError, MB_OK, IDOK);
    CustomExitCode := 10;
    Exit;
  end;
  if HasExistingInstall then begin
    Comparison := CompareSemVer3(ExpectedVersion, ExistingVersion, Valid);
    if (not Valid) or (Comparison < 0) then begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('DowngradeBlocked'), [ExistingVersion, ExpectedVersion]), mbCriticalError, MB_OK, IDOK);
      CustomExitCode := 11;
      Result := False;
      Exit;
    end;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  NeedsRestart := False;
  if HasExistingInstall then
    Result := PrepareExistingAgent(WizardSilent)
  else
    Result := EnsureNoUnmanagedAgent(WizardSilent);
end;

function MetadataMatchesTarget: Boolean;
var
  ExitCode: Integer;
  Output: TExecOutput;
  Content: string;
begin
  Result := False;
  try
    if not ExecAndCaptureOutput(TargetExecutable, '--print-release-metadata', TargetDirectory,
      SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode, Output) then Exit;
  except
    Log('Target release metadata capture failed');
    Exit;
  end;
  if (ExitCode <> 0) or Output.Error then Exit;
  Content := StringJoin('', Output.StdOut);
  Result := (Pos('"version":"' + ExpectedVersion + '"', Content) > 0) and
    (Pos('"protocolVersion":' + ExpectedProtocol, Content) > 0) and
    (Pos('"sqliteSchemaVersion":' + ExpectedSchema, Content) > 0) and
    (Pos('"runtimeIdentifier":"win-x64"', Content) > 0);
end;

procedure RetargetOwnedStartup;
var
  Configured, Enabled: Cardinal;
  ExistingRun, ExePath, Arguments: string;
  QuotePosition: Integer;
begin
  if not RegQueryDWordValue(HKCU, PreferenceKey, 'StartupConfigured', Configured) then Exit;
  if not RegQueryDWordValue(HKCU, PreferenceKey, 'StartWithWindows', Enabled) then Exit;
  if (Configured = 0) or (Enabled = 0) then Exit;
  if not RegQueryStringValue(HKCU, RunKey, RunValueName, ExistingRun) then Exit;
  if Pos('"', ExistingRun) <> 1 then Exit;
  Delete(ExistingRun, 1, 1);
  QuotePosition := Pos('"', ExistingRun);
  if QuotePosition <= 1 then Exit;
  ExePath := Copy(ExistingRun, 1, QuotePosition - 1);
  Arguments := Copy(ExistingRun, QuotePosition + 1, Length(ExistingRun));
  if (CompareText(Arguments, ' --background') <> 0) or (not IsOwnedExecutable(ExePath)) then Exit;
  RegWriteStringValue(HKCU, RunKey, RunValueName, '"' + TargetExecutable + '" --background');
end;

function ContainsReparsePoint(const DirectoryName: string): Boolean;
var
  FindRec: TFindRec;
  Child: string;
begin
  Result := False;
  if not FindFirst(AddBackslash(DirectoryName) + '*', FindRec) then Exit;
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
        Child := AddBackslash(DirectoryName) + FindRec.Name;
        if (FindRec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then begin
          Result := True;
          Exit;
        end;
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           ContainsReparsePoint(Child) then begin
          Result := True;
          Exit;
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function IsReparsePoint(const Path: string): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not FindFirst(Path, FindRec) then Exit;
  try
    Result := (FindRec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0;
  finally
    FindClose(FindRec);
  end;
end;

procedure PruneUnretainedVersions;
var
  FindRec: TFindRec;
  VersionsRoot, Candidate, RetainedPreviousDirectory: string;
begin
  VersionsRoot := CanonicalInstallRoot + '\versions';
  RetainedPreviousDirectory := '';
  if HasExistingInstall then begin
    if CompareText(ExistingVersion, ExpectedVersion) <> 0 then
      RetainedPreviousDirectory := ExtractFileDir(ExistingActivePath)
    else if ExistingPreviousPath <> '' then
      RetainedPreviousDirectory := ExtractFileDir(ExistingPreviousPath);
  end;

  if not FindFirst(AddBackslash(VersionsRoot) + '*', FindRec) then Exit;
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') and
         ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) then begin
        Candidate := AddBackslash(VersionsRoot) + FindRec.Name;
        if (CompareText(Candidate, TargetDirectory) <> 0) and
           (CompareText(Candidate, RetainedPreviousDirectory) <> 0) then begin
          if ((FindRec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) = 0) and
             (not ContainsReparsePoint(Candidate)) then
            DelTree(Candidate, True, True, True)
          else
            Log('Skipped unsafe version directory during pruning');
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure CommitInstallerState;
begin
  if HasExistingInstall and (CompareText(ExistingVersion, ExpectedVersion) <> 0) then begin
    RegWriteStringValue(HKCU, InstallerStateKey, 'PreviousVersion', ExistingVersion);
    RegWriteStringValue(HKCU, InstallerStateKey, 'PreviousPath', ExistingActivePath);
  end;
  RegWriteStringValue(HKCU, InstallerStateKey, 'InstallRoot', CanonicalInstallRoot);
  RegWriteStringValue(HKCU, InstallerStateKey, 'InstalledVersion', ExpectedVersion);
  RegWriteStringValue(HKCU, InstallerStateKey, 'ActivePath', TargetExecutable);
  RegWriteStringValue(HKCU, InstallerStateKey, 'GitCommit', ExpectedCommit);
  RegWriteDWordValue(HKCU, InstallerStateKey, 'InstallerSchema', 1);
  RetargetOwnedStartup;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    if (not FileExists(TargetExecutable)) or
       (not FileExists(TargetDirectory + '\release-manifest.json')) or
       (not MetadataMatchesTarget) then begin
      CustomExitCode := 12;
      RaiseException(CustomMessage('MetadataFailed'));
    end;
    ExitCode := 21;
    if (not RunAgent(TargetExecutable, '--storage-preflight', ExitCode)) or (ExitCode <> 0) then begin
      CustomExitCode := ExitCode;
      RaiseException(FmtMessage(CustomMessage('PreflightFailed'), [IntToStr(ExitCode)]));
    end;
    CommitInstallerState;
    PruneUnretainedVersions;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := CustomExitCode;
end;

function HasCommandLineParameter(const Value: string): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do begin
    if CompareText(ParamStr(Index), Value) = 0 then begin
      Result := True;
      Exit;
    end;
  end;
end;

function InitializeUninstall: Boolean;
var
  ErrorMessage: string;
  ExitCode: Integer;
begin
  CustomExitCode := 0;
  Result := LoadExistingState;
  if not Result then begin
    MsgBox(CustomMessage('InvalidInstallState'), mbCriticalError, MB_OK);
    Exit;
  end;
  ErrorMessage := PrepareExistingAgent(UninstallSilent);
  if ErrorMessage <> '' then begin
    MsgBox(ErrorMessage, mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  PurgeCertificate := HasCommandLineParameter('/PURGECERT=1');
  PurgeData := HasCommandLineParameter('/PURGEDATA=1');
  if not UninstallSilent then begin
    PurgeCertificate := MsgBox(CustomMessage('RemoveCertificate'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    PurgeData := MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;

  if PurgeCertificate then begin
    if (not RunAgent(ExistingActivePath, '--remove-local-certificate', ExitCode)) or (ExitCode <> 0) then begin
      MsgBox(FmtMessage(CustomMessage('PurgeFailed'), [ExpandConstant('{localappdata}\DebtFlow\SipAgent')]), mbCriticalError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

procedure RemoveOwnedStartup;
var
  ExistingRun, ExePath, Arguments: string;
  QuotePosition: Integer;
begin
  if not RegQueryStringValue(HKCU, RunKey, RunValueName, ExistingRun) then Exit;
  if Pos('"', ExistingRun) <> 1 then Exit;
  Delete(ExistingRun, 1, 1);
  QuotePosition := Pos('"', ExistingRun);
  if QuotePosition <= 1 then Exit;
  ExePath := Copy(ExistingRun, 1, QuotePosition - 1);
  Arguments := Copy(ExistingRun, QuotePosition + 1, Length(ExistingRun));
  if (CompareText(Arguments, ' --background') = 0) and IsOwnedExecutable(ExePath) then
    RegDeleteValue(HKCU, RunKey, RunValueName);
end;

function IsSafeDataRoot(const DataRoot: string): Boolean;
begin
  Result := (CompareText(RemoveBackslashUnlessRoot(DataRoot),
    RemoveBackslashUnlessRoot(ExpandConstant('{localappdata}\DebtFlow\SipAgent'))) = 0) and
    (Length(DataRoot) > Length(ExpandConstant('{localappdata}'))) and
    (Pos('..', DataRoot) = 0) and
    ((not DirExists(DataRoot)) or
      ((not IsReparsePoint(DataRoot)) and (not ContainsReparsePoint(DataRoot))));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataRoot: string;
begin
  if CurUninstallStep = usUninstall then begin
    RemoveOwnedStartup;
    RegDeleteKeyIncludingSubkeys(HKCU, InstallerStateKey);
  end;
  if CurUninstallStep = usPostUninstall then begin
    if (CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')), CanonicalInstallRoot) = 0) and
       (not IsReparsePoint(CanonicalInstallRoot + '\versions')) and
       (not ContainsReparsePoint(CanonicalInstallRoot + '\versions')) then
      DelTree(CanonicalInstallRoot + '\versions', True, True, True)
    else
      Log('Skipped unsafe version directory during uninstall cleanup');
    if PurgeData then begin
      DataRoot := ExpandConstant('{localappdata}\DebtFlow\SipAgent');
      if IsSafeDataRoot(DataRoot) then begin
        if not DelTree(DataRoot, True, True, True) then
          MsgBox(FmtMessage(CustomMessage('PurgeFailed'), [DataRoot]), mbError, MB_OK);
      end;
    end;
  end;
end;
