"""
AI Agent Server - Flask HTTP API for task execution with Gemini.
Provides endpoints for task submission, status checking, and cancellation.
Executes code in sandboxed workspace with .NET SDK and Python available.
"""

import os
import json
import uuid
import subprocess
import shutil
import zipfile
import threading
import time
from pathlib import Path
from datetime import datetime, timedelta
from dataclasses import dataclass, field, asdict
from enum import Enum
from typing import Optional
from flask import Flask, request, jsonify
import google.generativeai as genai

app = Flask(__name__)

# Configuration from environment
WORKSPACE_DIR = Path(os.environ.get("WORKSPACE_DIR", "/workspace"))
GEMINI_API_KEY = os.environ.get("GEMINI_API_KEY", "")
GEMINI_MODEL = os.environ.get("GEMINI_MODEL", "gemini-2.5-flash")
MAX_ITERATIONS = int(os.environ.get("MAX_ITERATIONS", "3"))
AGENT_SESSION_TIMEOUT_MINUTES = int(os.environ.get("AGENT_SESSION_TIMEOUT_MINUTES", "10"))
TIMEOUT_SECONDS = AGENT_SESSION_TIMEOUT_MINUTES * 60
MAX_CONCURRENT_TASKS = int(os.environ.get("MAX_CONCURRENT_TASKS", "2"))
WORKSPACE_MAX_AGE_HOURS = int(os.environ.get("WORKSPACE_MAX_AGE_HOURS", "24"))

# Configure Gemini API
genai.configure(api_key=GEMINI_API_KEY)


class TaskStatus(str, Enum):
    QUEUED = "queued"
    RUNNING = "running"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCELLED = "cancelled"


@dataclass
class AgentTask:
    task_id: str
    prompt: str
    document_content: Optional[str] = None
    model: str = GEMINI_MODEL
    max_iterations: int = MAX_ITERATIONS
    timeout_seconds: int = TIMEOUT_SECONDS
    status: TaskStatus = TaskStatus.QUEUED
    message: Optional[str] = None
    error: Optional[str] = None
    output_files: list = field(default_factory=list)
    created_at: datetime = field(default_factory=datetime.utcnow)
    started_at: Optional[datetime] = None
    completed_at: Optional[datetime] = None
    cancelled: bool = False


# In-memory task storage (for simplicity; use Redis for production)
tasks: dict[str, AgentTask] = {}
task_lock = threading.Lock()
task_semaphore = threading.Semaphore(MAX_CONCURRENT_TASKS)


def get_task(task_id: str) -> Optional[AgentTask]:
    with task_lock:
        return tasks.get(task_id)


def update_task(task: AgentTask):
    with task_lock:
        tasks[task.task_id] = task


def create_workspace(task_id: str) -> Path:
    """Create isolated workspace directory for task."""
    task_workspace = WORKSPACE_DIR / task_id
    task_workspace.mkdir(parents=True, exist_ok=True)
    return task_workspace


def cleanup_workspace(task_id: str):
    """Remove task workspace after completion."""
    task_workspace = WORKSPACE_DIR / task_id
    if task_workspace.exists():
        shutil.rmtree(task_workspace, ignore_errors=True)


def cleanup_old_workspaces():
    """
    Removes workspace directories that are older than WORKSPACE_MAX_AGE_HOURS and
    belong to tasks that are no longer running (completed, failed, cancelled, or
    evicted from in-memory storage).

    A workspace is safe to delete when:
    - The directory mtime is older than the max age threshold, AND
    - The task is not currently in RUNNING or QUEUED state.
    """
    if not WORKSPACE_DIR.exists():
        return

    cutoff = datetime.utcnow() - timedelta(hours=WORKSPACE_MAX_AGE_HOURS)
    removed = 0

    for entry in WORKSPACE_DIR.iterdir():
        if not entry.is_dir():
            continue

        try:
            mtime = datetime.utcfromtimestamp(entry.stat().st_mtime)
            if mtime >= cutoff:
                continue

            task = get_task(entry.name)
            if task and task.status in (TaskStatus.RUNNING, TaskStatus.QUEUED):
                continue

            shutil.rmtree(entry, ignore_errors=True)
            removed += 1
        except Exception:
            pass

    if removed:
        print(f"[cleanup] Removed {removed} old workspace(s) older than {WORKSPACE_MAX_AGE_HOURS}h", flush=True)


def start_cleanup_scheduler():
    """Starts a background daemon thread that runs workspace cleanup every hour."""
    def loop():
        while True:
            time.sleep(3600)
            try:
                cleanup_old_workspaces()
            except Exception as e:
                print(f"[cleanup] Error during scheduled cleanup: {e}", flush=True)

    thread = threading.Thread(target=loop, daemon=True, name="workspace-cleanup")
    thread.start()


def run_command(cmd: list[str], cwd: Path, timeout: int = 60) -> tuple[int, str, str]:
    """Execute shell command with timeout. Returns (returncode, stdout, stderr)."""
    try:
        result = subprocess.run(
            cmd,
            cwd=cwd,
            capture_output=True,
            text=True,
            timeout=timeout,
            env={**os.environ, "DOTNET_CLI_HOME": str(cwd / ".dotnet")}
        )
        return result.returncode, result.stdout, result.stderr
    except subprocess.TimeoutExpired:
        return -1, "", f"Command timed out after {timeout}s"
    except Exception as e:
        return -1, "", str(e)


def zip_directory(source_dir: Path, output_path: Path):
    """Compress directory into zip archive."""
    with zipfile.ZipFile(output_path, 'w', zipfile.ZIP_DEFLATED) as zipf:
        for file_path in source_dir.rglob('*'):
            if file_path.is_file():
                arcname = file_path.relative_to(source_dir)
                zipf.write(file_path, arcname)


GEMINI_RETRY_MAX_ATTEMPTS = 5
GEMINI_RETRY_BASE_DELAY_S = 2.0
GEMINI_RETRY_MAX_DELAY_S = 60.0


def call_gemini_with_retry(model, prompt: str, task: AgentTask) -> str:
    """
    Calls model.generate_content with exponential backoff on rate-limit (429) and
    transient server errors (500/503). Respects the task timeout and cancellation flag.

    Raises RuntimeError if all attempts are exhausted or the task is cancelled/timed out.
    """
    attempt = 0
    delay = GEMINI_RETRY_BASE_DELAY_S

    while attempt < GEMINI_RETRY_MAX_ATTEMPTS:
        if task.cancelled:
            raise RuntimeError("Task was cancelled before Gemini call.")

        elapsed = (datetime.utcnow() - task.started_at).total_seconds()
        if elapsed > task.timeout_seconds:
            raise RuntimeError(f"Task timed out after {task.timeout_seconds}s.")

        try:
            response = model.generate_content(prompt)
            return response.text
        except Exception as e:
            error_str = str(e).lower()
            is_rate_limit = "429" in error_str or "quota" in error_str or "resource_exhausted" in error_str
            is_transient = "500" in error_str or "503" in error_str or "unavailable" in error_str

            if not (is_rate_limit or is_transient):
                raise

            attempt += 1
            if attempt >= GEMINI_RETRY_MAX_ATTEMPTS:
                raise RuntimeError(
                    f"Gemini API failed after {GEMINI_RETRY_MAX_ATTEMPTS} attempts: {e}"
                ) from e

            wait = min(delay * (2 ** (attempt - 1)), GEMINI_RETRY_MAX_DELAY_S)
            time.sleep(wait)


def execute_agent_task(task: AgentTask):
    """Main agent execution loop with Gemini."""
    if not task_semaphore.acquire(blocking=False):
        task.status = TaskStatus.FAILED
        task.error = f"Server at capacity (max {MAX_CONCURRENT_TASKS} concurrent tasks)"
        task.completed_at = datetime.utcnow()
        update_task(task)
        return
    
    try:
        task.status = TaskStatus.RUNNING
        task.started_at = datetime.utcnow()
        update_task(task)
        
        workspace = create_workspace(task.task_id)
        model = genai.GenerativeModel(task.model)
    
        # Build initial prompt
        system_prompt = """You are an autonomous AI Agent. Your primary goal is to provide DATA results to the user, not just code. You are also an expert AI software engineer with access to a Linux environment.
You have the following tools available:
- .NET 9.0 SDK (dotnet command)
- Python 3 with pip, pytest, black, pylint, mypy
- Node.js and npm
- Git, curl, wget, zip/unzip

When asked to scrape or fetch data:
1. Write the necessary Python script.
2. IMMEDIATELY RUN the script using a bash block.
3. Save the results (data, text, links) to a .txt or .json file.
4. DO NOT return .py files unless explicitly asked. Return the DATA files.

When creating files, use the format:
```filename.ext
<content>
```

When running commands, use:
```bash
<command>
```

Your goal is to complete the user's task by writing code, scrapping data, providing information, testing, fixing any errors and more.
After completing the task, list all output files that should be returned to the user or the proper answer in the response if no files are needed. If there are more than 3 files, create a zip archive named 'project.zip' and return that instead.

IMPORTANT: 
- Always test your code before declaring completion
- If tests fail, fix the code (max {max_iter} attempts)
- For multi-file projects, create a zip archive named 'project.zip'
- WHEN YOU FINISH THE TASK, YOU MUST EXPLICITLY WRITE THE EXACT PHRASE "TASK COMPLETE" AT THE END OF YOUR RESPONSE SO THE SYSTEM KNOWS TO STOP.
""".format(max_iter=task.max_iterations)
        
        user_prompt = task.prompt
        if task.document_content:
            user_prompt += f"\n\nAttached document:\n---\n{task.document_content}\n---"

        conversation_context = system_prompt + "\n\nTask:\n" + user_prompt
        iteration = 0

        while iteration < task.max_iterations and not task.cancelled:
            iteration += 1

            elapsed = (datetime.utcnow() - task.started_at).total_seconds()
            if elapsed > task.timeout_seconds:
                task.status = TaskStatus.FAILED
                task.error = f"Task timed out after {task.timeout_seconds}s"
                break

            assistant_message = call_gemini_with_retry(model, conversation_context, task)

            # Parse and execute code blocks
            execution_results = []
            lines = assistant_message.split('\n')
            i = 0

            while i < len(lines):
                line = lines[i]
                if line.startswith('```') and not line.startswith('```bash'):
                    filename = line[3:].strip()
                    if filename:
                        content_lines = []
                        i += 1
                        while i < len(lines) and not lines[i].startswith('```'):
                            content_lines.append(lines[i])
                            i += 1

                        if content_lines and content_lines[0].strip() == filename:
                            content_lines.pop(0)

                        file_path = workspace / filename
                        file_path.parent.mkdir(parents=True, exist_ok=True)
                        file_path.write_text('\n'.join(content_lines))
                        execution_results.append(f"Created file: {filename}")

                elif line.startswith('```bash'):
                    cmd_lines = []
                    i += 1
                    while i < len(lines) and not lines[i].startswith('```'):
                        cmd_lines.append(lines[i])
                        i += 1

                    for cmd in cmd_lines:
                        cmd = cmd.strip()
                        if cmd and not cmd.startswith('#'):
                            returncode, stdout, stderr = run_command(
                                ["bash", "-c", cmd],
                                cwd=workspace,
                                timeout=120
                            )
                            result = f"$ {cmd}\nExit code: {returncode}"
                            if stdout:
                                result += f"\nStdout:\n{stdout[:2000]}"
                            if stderr:
                                result += f"\nStderr:\n{stderr[:2000]}"
                            execution_results.append(result)

                i += 1

            if "TASK COMPLETE" in assistant_message.upper() or "COMPLETED" in assistant_message.upper():
                output_files = []
                for f in workspace.rglob('*'):
                    if f.is_file() and not f.name.startswith('.'):
                        csharp_path = str(f).replace(str(WORKSPACE_DIR), '/app/agent-output')
                        output_files.append(csharp_path)

                if len(output_files) > 3:
                    zip_path = workspace / "project.zip"
                    zip_directory(workspace, zip_path)
                    task.output_files = [str(zip_path).replace(str(WORKSPACE_DIR), '/app/agent-output')]
                else:
                    task.output_files = output_files

                task.status = TaskStatus.COMPLETED
                task.message = f"Task completed successfully after {iteration} iteration(s)"
                break

            if execution_results:
                feedback = "\n".join(execution_results)
                conversation_context += (
                    f"\n\n[Your previous response]:\n{assistant_message}"
                    f"\n\n[Terminal output]:\n{feedback}"
                    f"\n\nFix any errors and continue."
                )
        
        if task.status == TaskStatus.RUNNING:
            # Max iterations reached without completion
            task.status = TaskStatus.FAILED
            task.error = f"Max iterations ({task.max_iterations}) reached without completion"
            
    except RuntimeError as e:
        task.status = TaskStatus.FAILED
        task.error = str(e)
    except Exception as e:
        task.status = TaskStatus.FAILED
        task.error = f"Unexpected error: {str(e)}"
    finally:
        task.completed_at = datetime.utcnow()
        update_task(task)
        task_semaphore.release()


def run_task_async(task: AgentTask):
    """Run task in background thread."""
    thread = threading.Thread(target=execute_agent_task, args=(task,))
    thread.daemon = True
    thread.start()


# ============================================================
# HTTP API Endpoints
# ============================================================

# Initialise workspace and start cleanup scheduler when the module is loaded.
# This runs for both `python server.py` and gunicorn workers.
WORKSPACE_DIR.mkdir(parents=True, exist_ok=True)
cleanup_old_workspaces()
start_cleanup_scheduler()


@app.route('/health', methods=['GET'])
def health():
    """Health check endpoint."""
    return jsonify({
        "status": "healthy",
        "timestamp": datetime.utcnow().isoformat(),
        "model": GEMINI_MODEL
    })


@app.route('/tasks', methods=['POST'])
def submit_task():
    data = request.json
    if not data:
        return jsonify({"error": "Empty request"}), 400

    # Pobieramy dane obsługując obie wielkości liter (bot wysyła małe)
    prompt = data.get('Prompt') or data.get('prompt')
    model = data.get('Model') or data.get('model') or GEMINI_MODEL
    task_id = data.get('TaskId') or data.get('taskId') or str(uuid.uuid4())[:8]
    doc_content = data.get('DocumentContent') or data.get('documentContent')
    max_iter = data.get('MaxIterations') or data.get('maxIterations') or MAX_ITERATIONS
    timeout = data.get('TimeoutSeconds') or data.get('timeoutSeconds') or TIMEOUT_SECONDS

    if not prompt:
        return jsonify({"error": "Missing field: prompt"}), 400


    task = AgentTask(
        task_id=task_id,
        prompt=prompt,
        document_content=doc_content,
        model=model,
        max_iterations=int(max_iter),
        timeout_seconds=int(timeout)
    )

    update_task(task)
    run_task_async(task)

    return jsonify({
        "TaskId": task.task_id,
        "Status": task.status.value,
        "Message": "Task submitted successfully"
    }), 202


@app.route('/tasks/<task_id>', methods=['GET'])
def get_task_status(task_id: str):
    """Get status of a specific task."""
    task = get_task(task_id)
    
    if not task:
        return jsonify({"error": "Task not found"}), 404
    
    return jsonify({
        "TaskId": task.task_id,
        "Status": task.status.value,
        "Message": task.message,
        "Error": task.error,
        "OutputFiles": task.output_files
    })


@app.route('/tasks/<task_id>', methods=['DELETE'])
def cancel_task(task_id: str):
    """Cancel a running task."""
    task = get_task(task_id)
    
    if not task:
        return jsonify({"error": "Task not found"}), 404
    
    if task.status in [TaskStatus.COMPLETED, TaskStatus.FAILED, TaskStatus.CANCELLED]:
        return jsonify({"error": f"Task already {task.status.value}"}), 400
    
    task.cancelled = True
    task.status = TaskStatus.CANCELLED
    task.completed_at = datetime.utcnow()
    update_task(task)
    
    # Cleanup workspace
    cleanup_workspace(task_id)
    
    return jsonify({
        "TaskId": task.task_id,
        "Status": task.status.value,
        "Message": "Task cancelled"
    })


if __name__ == '__main__':
    WORKSPACE_DIR.mkdir(parents=True, exist_ok=True)
    cleanup_old_workspaces()
    start_cleanup_scheduler()
    port = int(os.environ.get('PORT', 8080))
    app.run(host='0.0.0.0', port=port, debug=False)
