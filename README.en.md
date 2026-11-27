# Windows Child Session Skill

[简体中文](README.md) · English

Run a project command on a local Windows Remote Desktop Services (RDS) child desktop while you stay signed in to the parent desktop. The CLI reports the Windows session ID of the process, its exit code, and separate logs for standard output and standard error. The executor runs one task at a time for one Windows user on the same machine.

## Installation

### Install the skill locally

If you already have a checkout, open its root directory in PowerShell 7. For a fresh checkout, run:

```powershell
git clone https://github.com/ha0018/CSessions.git
Set-Location CSessions
```

Run the following commands in a non-elevated PowerShell session. They install both `SKILL.md` and the browser launcher for the current user and print the installed paths. With their default settings, Codex, DeepSeek Harness, and Gemini CLI share `%USERPROFILE%\.agents\skills`. Claude Code reads `%USERPROFILE%\.claude\skills`.

```powershell
$source = (Resolve-Path -LiteralPath '.\.agents\skills\windows-child-session' -ErrorAction Stop).Path
foreach ($root in @('.agents\skills', '.claude\skills')) {
    $target = Join-Path $env:USERPROFILE "$root\windows-child-session"
    $null = New-Item -ItemType Directory -Path $target -Force -ErrorAction Stop
    Get-ChildItem -LiteralPath $source -Force -ErrorAction Stop |
        Copy-Item -Destination $target -Recurse -Force -ErrorAction Stop
    Get-Item -LiteralPath (Join-Path $target 'SKILL.md'), (Join-Path $target 'scripts\Open-ChildSessionBrowser.ps1') -ErrorAction Stop |
        Select-Object -ExpandProperty FullName
}
```

Open a new local client session. Enter `$windows-child-session` in Codex or `/windows-child-session` in Claude Code. In Gemini CLI, use `/skills list`. In DeepSeek Harness, ask to use `windows-child-session` by name. When using Codex inside this repository, you can skip the copy step because the project already includes the skill.

**To update the skill**, run `git pull --ff-only origin main` on the repository's `main` branch. After it succeeds, rerun the copy commands. This updates the instructions and scripts used by the clients. To run child-session tasks for the first time or update the local `WindowsChildSession.exe`, follow the executor steps below.

### Install the Windows executor

You need Windows 10 or 11, the built-in Remote Desktop ActiveX control, PowerShell 7, and the [.NET 10 SDK](https://learn.microsoft.com/dotnet/core/install/windows).

From the project root, publish a self-contained executable in a non-elevated PowerShell session. The example targets x64. For ARM64, use `win-arm64` and run the acceptance script on the target machine before relying on the build.

```powershell
$installDir = Join-Path $env:LOCALAPPDATA 'WindowsChildSession\bin'
$cli = Join-Path $installDir 'WindowsChildSession.exe'
dotnet publish .\src\WindowsChildSession -c Release -r win-x64 --self-contained -o $installDir
& $cli status
```

If `childSessionsEnabled` is `false`, run `feature enable` with the same executable in an **elevated** PowerShell session. If Windows requires a new sign-in, save your work, sign out, and sign back in as the same user. Windows may prompt for credentials if the parent session was already signed in when you enabled the feature. Complete the prompt in the local **Windows Child Session** window. [Microsoft Learn: Child Sessions](https://learn.microsoft.com/windows/win32/termserv/child-sessions)

Return to the non-elevated PowerShell session to register the executor for your user, connect to the child desktop, and run the acceptance script.

```powershell
& $cli install-executor
& $cli connect
.\scripts\Test-ChildSession.ps1 -Cli $cli
```

`install-executor` registers an on-demand task in Windows Task Scheduler. When the executor is needed, the host calls `RunEx` with the ID of the signed-in child session reported by Windows. The host then verifies the session of the executor process during the handshake. Before updating the executable, let any active task finish or cancel it and confirm its result. Run `stop` and `uninstall-executor`, update the executable, then run `install-executor` again. [Microsoft Learn: IRegisteredTask::RunEx](https://learn.microsoft.com/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex)

If you publish the CLI elsewhere, set `WINDOWS_CHILD_SESSION_EXE` to the full path of the executable. In your local client, ask `windows-child-session` to run `probe`. Verify that `processSessionId` matches `childSessionId` and differs from `parentSessionId`.

## Run a task

This example runs the CLI's own `probe` command inside the child session. To run a build, set `cwd` to your project directory, `program` to the full path of the executable, and `arguments` to the arguments your project uses.

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

- `status` reports whether child sessions are enabled, the connection state, executor readiness, and the active task. `status <taskId>` reads a saved task record.
- `run --request <file>` waits for the result. Add `--detach` to return as soon as the host accepts the request. Resubmitting the same request with the same ID returns its saved state. Reusing an ID with different parameters returns `conflict`.
- `cancel <taskId>` requests cancellation. Poll `status <taskId>` until it reports `cancelled` or another confirmed outcome. If you interrupt the CLI while it is waiting, you can still query the same ID.
- `connect` opens or reuses the child desktop. `disconnect` closes the RDP connection and keeps the child session and its tasks running. Submitting a new task to a disconnected child session triggers a reconnection attempt.
- Closing the **Windows Child Session** window or running `stop` logs off the window's child session and closes the host. Save your work first because logoff ends applications and tasks in the child session.
- `stop` asks the host in the parent session to shut down. A reply of `stopping` acknowledges the request. Use `status` to confirm that `connection` becomes `host_not_running`. If the host still holds its session lock and does not reply, `stop` and `disconnect` return `host_unresponsive` with CLI exit code 1.

Use an absolute path to an `.exe` or `.com` file for `program`. Arguments are quoted according to Windows C runtime rules. For a PowerShell script, use the full path to `pwsh.exe` and pass `-NoProfile`, `-File`, and the script path as separate arguments. For pnpm, use the full path to `node.exe` and pass pnpm's JavaScript entry point followed by the pnpm arguments. The command inherits the child session's environment, including its tool paths and versions.

The result JSON includes `taskId`, `status`, `parentSessionId`, `childSessionId`, `processSessionId`, `exitCode`, `stdoutPath`, and `stderrPath`. Logs preserve the program's original output bytes under `%LOCALAPPDATA%\WindowsChildSession\tasks\<taskId>`. A failure before dispatch can occur before the log files exist.

The JSON field `exitCode` belongs to the task's root process. The final column below gives the exit code of the CLI when `run` waits for the task.

| Task status | Meaning | CLI exit code |
| --- | --- | --- |
| `preparing` / `running` | The host is preparing the task or the process tree is running. | Still waiting |
| `completed` | The process tree exited and the root process returned zero. | 0 |
| `process_failed` | The process tree exited and the root process returned a nonzero code. | 10 |
| `start_failed` | Connection, handshake, or process startup failed. | 11 |
| `timed_out` | The task exceeded its time limit and the process tree was terminated. | 12 |
| `cancelled` | Cancellation was confirmed. | 13 |
| `unknown` | The request was recorded, but its outcome cannot currently be confirmed. | 14 |

The executor creates the process suspended in a Windows Job Object. It checks the process's actual session ID before resuming it, then waits for the entire process tree to exit. A timeout or cancellation terminates the tree. The named pipes between the parent and child sessions use an explicit access control list for the current user, reject remote clients, and verify the Windows session of each peer process. [Job Objects](https://learn.microsoft.com/windows/win32/procthread/job-objects) · [Named Pipe Security and Access Rights](https://learn.microsoft.com/windows/win32/ipc/named-pipe-security-and-access-rights)

## Open a browser in the child session

From the project root in the parent desktop's PowerShell session, run the launcher with `Chrome`, `Edge`, or `Brave`.

```powershell
.\scripts\Open-ChildSessionBrowser.ps1 -Browser Chrome
```

The launcher submits the browser as a detached task. All three browsers default to `$env:LOCALAPPDATA\Chrome-ChildSession` for `--user-data-dir`. If the parent and child desktops use the same profile directory, the first browser can hold it open and prevent a window from appearing on the other desktop. Use `-UserDataDir` to give a browser its own profile, and `-BrowserPath` to supply the full executable path for a custom installation.

Query the returned task ID with `& $cli status <taskId>`, replacing the placeholder with the actual ID. A `running` task whose `processSessionId` matches `childSessionId` and differs from `parentSessionId` confirms that the process is running in the child session. Check the child desktop to confirm the window is visible. The browser occupies the executor's single task slot until it exits, you cancel the task, or the 24-hour timeout expires.

## Troubleshooting and uninstalling

- **Sign-in times out.** Check the **Windows Child Session** window and complete any Windows sign-in prompt. Windows supports one connected child session at a time. If another application is connected to it, disconnect that application first.
- **The executor fails to start.** Check the parent and child session IDs with `status`. On the connected **child desktop**, you can run `WindowsChildSession.exe executor <parentSessionId> <childSessionId>` with those IDs. Running this command on the parent desktop produces a session mismatch error.
- **The result is unknown.** Query the same task ID with `status <taskId>` and inspect any existing logs. The executor may still write a final result after startup was interrupted.

To uninstall, complete these steps in order.

1. Run `status` as the ordinary user. Let any active task finish, or use `cancel <taskId>` and confirm its final result.
2. Run `stop`. Once `status` reports `host_not_running` for `connection`, run `uninstall-executor` to remove the scheduled task.
3. If you enabled the machine-wide child session feature for this installation and want to restore its previous setting, run `feature disable` with the same executable in an elevated PowerShell session.
4. Remove the published files and the copied skill. Task records and logs remain under `%LOCALAPPDATA%\WindowsChildSession\tasks\<taskId>`, including when you use a custom publish directory. Keep the `tasks` directory if you need the history.

The target-machine acceptance record includes the tested Windows configuration, observed results, and commands for rerunning the checks. [Acceptance record (Chinese)](docs/acceptance.md)
