; Elevation keeps the same Windows user so HKCU and the data profile remain valid.
Var SetupLockHandle
Var SetupIsAdmin
Var SetupUserSid
Var IsElevationChild
Var ElevationFile
Var ElevationArgs
Var ElevationExitCode

!macro DefineElevation PREFIX
Function ${PREFIX}InitElevation
 System::Call 'shell32::IsUserAnAdmin()i.s'
 Pop $SetupIsAdmin
 System::Call 'advapi32::OpenProcessToken(p -1,i 8,*p.r0)i.r1'
 ${If} $1 != 0
  System::Call 'advapi32::GetTokenInformation(p r0,i 1,p 0,i 0,*i.r2)'
  System::Alloc $2
  Pop $3
  System::Call 'advapi32::GetTokenInformation(p r0,i 1,p r3,i r2,*i.r2)i.r1'
  ${If} $1 != 0
   System::Call '*$3(p.r4)'
   System::Call 'advapi32::ConvertSidToStringSidW(p r4,*p.r5)i.r1'
   ${If} $1 != 0
    System::Call 'kernel32::lstrcpynW(w .s,p r5,i ${NSIS_MAX_STRLEN})'
    Pop $SetupUserSid
    System::Call 'kernel32::LocalFree(p r5)'
   ${EndIf}
  ${EndIf}
  System::Free $3
  System::Call 'kernel32::CloseHandle(p r0)'
 ${EndIf}

 ${GetParameters} $0
 ${GetOptions} $0 "/NookElevatedUser=" $1
 ${IfNot} ${Errors}
  ${If} $1 == ""
  ${OrIf} $1 != $SetupUserSid
  ${OrIf} $SetupIsAdmin == 0
   MessageBox MB_OK|MB_ICONEXCLAMATION "请使用当前 Windows 账户的管理员授权，或选用当前账户可写的位置。" /SD IDOK
   SetErrorLevel 10
   Quit
  ${EndIf}
  StrCpy $IsElevationChild 1
 ${EndIf}
 ClearErrors
FunctionEnd

Function ${PREFIX}RequestElevation
 ${If} $SetupUserSid == ""
  SetErrors
  Return
 ${EndIf}
 System::Call 'shlwapi::SHStrDupW(w "runas",*p.r1)'
 System::Call 'shlwapi::SHStrDupW(w "$ElevationFile",*p.r2)'
 System::Call 'shlwapi::SHStrDupW(w "$ElevationArgs",*p.r3)'
 ; SHELLEXECUTEINFOW for the x86 Unicode installer stub. Require the child process handle.
 System::Call '*(i 60,i 0x440,p $HWNDPARENT,p r1,p r2,p r3,p 0,i 1,p 0,p 0,p 0,p 0,i 0,p 0,p 0)p.r4'
 ${IfNot} ${Silent}
  ShowWindow $HWNDPARENT ${SW_HIDE}
 ${EndIf}
 ; The elevated process acquires the same per-user installation lock.
 System::Call 'kernel32::CloseHandle(p $SetupLockHandle)'
 System::Call 'shell32::ShellExecuteExW(p r4)i.r0'
 System::Call '*$4(i,i,p,p,p,p,p,i,p,p,p,p,i,p,p.r5)'
 System::Free $4
 System::Call 'ole32::CoTaskMemFree(p r1)'
 System::Call 'ole32::CoTaskMemFree(p r2)'
 System::Call 'ole32::CoTaskMemFree(p r3)'
 ${If} $0 != 0
 ${AndIf} $5 != 0
  System::Call 'kernel32::WaitForSingleObject(p r5,i -1)'
  System::Call 'kernel32::GetExitCodeProcess(p r5,*i.r6)i.r7'
  System::Call 'kernel32::CloseHandle(p r5)'
  ${If} $7 == 0
   StrCpy $6 10
  ${EndIf}
  StrCpy $ElevationExitCode $6
  !insertmacro AcquireInstallLock
  ClearErrors
  Return
 ${EndIf}
 !insertmacro AcquireInstallLock
 ${IfNot} ${Silent}
  ShowWindow $HWNDPARENT ${SW_SHOW}
 ${EndIf}
 SetErrors
FunctionEnd
!macroend
