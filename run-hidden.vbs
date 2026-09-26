' 后台启动 bedremote，不显示黑窗口。
' 注意：没有托盘图标，所以要用 stop.cmd 关它，或者在任务管理器里结束 bedremote.exe。
Dim sh, fso, dir
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = dir
If Not fso.FileExists(dir & "\bedremote.exe") Then
  sh.Run "cmd /c cd /d """ & dir & """ && build.cmd", 1, False
  WScript.Sleep 1500
End If
sh.Run "bedremote.exe", 0, False
