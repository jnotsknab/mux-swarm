---
name: 2-captcha
description: Solve CAPTCHAs using 2Captcha service via CLI. Use for bypassing captchas during web automation, account creation, or form submission.
homepage: https://github.com/adinvadim/2captcha-cli
requires_bins: [node, npm]
---

# 2Captcha Skill ({{os}})

Solve CAPTCHAs programmatically using the 2Captcha human-powered service.

## Installation

```bash
npm install -g 2captcha-cli

# Verify
2captcha-cli --version
```

## Configuration

### Option 1: Environment variable (Recommended)

Set your API key as an environment variable. Get your key at https://2captcha.com/enterpage

**Unix/Mac:**
```bash
export TWOCAPTCHA_API_KEY=your-key
# To persist, add the above line to ~/.bashrc or ~/.zshrc
```

**Windows:**
```powershell
# Session only
$env:TWOCAPTCHA_API_KEY = "your-key"

# Persistent
setx TWOCAPTCHA_API_KEY your-key
```

### Option 2: Save API key to file

**Unix/Mac:**
```bash
mkdir -p ~/.2captcha
echo "YOUR_API_KEY" > ~/.2captcha/api-key
```

**Windows:**
```powershell
New-Item -ItemType Directory -Path "$env:APPDATA\2captcha" -Force
"YOUR_API_KEY" | Out-File -FilePath "$env:APPDATA\2captcha\api-key" -Encoding UTF8
```

## Quick Reference

### Check Balance First
```bash
2captcha-cli balance
```

### Image CAPTCHA
```bash
# From file
2captcha-cli image /path/to/captcha.png

# From URL
2captcha-cli image "https://site.com/captcha.jpg"

# With options
2captcha-cli image captcha.png --numeric 1 --math
2captcha-cli image captcha.png --comment "Enter red letters only"
```

### reCAPTCHA v2
```bash
2captcha-cli recaptcha2 --sitekey "6Le-wvk..." --url "https://example.com"
```

### reCAPTCHA v3
```bash
2captcha-cli recaptcha3 --sitekey "KEY" --url "URL" --action "submit" --min-score 0.7
```

### hCaptcha
```bash
2captcha-cli hcaptcha --sitekey "KEY" --url "URL"
```

### Cloudflare Turnstile
```bash
2captcha-cli turnstile --sitekey "0x4AAA..." --url "URL"
```

### FunCaptcha (Arkose)
```bash
2captcha-cli funcaptcha --public-key "KEY" --url "URL"
```

### GeeTest
```bash
# v3
2captcha-cli geetest --gt "GT" --challenge "CHALLENGE" --url "URL"

# v4
2captcha-cli geetest4 --captcha-id "ID" --url "URL"
```

### Text Question
```bash
2captcha-cli text "What color is the sky?" --lang en
```

## Finding CAPTCHA Parameters

### reCAPTCHA sitekey
Look for:
- `data-sitekey` attribute in HTML
- `k=` parameter in reCAPTCHA iframe URL
- Network request to `google.com/recaptcha/api2/anchor`

### hCaptcha sitekey
Look for:
- `data-sitekey` in hCaptcha div
- Network requests to `hcaptcha.com`

### Turnstile sitekey
Look for:
- `data-sitekey` in Turnstile widget
- `cf-turnstile` class elements

## Workflow for Browser Automation

1. **Detect CAPTCHA** - Check if page has captcha element
2. **Extract params** - Get sitekey/challenge from page source
3. **Solve via CLI** - Call 2captcha-cli with params
4. **Inject token** - Set `g-recaptcha-response` or callback

### Example: Inject reCAPTCHA Token

```javascript
// After getting token from 2captcha-cli
document.getElementById('g-recaptcha-response').value = token;
// Or call callback if defined
___grecaptcha_cfg.clients[0].callback(token);
```

### Example: Automation Script

**Unix/Mac:**
```bash
export TWOCAPTCHA_API_KEY="YOUR_API_KEY"
token=$(2captcha-cli recaptcha2 --sitekey "6Le-wvk..." --url "https://example.com")
echo "Token: $token"
```

**Windows:**
```powershell
$env:TWOCAPTCHA_API_KEY = "YOUR_API_KEY"
$token = 2captcha-cli recaptcha2 --sitekey "6Le-wvk..." --url "https://example.com"
Write-Host "Token: $token"
```

## Cost Awareness

- Check balance before heavy automation
- Image: ~$0.001 per solve
- reCAPTCHA/hCaptcha/Turnstile: ~$0.003 per solve

## Error Handling

Common errors:
- `ERROR_ZERO_BALANCE` - Top up account
- `ERROR_NO_SLOT_AVAILABLE` - Retry in few seconds
- `ERROR_CAPTCHA_UNSOLVABLE` - Bad image or impossible captcha
- `ERROR_WRONG_CAPTCHA_ID` - Invalid task ID

## Notes

- Solving takes 10-60 seconds depending on type
- reCAPTCHA v3 may need multiple attempts for high scores
- Some sites detect automation — use carefully
- Tokens expire — use within 2-5 minutes
