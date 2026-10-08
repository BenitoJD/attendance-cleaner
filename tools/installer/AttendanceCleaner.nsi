Unicode true

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "0.0.0"
!endif
!ifndef APP_ARCH
  !error "Pass APP_ARCH as x64 or x86"
!endif
!ifndef OUTPUT_DIR
  !error "Pass OUTPUT_DIR as the directory for generated setup files"
!endif
!if "${APP_ARCH}" != "x64"
  !if "${APP_ARCH}" != "x86"
    !error "APP_ARCH must be x64 or x86"
  !endif
!endif

!define APP_NAME "Attendance Cleaner"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AttendanceCleaner"

Name "${APP_NAME}"
Caption "${APP_NAME} Setup"
OutFile "${OUTPUT_DIR}\AttendanceCleaner-Setup-${APP_ARCH}.exe"
InstallDir "$LOCALAPPDATA\Programs\Attendance Cleaner"
RequestExecutionLevel user
SetCompressor /SOLID zlib
ShowInstDetails show

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME} Setup"
VIAddVersionKey "FileDescription" "${APP_NAME} installer"
VIAddVersionKey "FileVersion" "${APP_VERSION}.0"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"

!define MUI_ABORTWARNING
!define MUI_ICON "${__FILEDIR__}\..\..\src\AttendanceCleaner\Resources\AppIcon\appicon.ico"
!define MUI_UNICON "${__FILEDIR__}\..\..\src\AttendanceCleaner\Resources\AppIcon\appicon.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\AttendanceCleaner.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch Attendance Cleaner"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!macro ConfigureRegistryView
  !if "${APP_ARCH}" == "x64"
    SetRegView 64
  !else
    SetRegView 32
  !endif
!macroend

Function .onInit
  StrCpy $0 "$LOCALAPPDATA\Programs\Attendance Cleaner"
  ${If} $INSTDIR != $0
    MessageBox MB_ICONSTOP "Attendance Cleaner installs in the standard per-user application folder."
    Abort
  ${EndIf}
  !insertmacro ConfigureRegistryView
FunctionEnd

Function un.onInit
  StrCpy $0 "$LOCALAPPDATA\Programs\Attendance Cleaner"
  ${If} $INSTDIR != $0
    MessageBox MB_ICONSTOP "The Attendance Cleaner install folder could not be verified; uninstall was stopped."
    Abort
  ${EndIf}
  !insertmacro ConfigureRegistryView
FunctionEnd

Section "Attendance Cleaner (required)" SecApp
  SectionIn RO
  SetShellVarContext current
  SetOutPath "$INSTDIR"

  !if "${APP_ARCH}" == "x64"
    File /r "${__FILEDIR__}\..\..\publish-x64\*"
  !else
    File /r "${__FILEDIR__}\..\..\publish-x86\*"
  !endif

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Attendance Cleaner"
  CreateShortCut "$SMPROGRAMS\Attendance Cleaner\Attendance Cleaner.lnk" "$INSTDIR\AttendanceCleaner.exe" "" "$INSTDIR\AttendanceCleaner.exe"

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Attendance Cleaner"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\AttendanceCleaner.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" "$\"$INSTDIR\Uninstall.exe$\" /S"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section /o "Create a desktop shortcut" SecDesktop
  SetShellVarContext current
  CreateShortCut "$DESKTOP\Attendance Cleaner.lnk" "$INSTDIR\AttendanceCleaner.exe" "" "$INSTDIR\AttendanceCleaner.exe"
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetOutPath "$TEMP"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$SMPROGRAMS\Attendance Cleaner\Attendance Cleaner.lnk"
  RMDir "$SMPROGRAMS\Attendance Cleaner"
  Delete "$DESKTOP\Attendance Cleaner.lnk"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  RMDir /r "$INSTDIR"
SectionEnd
