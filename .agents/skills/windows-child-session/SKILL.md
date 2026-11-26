---
name: windows-child-session
description: Run, query, or cancel an explicitly requested project command inside the current Windows user's local RDS child session. Use when a build, test, or probe must run in that child session and the caller needs the actual session ID, exit code, or logs.
---

# Windows child session

Use PowerShell in the signed-in Windows parent session. Resolve the executable from a user-provided location, then `$env:WINDOWS_CHILD_SESSION_EXE`, then `$env:LOCALAPPDATA\WindowsChildSession\bin\WindowsChildSession.exe`. If it is missing, report the searched path and refer to the installation steps in the source project's `README.md`. Use the user's current Windows account for the task.

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
