Unicode true
ManifestSupportedOS all
RequestExecutionLevel admin
SetCompressor /SOLID zlib

!include "MUI2.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

!define PUBLISHER "IsaacZimm"
!define PROJECT_URL "https://github.com/IsaacZimm/diagnostico-e-manutencao"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\IsaacZimm.Otimizador"

Name "Otimizador ${APP_VERSION}"
OutFile "${SETUP_FILE}"
InstallDir "$PROGRAMFILES64\Otimizador"
Icon "..\src\Otimizador\Assets\app.ico"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=1046 "ProductName" "Otimizador"
VIAddVersionKey /LANG=1046 "FileDescription" "Instalador do Otimizador"
VIAddVersionKey /LANG=1046 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1046 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1046 "LegalCopyright" "GustavoReset e colaboradores"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\Otimizador.exe"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "PortugueseBR"

Function .onInit
    ${IfNot} ${AtLeastWin10}
    ${OrIfNot} ${IsNativeAMD64}
        MessageBox MB_OK|MB_ICONSTOP "O Otimizador precisa de Windows 10 ou 11 para processadores x64." /SD IDOK
        SetErrorLevel 2
        Quit
    ${EndIf}
    SetRegView 64
    SetShellVarContext all
FunctionEnd

Section "Otimizador"
    SetOutPath "$INSTDIR"
    File "${PAYLOAD_DIR}\Otimizador.exe"
    File "${PAYLOAD_DIR}\LEIA-ME.txt"
    File "${PAYLOAD_DIR}\LICENSE.txt"
    File "${PAYLOAD_DIR}\LICENCAS-DE-TERCEIROS.txt"
    WriteUninstaller "$INSTDIR\Desinstalar.exe"

    CreateShortcut "$SMPROGRAMS\Otimizador.lnk" "$INSTDIR\Otimizador.exe"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "Otimizador"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "${PUBLISHER}"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "URLInfoAbout" "${PROJECT_URL}"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Otimizador.exe,0"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Desinstalar.exe"'
    WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
    SetRegView 64
    SetShellVarContext all
    ${If} ${FileExists} "$INSTDIR\Otimizador.exe"
        ClearErrors
        Delete "$INSTDIR\Otimizador.exe"
        ${If} ${Errors}
            MessageBox MB_OK|MB_ICONSTOP "Feche o Otimizador e tente desinstalar novamente." /SD IDOK
            SetErrorLevel 2
            Abort
        ${EndIf}
    ${EndIf}
    Delete "$SMPROGRAMS\Otimizador.lnk"
    Delete "$INSTDIR\LEIA-ME.txt"
    Delete "$INSTDIR\LICENSE.txt"
    Delete "$INSTDIR\LICENCAS-DE-TERCEIROS.txt"
    Delete "$INSTDIR\Desinstalar.exe"
    RMDir "$INSTDIR"
    DeleteRegKey HKLM "${UNINSTALL_KEY}"
SectionEnd
