from mcp.server.fastmcp import FastMCP
import asyncio
import uuid
import os
import sys
import json
import traceback
from pathlib import Path

mcp = FastMCP("RobustAsyncREPL")

# --- Async Shell Jobs State ---
jobs = {}

# Ensure a virtual environment exists for the REPL to use
VENV_DIR = Path(".venv")

async def ensure_venv():
    """Create a virtual environment using uv if it doesn't exist."""
    if not VENV_DIR.exists():
        print("Creating virtual environment with uv...")
        process = await asyncio.create_subprocess_shell(
            "uv venv .venv",
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE
        )
        await process.communicate()

# --- Persistent Python Background Worker ---

WORKER_CODE = """
import sys, json, traceback

original_stdout = sys.stdout
original_stderr = sys.stderr

class StreamSender:
    def __init__(self, name):
        self.name = name
    def write(self, s):
        original_stdout.write(json.dumps({"type": "stream", "stream": self.name, "data": s}) + "\\n")
        original_stdout.flush()
    def flush(self):
        pass

sys.stdout = StreamSender("stdout")
sys.stderr = StreamSender("stderr")
repl_globals = {}

for line in sys.stdin:
    try:
        req = json.loads(line)
    except:
        continue
    cmd = req.get("cmd")
    if cmd == "execute":
        code = req.get("code")
        try:
            exec(code, repl_globals)
            original_stdout.write(json.dumps({"type": "done", "status": "ok"}) + "\\n")
        except Exception as e:
            original_stdout.write(json.dumps({"type": "done", "status": "error", "error": traceback.format_exc()}) + "\\n")
        original_stdout.flush()
    elif cmd == "list_vars":
        vars_list = [k for k in repl_globals.keys() if not k.startswith('__')]
        original_stdout.write(json.dumps({"type": "vars", "vars": vars_list}) + "\\n")
        original_stdout.flush()
    elif cmd == "input":
        text = req.get("text", "")
        # In a real interactive PTY we would push this to stdin,
        # but for this basic exec() model, stdin is already consumed by the loop.
        # True interactive python inputs require a PTY or threading model.
        # We will just print a warning for now.
        original_stdout.write(json.dumps({"type": "stream", "stream": "stderr", "data": "Warning: send_python_input is not fully supported in this worker model yet."}) + "\\n")
        original_stdout.flush()
"""

class PythonWorkerManager:
    def __init__(self):
        self.process = None
        self.current_job_id = None
        self.job_status = "idle" # idle, running, completed, error, dead
        self.output = ""
        self.error = ""
        self.read_task = None
        self.vars_future = None
        self.done_future = None

    async def start(self):
        if self.process:
            await self.stop()
        
        # Write the worker script
        with open(".repl_worker.py", "w") as f:
            f.write(WORKER_CODE.strip())

        env = os.environ.copy()
        env["PYTHONUNBUFFERED"] = "1"
        env["VIRTUAL_ENV"] = str(VENV_DIR.absolute())
        if os.name == 'nt':
            env["PATH"] = f"{VENV_DIR.absolute()}\\Scripts;{env.get('PATH', '')}"
            python_exe = str(VENV_DIR.absolute() / "Scripts" / "python.exe")
        else:
            env["PATH"] = f"{VENV_DIR.absolute()}/bin:{env.get('PATH', '')}"
            python_exe = str(VENV_DIR.absolute() / "bin" / "python")

        self.process = await asyncio.create_subprocess_exec(
            python_exe, ".repl_worker.py",
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            env=env
        )
        self.read_task = asyncio.create_task(self._read_loop())
        self.job_status = "idle"
        self.current_job_id = None

    async def stop(self):
        if self.process:
            try:
                self.process.kill()
            except:
                pass
        if self.read_task:
            self.read_task.cancel()
        self.process = None
        self.job_status = "idle"
        self.current_job_id = None

    async def _read_loop(self):
        while True:
            try:
                line = await self.process.stdout.readline()
            except Exception:
                break
                
            if not line:
                self.job_status = "dead"
                break
                
            try:
                msg = json.loads(line.decode('utf-8'))
                mtype = msg.get("type")
                if mtype == "stream":
                    if msg.get("stream") == "stdout":
                        self.output += msg.get("data", "")
                    else:
                        self.error += msg.get("data", "")
                elif mtype == "done":
                    self.job_status = "completed" if msg.get("status") == "ok" else "error"
                    if msg.get("status") == "error":
                        self.error += msg.get("error", "")
                    if self.done_future and not self.done_future.done():
                        self.done_future.set_result(True)
                elif mtype == "vars":
                    if self.vars_future and not self.vars_future.done():
                        self.vars_future.set_result(msg.get("vars", []))
            except Exception as e:
                # If it's not JSON, it might be a raw print from a crash
                self.error += line.decode(errors='replace')

    async def execute(self, code: str):
        if self.job_status == "running":
            return f"Error: Python worker is currently busy running job {self.current_job_id}. Wait for it to finish, or use restart_python_worker() if it's hung."
        
        self.current_job_id = str(uuid.uuid4())
        self.job_status = "running"
        self.output = ""
        self.error = ""
        self.done_future = asyncio.Future()

        req = json.dumps({"cmd": "execute", "code": code}) + "\n"
        self.process.stdin.write(req.encode('utf-8'))
        await self.process.stdin.drain()

        # Wait up to 2 seconds to see if it finishes quickly
        try:
            await asyncio.wait_for(asyncio.shield(self.done_future), timeout=2.0)
            # Finished quickly!
            res = f"Status: {self.job_status}\n"
            if self.output: res += f"\n--- STDOUT ---\n{self.output}"
            if self.error: res += f"\n--- STDERR ---\n{self.error}"
            return res.strip()
        except asyncio.TimeoutError:
            # Still running
            return f"Job ID: {self.current_job_id}\nStatus: running (in background)\n\nUse check_python_status() to see intermediary output or check completion."

    async def get_vars(self):
        if self.job_status == "running":
            return "Error: Cannot list variables while a job is running."
        self.vars_future = asyncio.Future()
        req = json.dumps({"cmd": "list_vars"}) + "\n"
        self.process.stdin.write(req.encode('utf-8'))
        await self.process.stdin.drain()
        try:
            vars_list = await asyncio.wait_for(self.vars_future, timeout=2.0)
            return vars_list
        except asyncio.TimeoutError:
            return "Error: Timeout waiting for variables. Worker might be dead or hung."

worker_manager = PythonWorkerManager()

async def ensure_worker():
    await ensure_venv()
    if worker_manager.process is None or worker_manager.job_status == "dead":
        await worker_manager.start()

# --- Tools ---

@mcp.tool()
async def execute_command_async(command: str) -> str:
    """
    Executes a shell command asynchronously to prevent timeouts.
    Returns a Job ID immediately. Use this for long-running scripts.
    """
    await ensure_venv()
    
    job_id = str(uuid.uuid4())
    jobs[job_id] = {"status": "running", "output": "", "error": ""}
    
    async def run():
        env = os.environ.copy()
        env["PYTHONUNBUFFERED"] = "1"
        env["PIP_NO_INPUT"] = "1"
        env["CI"] = "true"
        env["VIRTUAL_ENV"] = str(VENV_DIR.absolute())
        if os.name == 'nt':
            env["PATH"] = f"{VENV_DIR.absolute()}\\Scripts;{env.get('PATH', '')}"
        else:
            env["PATH"] = f"{VENV_DIR.absolute()}/bin:{env.get('PATH', '')}"
        
        process = await asyncio.create_subprocess_shell(
            command,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            env=env
        )
        jobs[job_id]["process"] = process
        
        async def read_stream(stream, key):
            while True:
                line = await stream.readline()
                if not line:
                    break
                jobs[job_id][key] += line.decode(errors='replace')
                
        await asyncio.gather(
            read_stream(process.stdout, "output"),
            read_stream(process.stderr, "error")
        )
        
        await process.wait()
        jobs[job_id]["status"] = "completed" if process.returncode == 0 else f"failed (code {process.returncode})"

    asyncio.create_task(run())
    return f"Job ID: {job_id}\nStatus: running\nCommand: {command}\n\nUse check_job_status('{job_id}') to see the output."

@mcp.tool()
async def send_command_input(job_id: str, text: str) -> str:
    """
    Send text input (like 'y\n' or passwords) to a running async shell command.
    """
    if job_id not in jobs:
        return f"Error: Job {job_id} not found."
    
    j = jobs[job_id]
    if j["status"] != "running":
        return f"Error: Job {job_id} is not running (status: {j['status']})."
        
    process = j.get("process")
    if not process or not process.stdin:
        return f"Error: Job {job_id} does not have an open stdin pipe."
        
    try:
        process.stdin.write(text.encode('utf-8'))
        await process.stdin.drain()
        return f"Successfully sent input to job {job_id}."
    except Exception as e:
        return f"Error sending input: {str(e)}"

@mcp.tool()
def check_job_status(job_id: str) -> str:
    """
    Retrieves the current status and output of a background shell job.
    """
    if job_id not in jobs:
        return f"Error: Job {job_id} not found."
    j = jobs[job_id]
    return f"Status: {j['status']}\n\n--- STDOUT ---\n{j['output']}\n\n--- STDERR ---\n{j['error']}"

@mcp.tool()
async def install_package_async(package: str) -> str:
    """
    Install a Python package asynchronously using uv into the virtual environment.
    Returns a Job ID to check the installation status.
    """
    await ensure_venv()
    command = f"uv pip install {package}"
    return await execute_command_async(command)

@mcp.tool()
async def execute_python(code: str) -> str:
    """
    Execute Python code in a persistent background worker.
    Variables persist between executions. Safe from hanging the server.
    If the code takes longer than 2 seconds, it will return a running status.
    WARNING: Do not use `input()` or interactive prompts in this tool. It will hang.
    """
    await ensure_worker()
    return await worker_manager.execute(code)

@mcp.tool()
async def check_python_status() -> str:
    """
    Check the status and output of the currently running or last run Python REPL job.
    Useful to see intermediary output of long-running scripts, or check if it's hung.
    """
    await ensure_worker()
    if not worker_manager.current_job_id:
        return "No Python jobs have been run yet."
    
    res = f"Job ID: {worker_manager.current_job_id}\nStatus: {worker_manager.job_status}\n"
    if worker_manager.output: res += f"\n--- STDOUT ---\n{worker_manager.output}"
    if worker_manager.error: res += f"\n--- STDERR ---\n{worker_manager.error}"
    return res.strip()

@mcp.tool()
async def list_variables() -> list:
    """
    List all variables in the current persistent Python session.
    """
    await ensure_worker()
    return await worker_manager.get_vars()

@mcp.tool()
async def restart_python_worker() -> str:
    """
    Kills the current persistent Python worker and starts a fresh one.
    Use this if a script hangs (e.g., infinite loop) and the worker becomes unresponsive.
    Note: This will clear all variables in memory.
    """
    await ensure_worker()
    await worker_manager.start()
    return "Python worker restarted successfully. All previous state has been cleared."

if __name__ == "__main__":
    asyncio.run(ensure_venv())
    mcp.run()