"""Control the local Windows child session from Python in the parent session."""

import json
import os
import subprocess
import tempfile
from collections.abc import Sequence
from pathlib import Path
from typing import Any
from uuid import UUID, uuid4


class ChildSessionError(RuntimeError):
    """The CLI could not fulfill the request. Its JSON response is in `response`."""

    def __init__(self, message: str, returncode: int, response: dict | None = None):
        super().__init__(message)
        self.returncode = returncode
        self.response = response


class ChildSession:
    """Use an explicit CLI path, WINDOWS_CHILD_SESSION_EXE, or the local installation."""

    def __init__(self, cli: str | Path | None = None):
        path = cli or os.environ.get("WINDOWS_CHILD_SESSION_EXE")
        if path is None:
            path = Path(os.environ["LOCALAPPDATA"]) / "WindowsChildSession/bin/WindowsChildSession.exe"
        self.cli = Path(path).resolve(strict=True)

    def _call(self, *args: str) -> dict[str, Any]:
        proc = subprocess.run(
            [self.cli, *args], capture_output=True, encoding="utf-8",
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        try:
            reply = json.loads(proc.stdout)
        except json.JSONDecodeError:
            reply = None
        if not isinstance(reply, dict):
            raise ChildSessionError(proc.stderr.strip() or "Expected a JSON object from the CLI.", proc.returncode)
        # A synchronous run uses codes 10–14 for recorded task outcomes.
        if proc.returncode and not (args[0] == "run" and 10 <= proc.returncode <= 14 and "taskId" in reply):
            raise ChildSessionError(reply.get("error") or reply.get("status") or "CLI request failed.",
                                   proc.returncode, reply)
        return reply

    def status(self, task_id: str | UUID | None = None) -> dict[str, Any]:
        """Get connection details or the persisted result for one task."""
        if task_id is None:
            return self._call("status")
        return self._call("status", str(task_id))

    def connect(self) -> dict[str, Any]:
        return self._call("connect")

    def disconnect(self) -> dict[str, Any]:
        """Disconnect the RDP viewer and keep the child session running."""
        return self._call("disconnect")

    def stop(self) -> dict[str, Any]:
        """Request host shutdown and logoff of its child session."""
        return self._call("stop")

    def cancel(self, task_id: str | UUID) -> dict[str, Any]:
        """Request cancellation. Query status(task_id) for the final outcome."""
        return self._call("cancel", str(task_id))

    def run(
        self,
        program: str | Path,
        arguments: Sequence[str] = (),
        *,
        cwd: str | Path | None = None,
        timeout_seconds: int = 1800,
        detach: bool = False,
        task_id: str | UUID | None = None,
    ) -> dict[str, Any]:
        """Run an absolute executable path in the child session, optionally detached."""
        if isinstance(arguments, (str, bytes)):
            raise TypeError("Pass arguments as a sequence of strings.")
        request = {
            "taskId": str(uuid4() if task_id is None else task_id),
            "cwd": str(Path.cwd() if cwd is None else Path(cwd).resolve()),
            "program": str(program),
            "arguments": list(arguments),
            "timeoutSeconds": timeout_seconds,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "request.json"
            path.write_text(json.dumps(request, ensure_ascii=False), encoding="utf-8")
            args = ["run", "--request", str(path)]
            if detach:
                args.append("--detach")
            return self._call(*args)
