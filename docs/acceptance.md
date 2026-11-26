# Windows 子会话目标机验收

日期为 2026 年 11 月 26 日。目标机运行 Windows 11 Home China 25H2，版本 `26200.6899`，Edition ID 为 `CoreCountrySpecific`。宿主、执行器与 CLI 均为 x64，使用 .NET SDK `10.0.404` 发布。测试在父会话 `2` 以普通用户权限执行，Windows 子会话功能原已启用。

## 探针与生命周期

原型连接时 Windows 曾暂时返回子会话 `3`。当时尚未完成登录，任务计划程序无法使用该会话的交互令牌。登录完成后 Windows 返回 `4`。用任务计划程序定向启动的独立探针确认自身在会话 `4`，父进程仍在会话 `2`。

正式验收使用已发布并注册执行器的程序。

```powershell
$cli = Join-Path $env:LOCALAPPDATA 'WindowsChildSession\bin\WindowsChildSession.exe'
.\scripts\Test-ChildSession.ps1 -Cli $cli
```

| 检查项 | 实测结果 | 任务 ID |
| --- | --- | --- |
| 会话归属与输出 | 父会话 2，Windows 子会话 4，实际进程会话 4，退出码 0 | `850cbce8-2539-4fed-8343-969689f92c1d` |
| 同 ID 重试 | 同一请求返回原结果，更新时间相同 | 同上 |
| 错误会话执行器 | 在父会话启动执行器立即报实际会话与预期子会话不符 | — |
| 非零退出和参数传递 | 退出码 7。验证中文和空格目录、参数内双引号和结尾反斜杠，以及独立的 stdout/stderr | `494c459e-6e79-481c-bf93-3c095d0faac9` |
| 程序不存在 | `start_failed`，返回缺失的可执行文件路径 | `7cf5b6dc-aa84-4156-97b1-63622c0f3c1b` |
| 工作目录无效 | `start_failed`，返回无效的工作目录 | `5ef2ade0-b516-41e6-8012-2aee1e1ebdfd` |
| 超时和派生进程 | `timed_out`，脚本核实派生进程退出 | `8a3d6d79-d5e1-40a9-9201-3f8324086416` |
| 运行中断开 RDP 后取消 | 断开后仍可按 ID 请求取消，最终为 `cancelled`，派生进程退出 | `c5b59880-0f1d-4255-a21d-465096e3ada4` |
| 断线后重连 | 新任务执行成功，实际进程会话匹配 | `9e44d7db-3991-4c19-8b7a-27aa7eaee04a` |
| 运行中注销测试子会话 | 父会话仍为 2，子会话 ID 恢复为空。原任务保留已有日志及 Windows 返回的非零退出码 | `7223a929-440e-4b6a-a3f8-00101e32fcfd` |
| CLI 客户端退出 | 关闭正在等待的客户端后，用原 ID 查到任务仍在子会话内运行 | `0b2741bc-7997-4697-b8c5-c1ac45c5d28f` |
| 执行器异常退出 | Job Object 清理了派生进程。原 ID 返回 `unknown`，重复提交仍返回原记录 | `0b2741bc-7997-4697-b8c5-c1ac45c5d28f` |
| 执行器自动恢复 | 下一次探针自动启动了新执行器，在子会话 3 成功运行 | `09d8060f-3e2d-45d1-b50d-1f25e923c36f` |

可用 `WindowsChildSession.exe status <taskId>` 复核结果。两份日志存储在 `%LOCALAPPDATA%\WindowsChildSession\tasks\<taskId>`。

## 本地 Codex 构建

注销测试子会话后，`status` 返回 `childSessionId: null`、`connection: disconnected`、`executorReady: false`。随后用本地 `codex exec` 提交中文请求，指定使用 `$windows-child-session` 构建 `C:\Users\h\source\repos\deepseek-harness`。Codex 读取 Skill，通过 CLI 提交并查询。同一请求完成了建立会话、自动启动执行器和运行真实构建。

| 字段 | 实测值 |
| --- | --- |
| 仓库提交 | `4b399c39a6` |
| 构建命令 | `pnpm run build` |
| 环境 | Node.js `24.22.3`，pnpm `11.28.5` |
| 任务 ID | `1e0bdf10-5bba-427d-8a63-16d9f3b45ab0` |
| 父会话 / Windows 子会话 / 构建进程会话 | `2 / 3 / 3` |
| 状态 / 退出码 | `completed / 0` |
| 构建检查 | 10 项通过，0 项失败，0 项跳过 |

在目标机运行下面的命令可打开该任务的结果和日志。本次使用 `%LOCALAPPDATA%\WindowsChildSessionDev\Invoke-DeepSeekBuild.ps1`，接受 Node.js 可执行文件和 pnpm JavaScript 入口的完整路径。脚本将对应 Node.js 的目录加到本次构建的 PATH，再运行 `pnpm run build`。

```powershell
$cli = Join-Path $env:LOCALAPPDATA 'WindowsChildSession\bin\WindowsChildSession.exe'
$result = & $cli status 1e0bdf10-5bba-427d-8a63-16d9f3b45ab0 | ConvertFrom-Json
$result
Get-Content -LiteralPath $result.stdoutPath -Tail 15
Get-Content -LiteralPath $result.stderrPath
```

## 待验收项

完整的父会话注销测试会结束当前桌面，留待下一次可注销的验收窗口。届时可先提交测试脚本中的长任务，保存任务 ID 并注销父会话。重新登录后检查执行器与任务子进程均已退出，再按原 ID 查询。

网页接入需先确定网页发起端可使用的本地执行通道。
