#!/usr/bin/env python3
"""
Mux Docker Wrapper - Launch and interact with Mux Swarm CLI in Docker containers.

Provides bidirectional communication, agent swapping, and lifecycle management
for running Mux Swarm instances in isolated containers.

Usage:
    from mux_docker import MuxDocker
    
    with MuxDocker.ensure_image() as mux:
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
import threading
import subprocess
from pathlib import Path
from datetime import datetime
from typing import Optional, List, Dict, Any, Callable

try:
    import docker
    from docker.errors import DockerException, ImageNotFound, APIError, ContainerError
    DOCKER_AVAILABLE = True
except ImportError:
    DOCKER_AVAILABLE = False


class MuxDockerError(Exception):
    """Base exception for MuxDocker errors."""
    pass


class DockerNotFoundError(MuxDockerError):
    """Raised when Docker is not installed or not running."""
    pass


class ImageNotFoundError(MuxDockerError):
    """Raised when MuxSwarm Docker image is not found."""
    pass


class MCPTimeoutError(MuxDockerError):
    """Raised when MCP servers fail to initialize within timeout."""
    pass


class AgentNotReadyError(MuxDockerError):
    """Raised when attempting to send before entering chat interface."""
    pass


class SentinelTimeoutError(MuxDockerError):
    """Raised when response sentinel is not received within timeout."""
    pass


class MuxDocker:
    """
    Manage a Mux Swarm CLI instance in a Docker container with bidirectional communication.
    
    Attributes:
        image: Docker image name
        name: Container name
        container: docker.Container instance
        log_file: Path to log file
    """
    
    # Default bootstrap timeout for MCP servers (longer in containers)
    DEFAULT_BOOTSTRAP_TIMEOUT = 20  # seconds
    # Default response timeout
    DEFAULT_RESPONSE_TIMEOUT = 120  # seconds
    # Sentinel format for message boundaries
    SENTINEL_FORMAT = "__MUX_DOCKER_SENTINEL_{}__"
    # Default image name
    DEFAULT_IMAGE = "mux-swarm:latest"
    
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
        image: str = None,
        name: Optional[str] = None,
        log_dir: Optional[str] = None,
        env_vars: Optional[Dict[str, str]] = None,
        volumes: Optional[Dict[str, str]] = None,
        cpu_limit: Optional[float] = None,
        memory_limit: Optional[str] = None,
        network: str = "bridge",
        working_dir: str = "/app",
        auto_remove: bool = True
    ):
        """
        Initialize MuxDocker with configuration.
        
        Args:
            image: Docker image name (default: mux-swarm:latest)
            name: Container name (auto-generated if not provided)
            log_dir: Directory for log files
            env_vars: Environment variables to pass to container
            volumes: Volume mounts {host_path: container_path}
            cpu_limit: CPU limit (e.g., 2.0 for 2 cores)
            memory_limit: Memory limit (e.g., "4g" for 4GB)
            network: Docker network (default: bridge)
            working_dir: Working directory inside container
            auto_remove: Remove container on exit
        """
        if not DOCKER_AVAILABLE:
            raise DockerNotFoundError(
                "Docker SDK not installed. Install with: pip install docker"
            )
        
        self.image = image or self.DEFAULT_IMAGE
        self.name = name or f"mux-{uuid.uuid4().hex[:8]}"
        self.container: Optional[docker.models.containers.Container] = None
        self._in_chat = False
        self._current_agent: Optional[str] = None
        self._output_queue: queue.Queue = queue.Queue()
        self._reader_thread: Optional[threading.Thread] = None
        self._stop_reader = threading.Event()
        
        # Container configuration
        self.env_vars = env_vars or {}
        self.volumes = volumes or {}
        self.cpu_limit = cpu_limit
        self.memory_limit = memory_limit
        self.network = network
        self.working_dir = working_dir
        self.auto_remove = auto_remove
        
        # Setup logging
        if log_dir:
            self.log_dir = Path(log_dir)
        else:
            self.log_dir = Path.home() / "mux-docker-logs"
        self.log_dir.mkdir(parents=True, exist_ok=True)
        self.log_file = self.log_dir / f"{self.name}.log"
        
        self._setup_logging()
        
        # Initialize Docker client
        try:
            self.client = docker.from_env()
            # Test connection
            self.client.ping()
        except DockerException as e:
            raise DockerNotFoundError(f"Docker not available: {e}")
        
        # Check if image exists
        try:
            self.client.images.get(self.image)
            self._log("INFO", f"Image found: {self.image}")
        except ImageNotFound:
            self._log("WARNING", f"Image not found: {self.image}")
        
        self._log("INFO", f"Initialized with image={self.image}, name={self.name}")
    
    @classmethod
    def ensure_image(
        cls,
        dockerfile_path: Optional[str] = None,
        image_name: str = None,
        **kwargs
    ) -> "MuxDocker":
        """
        Ensure Docker image exists, building if necessary.
        
        Args:
            dockerfile_path: Path to Dockerfile directory
            image_name: Name for built image
            **kwargs: Additional arguments for MuxDocker constructor
            
        Returns:
            Configured MuxDocker instance
        """
        if not DOCKER_AVAILABLE:
            raise DockerNotFoundError("Docker SDK not installed")
        
        image_name = image_name or cls.DEFAULT_IMAGE
        
        try:
            client = docker.from_env()
            client.images.get(image_name)
            return cls(image=image_name, **kwargs)
        except ImageNotFound:
            if dockerfile_path:
                # Build image
                print(f"Building image {image_name} from {dockerfile_path}...")
                client.images.build(
                    path=dockerfile_path,
                    tag=image_name,
                    rm=True
                )
                print(f"Image {image_name} built successfully")
                return cls(image=image_name, **kwargs)
            else:
                raise ImageNotFoundError(
                    f"Image {image_name} not found and no Dockerfile path provided"
                )
    
    def _setup_logging(self):
        """Configure file logging."""
        self.logger = logging.getLogger(f"MuxDocker.{self.name}")
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
    
    def _convert_volumes(self) -> Dict[str, Dict[str, str]]:
        """Convert volume dict to Docker format."""
        docker_volumes = {}
        for host_path, container_path in self.volumes.items():
            # Handle Windows paths
            host_path = str(Path(host_path).resolve())
            docker_volumes[host_path] = {
                'bind': container_path,
                'mode': 'rw'
            }
        return docker_volumes
    
    def start(self):
        """
        Start the Docker container.
        
        Must be called before any interaction.
        """
        if self.container and self.container.status == 'running':
            self._log("WARNING", "Container already running")
            return
        
        self._log("INFO", f"Starting container from image: {self.image}")
        
        # Prepare container configuration
        kwargs = {
            'image': self.image,
            'name': self.name,
            'detach': True,
            'stdin_open': True,
            'tty': True,
            'working_dir': self.working_dir,
            'environment': self.env_vars,
            'network': self.network,
            'auto_remove': self.auto_remove
        }
        
        if self.volumes:
            kwargs['volumes'] = self._convert_volumes()
        
        if self.cpu_limit:
            kwargs['cpu_quota'] = int(self.cpu_limit * 100000)
            kwargs['cpu_period'] = 100000
        
        if self.memory_limit:
            kwargs['mem_limit'] = self.memory_limit
        
        # Create and start container
        try:
            self.container = self.client.containers.run(**kwargs)
            self._log("INFO", f"Container started: {self.container.id[:12]}")
        except ImageNotFound:
            raise ImageNotFoundError(f"Image not found: {self.image}")
        except APIError as e:
            raise MuxDockerError(f"Failed to start container: {e}")
        
        # Start reader thread for logs
        self._stop_reader.clear()
        self._reader_thread = threading.Thread(
            target=self._reader_loop,
            daemon=True
        )
        self._reader_thread.start()
        
        # Register cleanup on exit
        atexit.register(self.terminate)
    
    def _reader_loop(self):
        """Background thread to read container logs and queue output."""
        try:
            for line in self.container.logs(stream=True, follow=True):
                if self._stop_reader.is_set():
                    break
                decoded = line.decode('utf-8', errors='replace')
                self._output_queue.put(decoded)
                self._log("DEBUG", f"LOG: {decoded.rstrip()}")
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
                if self.container and self.container.status != 'running':
                    raise MuxDockerError(
                        f"Container stopped unexpectedly (status: {self.container.status})"
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
            timeout: Maximum wait time (default: 20 seconds)
        """
        timeout = timeout or self.DEFAULT_BOOTSTRAP_TIMEOUT
        
        if not self.container:
            raise MuxDockerError("Container not started. Call start() first.")
        
        self._log("INFO", f"Waiting for MCP servers (timeout={timeout}s)")
        
        # MCP servers typically print initialization messages
        start_time = time.time()
        
        try:
            # Try to detect ready state from output
            self._read_until(r"(ready|mcp.*started|server.*connected|initialized)", timeout=timeout * 0.8)
        except SentinelTimeoutError:
            # If no ready indicator found, just wait the remaining time
            remaining = timeout - (time.time() - start_time)
            if remaining > 0:
                time.sleep(remaining)
        
        self._log("INFO", "MCP servers initialized")
    
    def _exec_in_container(self, command: str, timeout: int = 30) -> str:
        """
        Execute command in container and return output.
        
        Args:
            command: Command to execute
            timeout: Execution timeout
            
        Returns:
            Command output
        """
        if not self.container:
            raise MuxDockerError("Container not started.")
        
        try:
            exit_code, output = self.container.exec_run(
                cmd=f"sh -c '{command}'",
                timeout=timeout
            )
            return output.decode('utf-8', errors='replace')
        except Exception as e:
            self._log("ERROR", f"Exec failed: {e}")
            raise MuxDockerError(f"Command execution failed: {e}")
    
    def enter_chat(self):
        """
        Enter the agent chat interface.
        """
        if not self.container:
            raise MuxDockerError("Container not started. Call start() first.")
        
        command = "/agent"
        self._log("INFO", f"Entering chat: {command}")
        
        # Send command via exec
        self._exec_in_container(f"echo '{command}' | mux-swarm")
        
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
        
        if not self.container:
            raise MuxDockerError("Container not started.")
        
        # Generate unique sentinel
        sentinel_id = uuid.uuid4().hex
        sentinel_marker = self.SENTINEL_FORMAT.format(sentinel_id)
        
        # Send message with sentinel
        full_message = message
        if sentinel:
            full_message += f"\necho {sentinel_marker}"
        
        self._log("INFO", f"SEND: {message[:100]}...")
        
        # Execute in container
        output = self._exec_in_container(f"mux-swarm --message '{full_message}'", timeout=int(timeout))
        
        # Filter UI chrome if requested
        if filter_ui:
            output = self._filter_ui_lines(output)
        
        self._log("INFO", f"RECV: {output[:100]}...")
        return output.strip()
    
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
            raise MuxDockerError(result.get('error', 'Unknown error'))
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
        if not self.container:
            raise MuxDockerError("Container not started.")
        
        self._log("INFO", "Listing agents with /swap")
        
        # Exit chat if we're in it
        was_in_chat = self._in_chat
        if self._in_chat:
            self.exit_chat()
        
        # Execute /swap command
        output = self._exec_in_container("mux-swarm --swap")
        self._log("DEBUG", f"Raw output: {output[:500]}")
        
        # Parse agent names
        agents = []
        
        # Method 1: Parse numbered list
        for line in output.split('\n'):
            match = re.match(r'\s*(\d+)[.\)]\s*(.+)', line)
            if match:
                agents.append(match.group(2).strip())
        
        # Method 2: Look for agent names in output
        if not agents:
            agent_pattern = re.compile(r'\b([A-Z][a-zA-Z]*Agent)\b')
            found = agent_pattern.findall(output)
            agents = list(dict.fromkeys(found))
        
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
        if not self.container:
            raise MuxDockerError("Container not started.")
        
        self._log("INFO", f"Swapping to agent index {agent_index}")
        
        # Exit current chat if in one
        if self._in_chat:
            self.exit_chat()
        
        # Execute swap
        output = self._exec_in_container(f"mux-swarm --swap {agent_index}")
        self._log("INFO", f"Agent swap response: {output[:100]}")
        
        # Enter chat with new agent
        self.enter_chat()
    
    def exit_chat(self):
        """Exit the agent chat interface."""
        if not self._in_chat:
            return
        
        self._log("INFO", "Exiting chat with /qc")
        
        self._exec_in_container("mux-swarm --exit")
        
        self._in_chat = False
        self._current_agent = None
    
    def terminate(self, force: bool = False):
        """
        Stop and remove the container.
        
        Args:
            force: Force kill container
        """
        if not self.container:
            return
        
        self._log("INFO", f"Terminating container {self.container.id[:12]} (force={force})")
        
        # Stop reader thread
        self._stop_reader.set()
        
        # Exit chat first
        if self._in_chat:
            try:
                self.exit_chat()
            except Exception:
                pass
        
        # Stop container
        try:
            if force:
                self.container.kill()
            else:
                self.container.stop(timeout=5)
            
            # Remove if not auto_remove
            if not self.auto_remove:
                self.container.remove()
        except Exception as e:
            self._log("ERROR", f"Error terminating container: {e}")
        
        self.container = None
        self._in_chat = False
        self._log("INFO", "Container terminated")
    
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
        if self.container and self.container.status == 'running':
            self.terminate()


# CLI interface for testing
if __name__ == "__main__":
    import argparse
    
    parser = argparse.ArgumentParser(description="Mux Docker Wrapper")
    parser.add_argument("--image", default="mux-swarm:latest", help="Docker image name")
    parser.add_argument("--name", help="Container name")
    parser.add_argument("--message", "-m", help="Send a message and exit")
    parser.add_argument("--interactive", "-i", action="store_true", help="Interactive mode")
    parser.add_argument("--list-agents", action="store_true", help="List available agents")
    parser.add_argument("--swap", type=int, help="Swap to agent by index")
    parser.add_argument("--timeout", type=float, default=20, help="Bootstrap timeout")
    parser.add_argument("--build", action="store_true", help="Build image if not found")
    parser.add_argument("--dockerfile", help="Path to Dockerfile directory")
    
    args = parser.parse_args()
    
    # Create instance
    if args.build and args.dockerfile:
        mux = MuxDocker.ensure_image(dockerfile_path=args.dockerfile, image_name=args.image, name=args.name)
    else:
        mux = MuxDocker(image=args.image, name=args.name)
    
    print(f"Image: {mux.image}")
    print(f"Container: {mux.name}")
    
    try:
        mux.start()
        print(f"Container started")
        
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
            try:
                print(f"\nResponse:\n{response}")
            except UnicodeEncodeError:
                safe = response.encode(sys.stdout.encoding or 'utf-8', errors='replace').decode(sys.stdout.encoding or 'utf-8')
                print(f"\nResponse:\n{safe}")
        
        else:
            print("\nNo action specified. Use --message, --interactive, or --list-agents")
    
    finally:
        mux.terminate()
        print("Container terminated.")
