#!/usr/bin/env python3
"""
Mux Subprocess Wrapper - Launch and interact with Mux Swarm CLI subprocesses.

Provides bidirectional communication, agent swapping, and lifecycle management
for running Mux Swarm instances as child processes.

Usage:
    from mux_subprocess import MuxSubprocess
    
    with MuxSubprocess.detect() as mux:
        mux.wait_for_ready()
        mux.enter_chat()
        response = mux.send("Hello, agent!")
        print(response)
"""

import os
import re
import sys
import uuid
import time
import queue
import shutil
import signal
import atexit
import logging
import platform
import subprocess
import threading
from pathlib import Path
from datetime import datetime
from typing import Optional, List, Dict, Any, Callable


class MuxSubprocessError(Exception):
    """Base exception for MuxSubprocess errors."""
    pass


class MCPTimeoutError(MuxSubprocessError):
    """Raised when MCP servers fail to initialize within timeout."""
    pass


class AgentNotReadyError(MuxSubprocessError):
    """Raised when attempting to send before entering chat interface."""
    pass


class SentinelTimeoutError(MuxSubprocessError):
    """Raised when response sentinel is not received within timeout."""
    pass


class MuxSubprocess:
    """
    Manage a Mux Swarm CLI subprocess with bidirectional communication.
    
    Attributes:
        mux_exe: Path to MuxSwarm binary
        cwd: Working directory (MuxSwarm install folder)
        process: subprocess.Popen instance
        name: Identifier for logging
        log_file: Path to log file
    """
    
    # Default bootstrap timeout for MCP servers
    DEFAULT_BOOTSTRAP_TIMEOUT = 15  # seconds
    # Default response timeout
    DEFAULT_RESPONSE_TIMEOUT = 120  # seconds
    # Sentinel format for message boundaries
    SENTINEL_FORMAT = "__MUX_SUBPROCESS_SENTINEL_{}__"
    
    # UI patterns to filter from responses (compiled once)
    UI_PATTERN_REGEX = re.compile(
        r'^(\s*[⠀⠄⠆⠦⠶⠷⣷⣿]\s*MuxAgent|'  # Thinking spinner
        r'\s*──\s*MuxAgent|'                     # Agent header
        r'\s*──\s*AGENTIC CHAT INTERFACE|'       # Chat header
        r'\s*──+|'                               # Separator lines
        r'\s*Type /qc to exit|'                  # Help text
        r'\s*\[SKILLS\]|'                        # Skills loading
        r'\s*\[AGENT\]|'                         # Agent tools
        r'\s*✓\s*\[|'                            # Checkmarks
        r'\s*>\s*$|'                             # Empty prompt
        r'\s*Provider:\s*|'                      # Provider info
        r'\s*Endpoint:\s*|'                       # Endpoint info
        r'\s*API key:\s*|'                       # API key info
        r'\s*\[AGENT SESSION\]|'                 # Session saved
        r'\s*~\d+ tokens|'                       # Token count
        r'\s*Press \[Escape\]|'                  # Escape hint
        r'\s*\[calling:)',                       # Tool calling indicator
        re.IGNORECASE
    )
    
    def __init__(
        self,
        mux_exe: str,
        cwd: str,
        name: Optional[str] = None,
        log_dir: Optional[str] = None,
        env: Optional[Dict[str, str]] = None
    ):
        """
        Initialize MuxSubprocess with explicit paths.
        
        Args:
            mux_exe: Full path to MuxSwarm.exe (Windows) or MuxSwarm (Unix)
            cwd: Working directory - MUST be MuxSwarm install folder
            name: Identifier for this instance (used in logs)
            log_dir: Directory for log files (default: sandbox/mux-subprocess-logs)
            env: Additional environment variables
        """
        self.mux_exe = Path(mux_exe).resolve()
        self.cwd = Path(cwd).resolve()
        self.name = name or f"mux-{uuid.uuid4().hex[:8]}"
        self.process: Optional[subprocess.Popen] = None
        self._in_chat = False
        self._current_agent: Optional[str] = None
        self._output_queue: queue.Queue = queue.Queue()
        self._reader_thread: Optional[threading.Thread] = None
        self._stop_reader = threading.Event()
        
        # Setup logging
        if log_dir:
            self.log_dir = Path(log_dir)
        else:
            # Try to find sandbox from common locations
            self.log_dir = Path.home() / "mux-subprocess-logs"
        self.log_dir.mkdir(parents=True, exist_ok=True)
        self.log_file = self.log_dir / f"{self.name}.log"
        
        self._setup_logging()
        
        # Prepare environment
        self.env = os.environ.copy()
        if env:
            self.env.update(env)
        
        # Validate paths
        if not self.mux_exe.exists():
            raise FileNotFoundError(f"MuxSwarm binary not found: {self.mux_exe}")
        if not self.cwd.exists():
            raise FileNotFoundError(f"Working directory not found: {self.cwd}")
        
        self._log("INFO", f"Initialized with mux_exe={self.mux_exe}, cwd={self.cwd}")
    
    @classmethod
    def detect(
        cls,
        name: Optional[str] = None,
        log_dir: Optional[str] = None,
        search_paths: Optional[List[str]] = None
    ) -> "MuxSubprocess":
        """
        Auto-detect MuxSwarm installation from common locations.
        
        Searches in order:
        1. Provided search_paths
        2. %LOCALAPPDATA%\Mux-Swarm (Windows)
        3. ~/.local/share/mux-swarm (Unix)
        4. PATH
        
        Args:
            name: Instance identifier
            log_dir: Log directory
            search_paths: Additional paths to search
            
        Returns:
            Configured MuxSubprocess instance
        """
        system = platform.system()
        
        # Build search paths
        paths = []
        if search_paths:
            paths.extend(search_paths)
        
        if system == "Windows":
            local_app_data = os.environ.get("LOCALAPPDATA", "")
            if local_app_data:
                paths.append(Path(local_app_data) / "Mux-Swarm")
            paths.append(Path("C:/Program Files/Mux-Swarm"))
            paths.append(Path("C:/Program Files (x86)/Mux-Swarm"))
        else:
            paths.append(Path.home() / ".local" / "share" / "mux-swarm")
            paths.append(Path("/opt/mux-swarm"))
            paths.append(Path("/usr/local/mux-swarm"))
        
        # Search for binary
        for base_path in paths:
            base = Path(base_path)
            if system == "Windows":
                exe = base / "MuxSwarm.exe"
            else:
                exe = base / "MuxSwarm"
            
            if exe.exists():
                return cls(mux_exe=str(exe), cwd=str(base), name=name, log_dir=log_dir)
        
        # Try PATH
        exe_name = "MuxSwarm.exe" if system == "Windows" else "MuxSwarm"
        mux_in_path = shutil.which(exe_name)
        if mux_in_path:
            mux_path = Path(mux_in_path)
            return cls(mux_exe=str(mux_path), cwd=str(mux_path.parent), name=name, log_dir=log_dir)
        
        raise FileNotFoundError(
            f"Could not find MuxSwarm installation. Searched: {[str(p) for p in paths]}"
        )
    
    def _setup_logging(self):
        """Configure file logging."""
        self.logger = logging.getLogger(f"MuxSubprocess.{self.name}")
        self.logger.setLevel(logging.DEBUG)
        
        # File handler
        fh = logging.FileHandler(self.log_file, encoding='utf-8')
        fh.setLevel(logging.DEBUG)
        fh.setFormatter(logging.Formatter('%(asctime)s [%(levelname)s] %(message)s'))
        self.logger.addHandler(fh)
    
    def _log(self, level: str, message: str):
        """Log a message."""
        getattr(self.logger, level.lower())(message)
    
    def _filter_ui_lines(self, response: str) -> str:
        """Filter out UI chrome lines from response."""
        lines = response.split('\n')
        filtered = [line for line in lines if not self.UI_PATTERN_REGEX.match(line)]
        return '\n'.join(filtered).strip()
    
    def start(self):
        """
        Start the MuxSwarm subprocess.
        
        Must be called before any interaction.
        """
        if self.process and self.process.poll() is None:
            self._log("WARNING", "Process already running")
            return
        
        self._log("INFO", f"Starting subprocess: {self.mux_exe}")
        
        # Create process with pipes for stdin/stdout/stderr
        self.process = subprocess.Popen(
            [str(self.mux_exe)],
            cwd=str(self.cwd),
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=self.env,
            text=True,
            bufsize=1,  # Line buffered
            encoding='utf-8',
            errors='replace'
        )
        
        self._log("INFO", f"Process started with PID={self.process.pid}")
        
        # Start reader thread for stdout
        self._stop_reader.clear()
        self._reader_thread = threading.Thread(
            target=self._reader_loop,
            daemon=True
        )
        self._reader_thread.start()
        
        # Register cleanup on exit
        atexit.register(self.terminate)
    
    def _reader_loop(self):
        """Background thread to read stdout and queue output."""
        try:
            while not self._stop_reader.is_set() and self.process:
                line = self.process.stdout.readline()
                if not line:
                    break
                self._output_queue.put(line)
                self._log("DEBUG", f"STDOUT: {line.rstrip()}")
        except Exception as e:
            self._log("ERROR", f"Reader thread error: {e}")
    
    def _read_until(
        self,
        pattern: str,
        timeout: float = None,
        include_match: bool = False
    ) -> str:
        """
        Read output until pattern is found or timeout.
        
        Args:
            pattern: Regex pattern to match
            timeout: Timeout in seconds
            include_match: Include the matching line in output
            
        Returns:
            Accumulated output up to (and optionally including) match
        """
        timeout = timeout or self.DEFAULT_RESPONSE_TIMEOUT
        deadline = time.time() + timeout
        output = []
        regex = re.compile(pattern, re.IGNORECASE)
        
        while time.time() < deadline:
            try:
                line = self._output_queue.get(timeout=0.5)
                output.append(line)
                
                if regex.search(line):
                    self._log("DEBUG", f"Pattern matched: {pattern}")
                    if not include_match:
                        output.pop()  # Remove the matching line
                    return ''.join(output)
            except queue.Empty:
                if self.process and self.process.poll() is not None:
                    raise MuxSubprocessError(
                        f"Process terminated unexpectedly (exit code: {self.process.poll()})"
                    )
                continue
        
        raise SentinelTimeoutError(
            f"Pattern '{pattern}' not found within {timeout}s. "
            f"Output so far: {''.join(output)[:500]}"
        )
    
    def wait_for_ready(self, timeout: float = None):
        """
        Wait for MCP servers to initialize.
        
        Should be called after start() before entering chat.
        
        Args:
            timeout: Maximum wait time (default: 15 seconds)
        """
        timeout = timeout or self.DEFAULT_BOOTSTRAP_TIMEOUT
        
        if not self.process:
            raise MuxSubprocessError("Process not started. Call start() first.")
        
        self._log("INFO", f"Waiting for MCP servers (timeout={timeout}s)")
        
        # MCP servers typically print initialization messages
        # Look for common indicators or just wait the full timeout
        start_time = time.time()
        
        try:
            # Try to detect ready state from output
            # This pattern may need adjustment based on actual MuxSwarm output
            self._read_until(r"(ready|mcp.*started|server.*connected|initialized)", timeout=timeout * 0.8)
        except SentinelTimeoutError:
            # If no ready indicator found, just wait the remaining time
            remaining = timeout - (time.time() - start_time)
            if remaining > 0:
                time.sleep(remaining)
        
        self._log("INFO", "MCP servers initialized")
    
    def enter_chat(self):
        """
        Enter the agent chat interface.
        """
        if not self.process:
            raise MuxSubprocessError("Process not started. Call start() first.")
        
        command = "/agent"
        self._log("INFO", f"Entering chat: {command}")
        
        self.process.stdin.write(command + "\n")
        self.process.stdin.flush()
        
        # Wait for agent interface ready indicator
        try:
            self._read_until(r"(agent.*ready|ready for input|>)", timeout=10)
        except SentinelTimeoutError:
            # May already be at prompt
            pass
        
        self._in_chat = True
        self._log("INFO", "Entered chat interface")
    
    def send(
        self,
        message: str,
        timeout: float = None,
        sentinel: bool = True,
        filter_ui: bool = True
    ) -> str:
        """
        Send a message and wait for response.
        
        Args:
            message: Message to send
            timeout: Response timeout
            sentinel: Use sentinel marker for clean boundaries
            filter_ui: Filter UI chrome from response
            
        Returns:
            Agent response
        """
        timeout = timeout or self.DEFAULT_RESPONSE_TIMEOUT
        
        if not self._in_chat:
            raise AgentNotReadyError("Not in chat interface. Call enter_chat() first.")
        
        if not self.process:
            raise MuxSubprocessError("Process not started.")
        
        # Generate unique sentinel
        sentinel_id = uuid.uuid4().hex
        sentinel_marker = self.SENTINEL_FORMAT.format(sentinel_id)
        
        # Send message with sentinel
        full_message = message
        if sentinel:
            # The sentinel is a marker we can use to detect response completion
            # We send it as an echo command that will appear in output
            full_message += f"\necho {sentinel_marker}"
        
        self._log("INFO", f"SEND: {message[:100]}...")
        self.process.stdin.write(full_message + "\n")
        self.process.stdin.flush()
        
        # Read until sentinel appears
        if sentinel:
            try:
                response = self._read_until(re.escape(sentinel_marker), timeout=timeout)
                self._log("INFO", f"RECV: {response[:100]}...")
                
                # Filter UI chrome if requested
                if filter_ui:
                    response = self._filter_ui_lines(response)
                
                return response.strip()
            except SentinelTimeoutError:
                self._log("ERROR", f"Response timeout after {timeout}s")
                raise
        else:
            # Without sentinel, just wait and collect output
            time.sleep(2)
            output = []
            while not self._output_queue.empty():
                try:
                    output.append(self._output_queue.get_nowait())
                except queue.Empty:
                    break
            result = ''.join(output)
            return self._filter_ui_lines(result) if filter_ui else result
    
    def send_async(
        self,
        message: str,
        callback: Optional[Callable[[str], None]] = None
    ) -> str:
        """
        Send message without waiting for response.
        
        Use is_complete() and get_result() to check status.
        
        Args:
            message: Message to send
            callback: Optional callback when response received
            
        Returns:
            Sentinel ID for tracking
        """
        if not self._in_chat:
            raise AgentNotReadyError("Not in chat interface. Call enter_chat() first.")
        
        sentinel_id = uuid.uuid4().hex
        self._log("INFO", f"SEND_ASYNC: {message[:100]}... (sentinel={sentinel_id})")
        
        # Store for later retrieval
        if not hasattr(self, '_async_results'):
            self._async_results = {}
        self._async_results[sentinel_id] = {
            'status': 'pending',
            'response': None,
            'callback': callback
        }
        
        # Send in background thread
        def _send_and_wait():
            try:
                response = self.send(message, sentinel=True)
                self._async_results[sentinel_id]['response'] = response
                self._async_results[sentinel_id]['status'] = 'complete'
                if callback:
                    callback(response)
            except Exception as e:
                self._async_results[sentinel_id]['status'] = 'error'
                self._async_results[sentinel_id]['error'] = str(e)
        
        threading.Thread(target=_send_and_wait, daemon=True).start()
        
        return sentinel_id
    
    def is_complete(self, sentinel_id: str) -> bool:
        """Check if async message has completed."""
        if not hasattr(self, '_async_results'):
            return False
        result = self._async_results.get(sentinel_id, {})
        return result.get('status') in ('complete', 'error')
    
    def get_result(self, sentinel_id: str) -> Optional[str]:
        """Get result of async message."""
        if not hasattr(self, '_async_results'):
            return None
        result = self._async_results.get(sentinel_id, {})
        if result.get('status') == 'error':
            raise MuxSubprocessError(result.get('error', 'Unknown error'))
        return result.get('response')
    
    def peek_output(self, lines: int = 5) -> str:
        """Peek at recent output without consuming."""
        output = []
        temp = []
        
        while len(temp) < lines:
            try:
                line = self._output_queue.get_nowait()
                temp.append(line)
            except queue.Empty:
                break
        
        # Put back
        for line in temp:
            self._output_queue.put(line)
            output.append(line)
        
        return ''.join(output[-lines:])
    
    def list_agents(self) -> List[str]:
        """
        List available agents using /swap command.
        
        Returns:
            List of agent names
        """
        if not self.process:
            raise MuxSubprocessError("Process not started.")
        
        self._log("INFO", "Listing agents with /swap")
        
        # Exit chat if we're in it
        was_in_chat = self._in_chat
        if self._in_chat:
            self.exit_chat()
        
        # Send /swap command
        self.process.stdin.write("/swap\n")
        self.process.stdin.flush()
        
        # Read agent list - wait longer for output
        output = ""
        try:
            # Collect output for a few seconds
            deadline = time.time() + 5
            while time.time() < deadline:
                try:
                    line = self._output_queue.get(timeout=0.5)
                    output += line
                except queue.Empty:
                    # Check if we have the agent list
                    if "Loaded" in output and "agents" in output:
                        break
                    continue
        except Exception as e:
            self._log("ERROR", f"Error reading agent list: {e}")
        
        self._log("DEBUG", f"Raw output: {output[:500]}")
        
        # Parse agent names - look for "Loaded N agents" and numbered list
        agents = []
        
        # Method 1: Parse numbered list (1. AgentName or 1) AgentName)
        for line in output.split('\n'):
            match = re.match(r'\s*(\d+)[.\)]\s*(.+)', line)
            if match:
                agents.append(match.group(2).strip())
        
        # Method 2: If no numbered list found, look for agent names in output
        if not agents:
            # Look for patterns like "MuxAgent", "CodeAgent", etc.
            agent_pattern = re.compile(r'\b([A-Z][a-zA-Z]*Agent)\b')
            found = agent_pattern.findall(output)
            agents = list(dict.fromkeys(found))  # Dedupe preserving order
        
        self._log("INFO", f"Found {len(agents)} agents: {agents}")
        
        # Re-enter chat if we were in it
        if was_in_chat:
            self.enter_chat()
        
        return agents
    
    def swap_agent(self, agent_index: int):
        """
        Swap to a different agent.
        
        Args:
            agent_index: 1-based index from list_agents()
        """
        if not self.process:
            raise MuxSubprocessError("Process not started.")
        
        self._log("INFO", f"Swapping to agent index {agent_index}")
        
        # Exit current chat if in one
        if self._in_chat:
            self.exit_chat()
        
        # Send /swap
        self.process.stdin.write("/swap\n")
        self.process.stdin.flush()
        
        try:
            self._read_until(r"select.*agent|enter number|>", timeout=5)
        except SentinelTimeoutError:
            pass
        
        # Send agent number
        self.process.stdin.write(f"{agent_index}\n")
        self.process.stdin.flush()
        
        try:
            response = self._read_until(r"selected|switched|>", timeout=5)
            self._log("INFO", f"Agent swap response: {response[:100]}")
        except SentinelTimeoutError:
            pass
        
        # Enter chat with new agent
        self.enter_chat()
    
    def exit_chat(self):
        """Exit the agent chat interface."""
        if not self._in_chat:
            return
        
        self._log("INFO", "Exiting chat with /qc")
        
        self.process.stdin.write("/qc\n")
        self.process.stdin.flush()
        
        try:
            self._read_until(r">|mux", timeout=5)
        except SentinelTimeoutError:
            pass
        
        self._in_chat = False
        self._current_agent = None
    
    def terminate(self, force: bool = False):
        """
        Terminate the subprocess.
        
        Args:
            force: Use SIGKILL instead of SIGTERM
        """
        if not self.process:
            return
        
        self._log("INFO", f"Terminating process PID={self.process.pid} (force={force})")
        
        # Stop reader thread
        self._stop_reader.set()
        
        # Exit chat first
        if self._in_chat:
            try:
                self.exit_chat()
            except Exception:
                pass
        
        # Terminate process
        try:
            if force:
                self.process.kill()
            else:
                self.process.terminate()
            
            # Wait for process to exit
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        except Exception as e:
            self._log("ERROR", f"Error terminating process: {e}")
        
        self.process = None
        self._in_chat = False
        self._log("INFO", "Process terminated")
    
    def __enter__(self):
        """Context manager entry."""
        self.start()
        return self
    
    def __exit__(self, exc_type, exc_val, exc_tb):
        """Context manager exit."""
        self.terminate()
        return False
    
    def __del__(self):
        """Destructor - ensure cleanup."""
        if self.process and self.process.poll() is None:
            self.terminate()


# CLI interface for testing
if __name__ == "__main__":
    import argparse
    
    parser = argparse.ArgumentParser(description="Mux Subprocess Wrapper")
    parser.add_argument("--mux-exe", help="Path to MuxSwarm binary")
    parser.add_argument("--cwd", help="Working directory")
    parser.add_argument("--message", "-m", help="Send a message and exit")
    parser.add_argument("--interactive", "-i", action="store_true", help="Interactive mode")
    parser.add_argument("--list-agents", action="store_true", help="List available agents")
    parser.add_argument("--swap", type=int, help="Swap to agent by index")
    parser.add_argument("--timeout", type=float, default=15, help="Bootstrap timeout")
    
    args = parser.parse_args()
    
    # Create instance
    if args.mux_exe and args.cwd:
        mux = MuxSubprocess(mux_exe=args.mux_exe, cwd=args.cwd)
    else:
        mux = MuxSubprocess.detect()
    
    print(f"MuxSwarm: {mux.mux_exe}")
    print(f"Working Dir: {mux.cwd}")
    
    try:
        mux.start()
        print(f"Process started (PID={mux.process.pid})")
        
        print(f"Waiting for MCP servers ({args.timeout}s)...")
        mux.wait_for_ready(timeout=args.timeout)
        print("Ready!")
        
        if args.list_agents:
            agents = mux.list_agents()
            print("\nAvailable agents:")
            for i, agent in enumerate(agents, 1):
                print(f"  {i}. {agent}")
        
        elif args.swap:
            print(f"\nSwapping to agent {args.swap}...")
            mux.swap_agent(args.swap)
            print("Agent swapped!")
        
        elif args.interactive:
            mux.enter_chat()
            print("\n=== Interactive Mode ===")
            print("Type messages and press Enter to send.")
            print("Type 'exit' or '/qc' to quit.\n")
            
            while True:
                try:
                    msg = input("You: ").strip()
                    if msg.lower() in ('exit', '/qc', 'quit'):
                        break
                    
                    response = mux.send(msg)
                    print(f"\nAgent: {response}\n")
                except KeyboardInterrupt:
                    break
            
            print("\nExiting...")
        
        elif args.message:
            mux.enter_chat()
            response = mux.send(args.message)
            # Handle Unicode characters that can't be printed to Windows console (cp1252)
            try:
                print(f"\nResponse:\n{response}")
            except UnicodeEncodeError:
                # Fallback: replace non-printable chars
                safe = response.encode(sys.stdout.encoding or 'utf-8', errors='replace').decode(sys.stdout.encoding or 'utf-8')
                print(f"\nResponse:\n{safe}")
        
        else:
            print("\nNo action specified. Use --message, --interactive, or --list-agents")
    
    finally:
        mux.terminate()
        print("Process terminated.")
