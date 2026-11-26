#ifndef AppVersion
 #define AppVersion "0.32.0"
#endif
#ifndef PublishDir
 #define PublishDir "..\release\0.32.0"
#endif
#ifdef TestBuild
 #define ProductName "Tunnelito Installer QA"
 #define ProductId "RelayDesk-Installer-QA-4F386E9C"
 #define SetupName "Tunnelito-Setup-QA"
#else
 #define ProductName "Tunnelito"
 #define ProductId "RelayDesk-Desktop-A7B02E29-4D16-47F0-B8F3-3B829E1F024D"
 #define SetupName "Tunnelito-Setup-" + AppVersion + "-x64"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=Tunnelito
VersionInfoDescription=Установка Tunnelito
AppPublisherURL=https://github.com/rapichmoney/Tunnelito
AppSupportURL=https://github.com/rapichmoney/Tunnelito/issues
AppUpdatesURL=https://github.com/rapichmoney/Tunnelito/releases
DefaultDirName={autopf}\{#ProductName}
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
#ifdef HandoffTestBuild
PrivilegesRequired=lowest
#else
PrivilegesRequired=admin
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=output
OutputBaseFilename={#SetupName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\RelayDesk.ico
UninstallDisplayIcon={app}\Tunnelito.exe
UninstallDisplayName={#ProductName}
CloseApplications=no
RestartApplications=no
#ifdef TestBuild
SetupMutex=RelayDesk.Setup.QA
#else
SetupMutex=RelayDesk.Setup
#endif

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,Prepare-Install.ps1"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Prepare-Install.ps1"; Flags: dontcopy

[Icons]
#ifndef TestBuild
Name: "{commonprograms}\Tunnelito\Tunnelito"; Filename: "{app}\Tunnelito.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Tunnelito"; Filename: "{app}\Tunnelito.exe"; WorkingDir: "{app}"; Check: WantDesktopShortcut
#endif

[InstallDelete]
#ifndef TestBuild
Type: files; Name: "{commonprograms}\RelayDesk\RelayDesk.lnk"
Type: dirifempty; Name: "{commonprograms}\RelayDesk"
Type: files; Name: "{userprograms}\RelayDesk\RelayDesk.lnk"
Type: dirifempty; Name: "{userprograms}\RelayDesk"
Type: files; Name: "{autodesktop}\RelayDesk.lnk"
#endif

[Run]
#ifndef TestBuild
Filename: "{app}\Tunnelito.exe"; Parameters: "--install-https-certificate"; Flags: runhidden waituntilterminated runasoriginaluser
Filename: "{app}\Tunnelito.exe"; Parameters: "--resume-update {code:GetResumeToken}"; Flags: nowait; Check: ShouldResume; AfterInstall: MarkResumed
Filename: "{app}\Tunnelito.exe"; Description: "Запустить Tunnelito"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: NormalLaunch
#endif

[UninstallRun]
Filename: "{app}\Tunnelito.exe"; Parameters: "--remove-https-certificate"; Flags: runhidden; RunOnceId: "RemoveTunnelitoHttpsCertificate"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Remove-Startup.ps1"" -Executable ""{app}\RelayDesk.exe"""; Flags: runhidden; RunOnceId: "RemoveRelayDeskStartup"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Remove-Startup.ps1"" -Executable ""{app}\Tunnelito.exe"""; Flags: runhidden; RunOnceId: "RemoveTunnelitoStartup"

[Messages]
russian.WelcomeLabel2=Установщик разместит Tunnelito и четыре ядра в Program Files.%n%nДля функции HTTPS будет добавлен локальный сертификат доверия текущего пользователя Windows. Перехват выключен до включения в настройках. Сертификат можно удалить из Tunnelito.%n%nПрофили и правила сохраняются.
english.WelcomeLabel2=Setup installs Tunnelito and four network cores.%n%nA local HTTPS certificate will be added to the current Windows user's trust store. HTTPS inspection stays off until enabled in Settings. You can remove certificate trust from Tunnelito.%n%nExisting profiles and rules are preserved.

[Code]
var
  ResumeToken: String;
  Prepared: Boolean;
  Resumed: Boolean;
  HadDesktopShortcut: Boolean;

function WantDesktopShortcut: Boolean;
begin
  Result := HadDesktopShortcut or WizardIsTaskSelected('desktopicon');
end;

function GetResumeToken(Param: String): String;
begin
  Result := ResumeToken;
end;

function ShouldResume: Boolean;
begin
  Result := (ResumeToken <> '') and (ExpandConstant('{param:NOLAUNCH|0}') <> '1');
end;

function NormalLaunch: Boolean;
begin
  Result := (ResumeToken = '') and (ExpandConstant('{param:NOLAUNCH|0}') <> '1');
end;

procedure MarkResumed;
begin
  Resumed := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  Args: String;
  TokenText: AnsiString;
begin
  Result := '';
  if Prepared then Exit;
  HadDesktopShortcut := FileExists(ExpandConstant('{autodesktop}\RelayDesk.lnk')) or FileExists(ExpandConstant('{autodesktop}\Tunnelito.lnk'));
#ifdef TestBuild
#ifndef HandoffTestBuild
  Prepared := True;
  Exit;
#endif
#endif
  ExtractTemporaryFile('Prepare-Install.ps1');
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\Prepare-Install.ps1') + '" -ResultFile "' + ExpandConstant('{tmp}\resume-token.txt') + '"';
  ResumeToken := ExpandConstant('{param:RESUMEUPDATE|}');
#ifdef HandoffTestBuild
  Args := Args + ' -OnlyProcessId ' + ExpandConstant('{param:QAPID|0}') + ' -DataFolder "' + ExpandConstant('{param:QADATA|}') + '"';
  if ExpandConstant('{param:QAPID|0}') = '0' then
  begin
    Result := 'Missing isolated QA process';
    Exit;
  end;
#endif
  if ResumeToken <> '' then Args := Args + ' -ResumeToken "' + ResumeToken + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := 'Не удалось подготовить Tunnelito к обновлению.'
  else if ExitCode <> 0 then
    Result := 'Приложение не завершило подготовку. Сохрани текущую операцию и повтори установку.'
  else if LoadStringFromFile(ExpandConstant('{tmp}\resume-token.txt'), TokenText) then
  begin
    ResumeToken := String(TokenText);
    Prepared := True;
  end
  else Result := 'Не удалось прочитать состояние перезапуска.';
end;

procedure DeinitializeSetup;
var
  ExitCode: Integer;
begin
  if Prepared and (not Resumed) and ShouldResume and FileExists(ExpandConstant('{app}\Tunnelito.exe')) then
    Exec(ExpandConstant('{app}\Tunnelito.exe'), '--resume-update ' + ResumeToken, ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ExitCode);
end;
