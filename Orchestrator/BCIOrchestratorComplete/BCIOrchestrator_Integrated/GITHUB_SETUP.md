# 📤 GitHub Setup Guide

## Quick Start: Push to GitHub

### Step 1: Clean Up Build Files (Important!)

Before pushing, **delete the Release folder** (if it exists):

```powershell
# PowerShell
Remove-Item -Recurse -Force .\Release -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\bin -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\obj -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\logs -ErrorAction SilentlyContinue
```

Or manually delete these folders:
- `Release/`
- `bin/`
- `obj/`
- `logs/`

### Step 2: Initialize Git Repository

```bash
# Navigate to your project folder
cd BCIOrchestrator_Integrated

# Initialize git
git init

# Add all files (the .gitignore will automatically exclude build files)
git add .

# Make first commit
git commit -m "Initial commit: BCI Orchestrator integrated application"
```

### Step 3: Create GitHub Repository

1. Go to https://github.com
2. Click **"New repository"**
3. Name it: `BCIOrchestrator` (or whatever you prefer)
4. Choose **Public** or **Private**
5. **Do NOT** initialize with README (we already have files)
6. Click **"Create repository"**

### Step 4: Push to GitHub

GitHub will show you commands. Use these:

```bash
# Add your GitHub repository as remote
git remote add origin https://github.com/YOUR_USERNAME/BCIOrchestrator.git

# Push to GitHub
git branch -M main
git push -u origin main
```

Replace `YOUR_USERNAME` with your actual GitHub username.

---

## ✅ What Gets Pushed (Small Files Only)

With the `.gitignore` file, only these get pushed:

```
✅ Program.cs                    (32 KB)
✅ BCIOrchestrator.csproj        (< 1 KB)
✅ appsettings.json              (< 1 KB)
✅ build.bat                     (< 1 KB)
✅ run.bat                       (< 1 KB)
✅ test_client.html              (11 KB)
✅ README.md                     (9 KB)
✅ QUICKSTART.md                 (5 KB)
✅ TESTING.md                    (10 KB)
✅ LICENSE                       (1 KB)
✅ .gitignore                    (< 1 KB)

Total: ~70 KB (well under 100MB!)
```

## ❌ What Doesn't Get Pushed (Excluded)

```
❌ Release/BCIOrchestrator.exe   (60 MB) - Too large, build artifact
❌ bin/                          (build files)
❌ obj/                          (build files)
❌ logs/                         (runtime logs)
❌ .vs/                          (Visual Studio files)
```

---

## 🎯 How Others Use Your Repository

When someone clones your repo:

```bash
git clone https://github.com/YOUR_USERNAME/BCIOrchestrator.git
cd BCIOrchestrator
./build.bat
```

They build it themselves! This is the standard way.

---

## 📦 Option 2: Use GitHub Releases for Large Files

If you want to provide pre-built executables:

### 1. Push Source Code (as above)

### 2. Create a Release

1. Go to your repo on GitHub
2. Click **"Releases"** → **"Create a new release"**
3. Tag: `v1.0.0`
4. Title: `BCI Orchestrator v1.0.0`
5. Description: Release notes
6. **Attach the .exe file** as a binary asset
7. Publish release

GitHub Releases allow files up to **2GB**!

---

## 🔄 Updating Your Repository

After making changes:

```bash
# See what changed
git status

# Add changes
git add .

# Commit with message
git commit -m "Fixed UDP parsing bug"

# Push to GitHub
git push
```

---

## 📊 Check Repository Size

Before pushing, check your repo size:

```bash
# See all file sizes
git ls-files -z | xargs -0 du -h | sort -h

# Check total repository size
du -sh .git
```

Should be well under 1MB for source-only.

---

## 🚨 If You Accidentally Push Large Files

If you already pushed the .exe file:

### Remove it from git history:

```bash
# Remove the file from git (keeps local copy)
git rm --cached Release/BCIOrchestrator.exe

# Commit the removal
git commit -m "Remove build artifacts from repository"

# Push changes
git push
```

### Or use BFG Repo-Cleaner (for complete removal from history):

```bash
# Install BFG (download from https://rtyley.github.io/bfg-repo-cleaner/)
java -jar bfg.jar --delete-files BCIOrchestrator.exe

# Clean up
git reflog expire --expire=now --all
git gc --prune=now --aggressive

# Force push
git push --force
```

---

## 📝 Sample README for GitHub

Here's a good README.md structure for your repo:

```markdown
# BCI Orchestrator

Single-executable BCI (Brain-Computer Interface) orchestrator for Emotiv devices.

## Features
- UDP receiver for Emotiv BCI-OSC
- WebSocket server for real-time broadcasting
- Advanced signal filtering
- Optional keyboard simulation
- System tray support

## Quick Start

### Build
\`\`\`bash
build.bat
\`\`\`

### Run
\`\`\`bash
cd Release
BCIOrchestrator.exe
\`\`\`

### Configure Emotiv
1. Open Emotiv App → Settings → OSC
2. Enable BCI-OSC
3. IP: `127.0.0.1`, Port: `7400`

## Documentation
- [Quick Start Guide](QUICKSTART.md)
- [Build Instructions](BUILD_INSTRUCTIONS.txt)
- [Testing Guide](TESTING.md)

## Requirements
- Windows 10/11
- .NET 8 or 9 SDK
- Emotiv BCI device

## License
MIT License - See LICENSE file
\`\`\`

---

## ✅ Complete Workflow

```bash
# 1. Clean build artifacts
Remove-Item -Recurse -Force .\Release, .\bin, .\obj, .\logs -ErrorAction SilentlyContinue

# 2. Initialize git (if not already done)
git init

# 3. Add files
git add .

# 4. Commit
git commit -m "Initial commit: BCI Orchestrator"

# 5. Create repo on GitHub (via website)

# 6. Add remote and push
git remote add origin https://github.com/YOUR_USERNAME/BCIOrchestrator.git
git branch -M main
git push -u origin main
```

**Total size pushed: ~70 KB** ✅

---

## 💡 Pro Tips

1. **Never commit build artifacts** - Let users build locally
2. **Use GitHub Releases** - For distributing pre-built binaries
3. **Update .gitignore** - Before first commit
4. **Check repo size** - Before pushing: `du -sh .git`
5. **Use meaningful commits** - Helps track changes

---

## 🆘 Common Issues

### "File exceeds 100MB"
- You forgot to delete Release folder
- Solution: `git rm --cached Release/BCIOrchestrator.exe`

### "Permission denied"
- Need to authenticate
- Use GitHub CLI or set up SSH keys

### ".gitignore not working"
- Files already tracked
- Solution: `git rm -r --cached .` then `git add .`

---

**Ready to push? Make sure Release/ folder is deleted first!** 🚀
