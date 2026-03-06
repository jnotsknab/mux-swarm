---
name: convert-skill-windows
description: Convert Unix/Linux-based skill.md files to {{os}} equivalents. Use when you need to transform a skill that contains Unix/Linux-specific commands, paths, and tools into a version that works on {{os}}. Takes a file path to an input skill.md and outputs a converted _{{os}}.md version.
---

# {{os}} Skill Converter

Convert Unix/Linux-based skill.md files to {{os}} equivalents by replacing OS-specific semantics with PowerShell/cmd.exe commands.

## Input

- **Required**: File path to the source skill.md file
- **Output**: New file with `_{{os}}.md` appended to the original filename (e.g., `SKILL.md` → `SKILL_{{os}}.md`)

## Conversion Rules

### Path Conversions

| Unix/Linux | {{os}} |
|------------|---------|
| `/home/user/` | `C:\Users\user\` |
| `/usr/local/bin/` | `C:\Program Files\` or `C:\Program Files (x86)\` |
| `~/` | `%USERPROFILE%` or `C:\Users\username\` |
| `~/.config/` | `%APPDATA%` |
| `/tmp/` | `%TEMP%` |
| `/path/to/file` | `C:\path\to\file` |

### Command Conversions

| Unix/Linux | {{os}} PowerShell | {{os}} cmd.exe |
|------------|-------------------|-----------------|
| `chmod +x script` | Not needed (executables don't need +x) | Not needed |
| `mkdir -p dir/subdir` | `New-Item -ItemType Directory -Path dir\subdir -Force` | `mkdir dir\subdir` |
| `ls`, `ls -la` | `Get-ChildItem` or `dir` | `dir` |
| `cat file` | `Get-Content file` | `type file` |
| `cp src dest` | `Copy-Item src dest` | `copy src dest` |
| `mv src dest` | `Move-Item src dest` | `move src dest` |
| `rm -rf dir` | `Remove-Item -Recurse -Force dir` | `rmdir /s /q dir` |
| `rm file` | `Remove-Item file` | `del file` |
| `touch file` | `New-Item -ItemType File file` | `echo. > file` |
| `curl -fsSL url -o file` | `Invoke-WebRequest -Uri url -OutFile file` | `curl -o file url` |
| `wget url -O file` | `Invoke-WebRequest -Uri url -OutFile file` | `curl -O url` |
| `echo "text" > file` | `"text" \| Out-File file` | `echo text > file` |
| `echo "text" >> file` | `"text" \| Out-File file -Append` | `echo text >> file` |
| `./script.sh` | `.\script.ps1` or `.\script.bat` | `script.bat` |
| `bash -c "command"` | `powershell -Command "command"` | `cmd /c "command"` |
| `export VAR=value` | `$env:VAR="value"` | `set VAR=value` |
| `source file` | `. file` (dot sourcing) | `call file` |
| `which command` | `Get-Command command -ErrorAction SilentlyContinue` | `where command` |
| `grep` | `Select-String` | `findstr` |
| `find . -name "*.ext"` | `Get-ChildItem -Recurse -Filter "*.ext"` | `dir /s /b *.ext` |
| `tar -xvf file.tar.gz` | `Expand-Archive file.tar.gz -DestinationPath .` | Use 7-Zip or WinRAR |
| `zip -r file.zip dir` | `Compress-Archive -Path dir -DestinationPath file.zip` | Use 7-Zip or WinRAR |
| `unzip file.zip` | `Expand-Archive file.zip -DestinationPath .` | Use 7-Zip or WinRAR |
| `pip install package` | `pip install package` (works in PowerShell) | `pip install package` |
| `npm install` | `npm install` (works in PowerShell) | `npm install` |
| `apt-get install pkg` | `winget install pkg` or download installer | N/A |
| `yum install pkg` | `winget install pkg` or download installer | N/A |

### Permission Commands

| Unix/Linux | {{os}} |
|------------|---------|
| `chmod 755 file` | `icacls file /grant Users:F` |
| `chmod +x file` | Not applicable (use file extension or execution policy) |
| `chown user:group file` | `icacls file /grant user:F` |

### Environment Variables

| Unix/Linux | {{os}} PowerShell | {{os}} cmd |
|------------|-------------------|-------------|
| `$VAR` | `$env:VAR` | `%VAR%` |
| `${VAR}` | `$env:VAR` | `%VAR%` |
| `$HOME` | `$env:USERPROFILE` | `%USERPROFILE%` |
| `$PATH` | `$env:PATH` | `%PATH%` |
| `export PATH=$PATH:/new/path` | `$env:PATH += ";C:\new\path"` | `set PATH=%PATH%;C:\new\path` |

### Common Tool Alternatives

| Unix/Linux Tool | {{os}} Alternative |
|-----------------|---------------------|
| `curl` | `Invoke-WebRequest` (PowerShell), `curl.exe` ({{os}}) |
| `wget` | `Invoke-WebRequest` (PowerShell) |
| `git` | `git` (works on {{os}}) |
| `python` | `python` or `py` |
| `node` | `node` (works on {{os}}) |
| `nano` | `notepad`, `code`, or VS Code |
| `vim` | `vim`, `code`, or VS Code |
| `grep` | `Select-String` (PowerShell), `findstr` (cmd) |
| `sed` | Use PowerShell regex or download sed for {{os}} |
| `awk` | Use PowerShell or download gawk for {{os}} |

## Usage

1. Read the input skill.md file
2. Apply path conversions (convert Unix paths to {{os}} paths)
3. Replace code blocks with {{os}} equivalents
4. Preserve all markdown structure, comments, and non-OS-specific content
5. Save the output as `{original_name}_{{os}}.md` in the same directory

## Example Conversions

### Before (Unix)
```bash
# Install the tool
curl -fsSL https://example.com/tool.sh -o /usr/local/bin/tool
chmod +x /usr/local/bin/tool

# Configure
mkdir -p ~/.config/tool
echo "api-key" > ~/.config/tool/config

# Run
tool --version
```

### After ({{os}})
```powershell
# Install the tool
Invoke-WebRequest -Uri "https://example.com/tool.ps1" -OutFile "C:\Program Files\tool\tool.ps1"

# Configure
New-Item -ItemType Directory -Path "$env:APPDATA\tool" -Force
"api-key" | Out-File "$env:APPDATA\tool\config"

# Run
& "C:\Program Files\tool\tool.ps1" -Version
```

### Before (Unix)
```bash
export API_KEY="your-key-here"
./solve-captcha balance
```

### After ({{os}})
```powershell
$env:API_KEY = "your-key-here"
.\solve-captcha balance
```

## Notes

- Always preserve the original file structure and markdown formatting
- Only convert code blocks (```bash, ```shell, ```sh) that contain OS-specific commands
- Keep explanatory text and non-code content unchanged
- Use PowerShell as the primary conversion target (most modern {{os}})
- For backward compatibility, cmd.exe alternatives are also provided
- Some Linux tools have {{os}} ports (git, node, python, etc.) and don't need conversion
