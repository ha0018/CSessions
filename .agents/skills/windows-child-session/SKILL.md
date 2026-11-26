---
name: windows-child-session
description: Run, query, or cancel an explicitly requested project command in the current Windows user's local RDS child session, or open Chrome, Edge, or Brave on that child desktop.
---

# Windows child session

Use PowerShell in the signed-in Windows parent session. Resolve the executable from a user-provided location, then `$env:WINDOWS_CHILD_SESSION_EXE`, then `$env:LOCALAPPDATA\WindowsChildSession\bin\WindowsChildSession.exe`. If it is missing, report the searched path and refer to the installation steps in the source project's `README.md`. Use the user's current Windows account for the task.

To open Chrome, Edge, or Brave, run the [bundled browser launcher](scripts/Open-ChildSessionBrowser.ps1) from the parent session. Resolve the script relative to this `SKILL.md` and pass `-Browser Chrome`, `-Browser Edge`, or `-Browser Brave`. Use `-Cli` or `-BrowserPath` for custom executable locations. The default profile is `$env:LOCALAPPDATA\Chrome-ChildSession` for all three browsers. Use `-UserDataDir` when the user wants a separate profile. A profile in use on the parent desktop can prevent a new window from opening in the child session.

The launcher submits a detached task. Query its task ID until `running` and verify that `processSessionId` matches `childSessionId` and differs from `parentSessionId`. Confirm window visibility on the child desktop when needed. The open browser occupies the executor's single task slot.

For a new task, resolve the project directory and executable to absolute paths. Check the project's declared command when the user names an action such as “build”. Resolve a script or package-manager shim through its actual executable and preserve arguments as an array. For instance, invoke a PowerShell script as `pwsh.exe -NoProfile -File <script-path>` or a pnpm JavaScript entry via `node.exe <pnpm-entry> run build`. The executor inherits the child session's environment. If a required environment setup exists in a project script, use that script.

Write the request to a UTF-8 JSON file. Save the generated ID so you can query or cancel it later.

```powershell
$taskId = [guid]::NewGuid().ToString('D')
$requestPath = Join-Path $env:TEMP "windows-child-session-$taskId.json"
@{
    taskId = $taskId
    cwd = $projectDirectory
    program = $executablePath
    arguments = @($programArguments)
    timeoutSeconds = 1800
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $requestPath -Encoding utf8
& $cli run --request $requestPath --detach
& $cli status $taskId
```

Poll `status <taskId>` while the task is `preparing` or `running`. A brief `unknown` can appear while the request transfers to the worker. For a persistent `unknown` or interrupted CLI, query the same ID and report any remaining uncertainty. A repeat invocation with the same ID and request reads the existing record. Use a new ID only when the user wants a separate execution. For cancellation use `cancel <taskId>` and query until a final result is available or cancellation cannot be confirmed.

Report the task ID, task status, parent and child session IDs, actual process session ID, exit code, and available log paths. Success requires `completed`, exit code zero, and the actual process in the reported child session, distinct from the parent. If startup failed or sign-in or an executor is required, report the CLI error and the corresponding step in the installation guide. Run `status` without an ID to inspect the host, connection, and executor.
