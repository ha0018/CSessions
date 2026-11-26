# Windows 子会话

当用户通过 `$windows-child-session` 在子会话中打开 Chrome、Edge 或 Brave 时，在父会话的 PowerShell 中从项目根目录运行 `.\scripts\Open-ChildSessionBrowser.ps1 -Browser <Chrome|Edge|Brave>`。脚本通过现有执行器启动浏览器，并默认将 `--user-data-dir` 设为 `$env:LOCALAPPDATA\Chrome-ChildSession`。如果父会话和子会话共用了同一个用户数据目录，先启动的浏览器占用目录后，另一边可能打开无反应。自定义目录和启动状态的检查见 [README.md](README.md#在子会话中打开浏览器)。浏览器启动任务可保持 `running`，通过实际进程会话 ID 核对运行位置，通过子会话桌面确认窗口。
