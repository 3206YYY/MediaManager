; MediaManager 安装包脚本（Inno Setup 6）
; 编译前先执行: dotnet publish src/MediaManager.App -c Release -r win-x64 --self-contained true -o dist/MediaManager-win-x64

#define MyAppName "媒体管理器"
#define MyAppNameEn "MediaManager"
#define MyAppVersion "1.3.0"
#define MyAppPublisher "MediaManager"
#define MyAppExeName "MediaManager.App.exe"

[Setup]
AppId={{8B7C1A2E-3D4F-4A5B-9C6D-1E2F3A4B5C6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\MediaManager
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=MediaManager-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
; 用户级安装，无需管理员权限
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 安装包图标
SetupIconFile=..\src\MediaManager.App\MediaManager.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
; 数据目录（供卸载时引用）
; 用户数据固定存 %LocalAppData%\MediaManager

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Files]
; self-contained 发布输出（含 .NET runtime + FFmpeg + LibVLC）
; Excludes 必须排除 logs：发布目录里残留的运行日志含本机路径/媒体路径，
; 打进安装包等于把开发机的隐私带到每个用户机器上。
Source: "..\dist\MediaManager-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "logs\*,logs\*.*"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载时清掉「用媒体管理器打开」的右键菜单注册（用户级 HKCU，不留垃圾）
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.jpg\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelJpg"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.jpeg\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelJpeg"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.png\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelPng"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.gif\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelGif"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.webp\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelWebp"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.bmp\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelBmp"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.tif\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelTif"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.tiff\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelTiff"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.heic\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelHeic"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.avif\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelAvif"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\MediaManager"" /f"; Flags: runhidden; RunOnceId: "DelMarker"

[UninstallDelete]
; 旧版本（1.2.4 及以前）把日志写在程序目录，卸载时清掉；
; 新版本日志改到 %LocalAppData%\MediaManager\logs，属于用户数据，卸载不删。
Type: filesandordirs; Name: "{app}\logs"

[Code]
var
  KeepDataPage: TInputOptionWizardPage;

procedure InitializeWizard();
begin
  KeepDataPage := CreateInputOptionPage(
    wpSelectDir,
    '卸载时的数据选项',
    '请选择卸载时如何处理您的数据',
    '您的收藏、标签、备注等数据保存在 %LocalAppData%\MediaManager 目录。' + #13#10 +
    '此设置将影响后续卸载时的行为。',
    True, False);

  KeepDataPage.Add('保留我的数据（推荐）');
  KeepDataPage.Add('删除我的数据');
  KeepDataPage.Values[0] := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  ResultCode: Integer;
  MsgRes: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\MediaManager');

    MsgRes := MsgBox(
      '是否保留您的收藏、标签、备注数据？' + #13#10 + #13#10 +
      '选择「是」：只卸载程序，保留数据目录（重新安装后数据仍在）。' + #13#10 +
      '选择「否」：连数据一起删除。' + #13#10 + #13#10 +
      '数据目录：' + DataDir,
      mbConfirmation, MB_YESNOCANCEL);

    if MsgRes = IDYES then
    begin
      // 保留数据：什么都不删，仅让卸载继续
    end
    else if MsgRes = IDNO then
    begin
      // 不保留数据：删除数据目录
      if DirExists(DataDir) then
      begin
        if MsgBox('确定要永久删除您的数据吗？此操作不可恢复！',
                  mbConfirmation, MB_YESNO) = IDYES then
        begin
          Exec('cmd.exe', '/C rmdir /S /Q "' + DataDir + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        end;
      end;
    end
    else
    begin
      // 取消：中止卸载
      Abort();
    end;
  end;
end;
