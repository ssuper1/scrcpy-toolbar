Option Explicit
Dim shell, fso, folder, exe, parent, link
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
folder = fso.GetParentFolderName(WScript.ScriptFullName)
exe = fso.BuildPath(folder, "ScrcpyToolbar.exe")
parent = fso.GetParentFolderName(folder)
If Not fso.FileExists(exe) Or Not fso.FileExists(fso.BuildPath(parent, "scrcpy.exe")) Or Not fso.FileExists(fso.BuildPath(parent, "adb.exe")) Then
    MsgBox "Place the entire toolbar folder inside your extracted scrcpy folder first." & vbCrLf & "Expected: scrcpy-folder\toolbar\ScrcpyToolbar.exe", vbExclamation, "scrcpy Toolbar"
    WScript.Quit 1
End If
Set link = shell.CreateShortcut(fso.BuildPath(shell.SpecialFolders("Desktop"), "scrcpy Toolbar.lnk"))
link.TargetPath = exe
link.WorkingDirectory = folder
link.IconLocation = fso.BuildPath(parent, "scrcpy.exe") & ",0"
link.Description = "Start scrcpy with the side toolbar"
link.WindowStyle = 1
link.Save
MsgBox "Created desktop shortcut: scrcpy Toolbar", vbInformation, "scrcpy Toolbar"
