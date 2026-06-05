Set WshShell = CreateObject("WScript.Shell")
WshShell.Run "dotnet """ & Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\")) & "publish\JiraWorklogViewer.dll""", 0, False
