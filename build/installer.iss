; MediaManager 安装包脚本（Inno Setup 6）
; 编译前先执行: dotnet publish src/MediaManager.App -c Release -r win-x64 --self-contained true -o dist/MediaManager-win-x64

#define MyAppName "媒体管理器"
#define MyAppNameEn "MediaManager"
#define MyAppVersion "1.3.2"
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
;
; 瘦身排除项（本应用只做本地浏览/播放，以下模块运行时永不加载）：
;   access_output / stream_out  —— LibVLC 推流输出与推流管线
;   visualization               —— 音频频谱可视化（无此 UI）
;   services_discovery          —— UPnP/SAP 网络设备发现
;   hrtfs                       —— 双耳 HRTF 音效数据（无空间音频）
;   *.lib                       —— 链接期导入库，运行时不读取
;   System.Windows.Forms.Design —— WinForms 设计器支持（托盘只用运行时）
;   mscordbi / mscordaccore / DiaSymReader —— 调试器与 dump 分析资产
; 注意：codec / demux / access / video_output / audio_output / text_renderer /
; spu 等播放核心一律不能动。
Source: "..\dist\MediaManager-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "logs\*,logs\*.*,System.Windows.Forms.Design.dll,Microsoft.DiaSymReader.Native.amd64.dll,mscordbi.dll,mscordaccore_*.dll,libvlc\win-x64\*.lib,libvlc\win-x64\hrtfs\*,libvlc\win-x64\plugins\access_output\*,libvlc\win-x64\plugins\stream_out\*,libvlc\win-x64\plugins\visualization\*,libvlc\win-x64\plugins\services_discovery\*"

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
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.mp4\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelMp4"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.mkv\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelMkv"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.avi\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelAvi"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.mov\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelMov"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.wmv\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelWmv"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.webm\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelWebm"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.flv\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelFlv"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.m4v\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelM4v"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.ts\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelTs"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.mp3\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelMp3"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.wav\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelWav"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.flac\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelFlac"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.m4a\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelM4a"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.aac\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelAac"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.ogg\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelOgg"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.opus\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelOpus"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.wma\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelWma"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.txt\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelTxt"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.md\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelMd"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.log\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelLog"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.json\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelJson"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.csv\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelCsv"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.xml\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelXml"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.ini\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelIni"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.yaml\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelYaml"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.yml\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelYml"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.zip\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelZip"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.rar\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelRar"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.7z\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "Del7z"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.tar\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelTar"
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Classes\SystemFileAssociations\.gz\shell\MediaManagerOpen"" /f"; Flags: runhidden; RunOnceId: "DelGz"
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

procedure DeleteShellIntegrationKeys();
var
  Names: TArrayOfString;
  I: Integer;
begin
  if RegGetValueNames(HKCU, 'Software\MediaManager\ShellExtensions', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
      RegDeleteKeyIncludingSubkeys(HKCU,
        'Software\Classes\SystemFileAssociations\' + Names[I] + '\shell\MediaManagerOpen');
  end;
  RegDeleteKeyIfEmpty(HKCU, 'Software\MediaManager\ShellExtensions');
  RegDeleteKeyIfEmpty(HKCU, 'Software\MediaManager');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  ResultCode: Integer;
  MsgRes: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // 按应用记录的扩展名清单清理右键菜单（以后新增格式不用再改这里）
    DeleteShellIntegrationKeys();

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
