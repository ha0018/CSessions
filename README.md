# Windows 子会话 Skill

简体中文 · [English](README.en.md)

在已登录的 Windows 桌面中启动本机 RDS 子会话，把指定项目命令交给子会话内的执行器。CLI 返回 Windows 实测的进程会话 ID、退出码和两份日志。首版面向同一台电脑上的一个 Windows 用户，每次运行一个任务。

## 安装

需要 Windows 10 或 11、系统内置的远程桌面 ActiveX 控件和 [.NET 10 SDK](https://learn.microsoft.com/dotnet/core/install/windows)。在普通用户的 PowerShell 7 中，从项目根目录发布自包含程序。下面以 x64 为例。ARM64 需要改用对应的 RID 并先运行验收脚本。

```powershell
$installDir = Join-Path $env:LOCALAPPDATA 'WindowsChildSession\bin'
$cli = Join-Path $installDir 'WindowsChildSession.exe'
dotnet publish .\src\WindowsChildSession -c Release -r win-x64 --self-contained -o $installDir
& $cli status
```

若 `childSessionsEnabled` 为 `false`，在管理员 PowerShell 中用同一个可执行文件运行 `feature enable`。需要时在保存工作后注销 Windows，再重新登录原用户。Windows 对启用功能前已登录的父会话可能提示输入凭据。程序通过本机 ActiveX 窗口呈现 Windows 登录流程。 [Microsoft Learn](https://learn.microsoft.com/windows/win32/termserv/child-sessions)

回到普通用户的 PowerShell，注册当前用户的执行器，再运行验收。

```powershell
& $cli install-executor
& $cli connect
.\scripts\Test-ChildSession.ps1 -Cli $cli
```

`install-executor` 在任务计划程序注册一个当前用户的按需任务。每次缺少执行器时，宿主用 `RunEx` 指定 Windows 返回且已经登录的子会话 ID。登录后仍以实际进程握手为准。更新程序前先运行 `stop` 和 `uninstall-executor`。运行中的任务可先取消并等待结果。更新后重新注册。 [Microsoft Learn](https://learn.microsoft.com/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex)

项目内的 Codex 可用 `$windows-child-session` 调用 Skill。要在其他项目使用，将整个 `.agents\skills\windows-child-session` 目录（含 `scripts` 子目录）复制到 `%USERPROFILE%\.agents\skills`。浏览器启动脚本随 Skill 一起安装。可以设置 `WINDOWS_CHILD_SESSION_EXE` 指向自定义的发布位置。

## 运行任务

示例把本工具的探针作为子会话任务运行。实际构建时，将 `cwd`、`program` 和 `arguments` 改成项目目录、可执行程序的完整路径和项目定义的参数。

```powershell
$id = [guid]::NewGuid().ToString('D')
$request = Join-Path $env:TEMP "windows-child-session-$id.json"
@{
    taskId = $id
    cwd = (Get-Location).Path
    program = $cli
    arguments = @('probe')
    timeoutSeconds = 30
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $request -Encoding utf8
& $cli run --request $request
& $cli status $id
```

- `status` 显示功能开关、连接、执行器状态和活动任务。 `status <taskId>` 查询持久化任务记录。
- `run --request <file>` 等待结果。追加 `--detach` 可在受理后立即返回。相同任务 ID 和参数会读取原任务状态，已使用的 ID 配上不同参数时返回 `conflict`。
- `cancel <taskId>` 请求取消，之后用 `status <taskId>` 等待 `cancelled` 或其他已确定的结果。中断等待中的 CLI 后仍可用同一个 ID 查询。
- `connect` 打开或复用子桌面，`disconnect` 断开当前 RDP 连接，`stop` 关闭父会话宿主。向已断开的子会话派发新的任务时会尝试重连。
- `stop` 返回 `stopping` 表示宿主已受理关闭请求。可用 `status` 确认连接状态变为 `host_not_running`。若宿主仍占用会话锁且未回复，`stop` 和 `disconnect` 返回 `host_unresponsive`，退出码为 1。

`program` 接受绝对路径指向的 `.exe` 或 `.com` 文件。普通程序按 Windows C 运行时规则接收参数。对于 PowerShell 脚本，使用 `pwsh.exe -NoProfile -File` 和脚本路径。对于 pnpm，可用实际的 `node.exe` 和 pnpm 的 JavaScript 入口。命令继承子会话环境，构建依赖的路径和版本以该环境为准。

任务 JSON 包含 `taskId`、`status`、`parentSessionId`、`childSessionId`、`processSessionId`、`exitCode`、`stdoutPath` 和 `stderrPath`。日志位于 `%LOCALAPPDATA%\WindowsChildSession\tasks\<taskId>`，保留程序输出的原始字节。对于派发前的失败，日志文件可能尚未创建。

| 状态 | 含义 | 同步运行的 CLI 退出码 |
| --- | --- | --- |
| `preparing` / `running` | 正在建立连接或执行任务 | 等待中 |
| `completed` | 进程树退出，根进程返回 0 | 0 |
| `process_failed` | 进程树退出，根进程返回非零值 | 10 |
| `start_failed` | 连接、握手或进程启动失败 | 11 |
| `timed_out` | 执行超时，进程树已清理 | 12 |
| `cancelled` | 取消已确认 | 13 |
| `unknown` | 已记录请求，但当前缺少可确认的结果 | 14 |

进程挂起创建并加入 Windows Job Object。执行器核对实际会话 ID 后恢复进程，等待整个受管进程树退出。超时或取消时终止整棵树。父子会话的命名管道使用当前用户的显式 ACL，拒绝远程客户端并检查双方进程的实际 Windows 会话。 [Job Objects](https://learn.microsoft.com/windows/win32/procthread/job-objects) · [命名管道访问权限](https://learn.microsoft.com/windows/win32/ipc/named-pipe-security-and-access-rights)

## 在子会话中打开浏览器

在父会话的 PowerShell 中运行以下命令。将 `Chrome` 替换为 `Edge` 或 `Brave` 即可打开对应浏览器。

```powershell
.\scripts\Open-ChildSessionBrowser.ps1 -Browser Chrome
```

启动脚本通过现有的子会话执行器提交任务。三个浏览器的 `--user-data-dir` 默认都指向 `$env:LOCALAPPDATA\Chrome-ChildSession`。**如果父会话和子会话共用了同一个用户数据目录**，先启动的浏览器会占用目录，另一边再打开时可能没有反应。若要给不同浏览器保留各自的配置，使用 `-UserDataDir` 指定不同的目录。浏览器安装在自定义位置时，使用 `-BrowserPath` 指定其可执行文件的完整路径。

用 `$cli status <taskId>` 查询启动状态。`running` 且 `processSessionId` 等于 `childSessionId` 表明进程已在子会话运行。可在子会话桌面确认窗口是否显示。执行器一次运行一个任务，浏览器打开期间会占用该任务，直至浏览器进程退出、任务被取消或达到 24 小时的超时。

## 排查与卸载

- 如果 `connect` 报登录超时，查看 `Windows Child Session` 窗口，并在 Windows 请求时完成登录。若机器已有其他应用连接子会话，先关闭该应用的子会话连接。系统同一时间只支持一个已连接的子会话。
- 如果执行器启动失败，先核对 `status` 返回的父子会话 ID。可以在已打开的子桌面中运行 `WindowsChildSession.exe executor <parentSessionId> <childSessionId>`。在父桌面执行这条命令会得到明确的会话不匹配错误。
- 如果结果暂不可确认，使用同一个任务 ID 查询 `status` 并查看已有日志。启动中断后，执行器仍可能写入最终结果。

按以下顺序卸载：

1. 用普通用户运行 `status`。如有活动任务，等待任务结束，或用 `cancel <taskId>` 取消并确认最终结果。
2. 运行 `stop`，用 `status` 确认 `connection` 为 `host_not_running`，再运行 `uninstall-executor` 注销执行器。
3. 如果本次安装才启用了机器级子会话功能，且希望将其还原，在管理员 PowerShell 中用同一个可执行文件运行 `feature disable`。
4. 移除发布文件和已复制的 Skill。任务记录和日志位于 `%LOCALAPPDATA%\WindowsChildSession\tasks\<taskId>`，自定义发布路径也使用这个位置。需要保留历史结果时，请保留 `tasks` 目录。

目标机验收及复跑命令见 [验收记录](docs/acceptance.md)。
