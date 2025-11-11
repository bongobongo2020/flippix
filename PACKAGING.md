# FlipPix Packaging Guide

This guide explains how to package FlipPix for distribution to Windows users.

## Option 1: ZIP Archive (Simple & Fast)

### Using the Packaging Script

1. **Close FlipPix** if it's currently running
2. Run `package-release.bat`
3. The script creates `FlipPix-Release.zip` in the root folder

**What's included:**
- FlipPix.UI.exe (self-contained executable)
- workflow/ folder (ComfyUI workflows)
- INSTALL.txt (user installation instructions)

**What's excluded:**
- .pdb debug files (reduces size)
- output/ folder (runtime output directory)

### Manual ZIP Creation

If you prefer to create the ZIP manually:

```powershell
# Using PowerShell
Compress-Archive -Path publish\* -DestinationPath FlipPix-Release.zip -Exclude *.pdb,output
```

Or using Windows Explorer:
1. Navigate to the `publish` folder
2. Select all files except .pdb files and output folder
3. Right-click > Send to > Compressed (zipped) folder

---

## Option 2: Windows Installer (Professional)

For a more professional distribution with Start Menu shortcuts and uninstaller:

### Using Inno Setup

1. **Download Inno Setup**
   - Visit: https://jrsoftware.org/isdl.php
   - Install Inno Setup Compiler

2. **Open the installer script**
   - Double-click `flippix-installer.iss`
   - Or open it in Inno Setup Compiler

3. **Compile the installer**
   - Click "Build" > "Compile" (or press F9)
   - The installer will be created as `FlipPix-Setup.exe`

**Installer Features:**
- Professional Windows installer experience
- Start Menu shortcuts
- Optional desktop icon
- Clean uninstallation
- ~175MB installer size

### Customizing the Installer

Edit `flippix-installer.iss` to customize:
- Version number (line 5): `#define MyAppVersion "1.0.0"`
- Publisher name (line 6)
- Add custom icon by uncommenting line 22 and providing an .ico file

---

## Option 3: GitHub Releases

For distributing via GitHub:

### Create a Release

1. **Build and package:**
   ```bash
   ./publish.bat
   ./package-release.bat
   ```

2. **Tag the version:**
   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

3. **Create GitHub Release:**
   - Go to: https://github.com/bongobongo2020/flippix/releases/new
   - Select the tag
   - Upload `FlipPix-Release.zip` or `FlipPix-Setup.exe`
   - Add release notes

### Release Assets to Upload

- `FlipPix-Release.zip` - Portable version (no installation needed)
- `FlipPix-Setup.exe` - Installer version (if using Inno Setup)

---

## What Users Need to Know

Remind users that FlipPix requires:
1. **ComfyUI** running locally on `http://127.0.0.1:8188`
2. **Specific models and nodes** installed (see README.md)
3. **Windows x64** operating system
4. **NVIDIA GPU** with 12GB+ VRAM (for ComfyUI processing)

The INSTALL.txt file included in the package contains quick start instructions.

---

## File Sizes

Typical package sizes:
- **ZIP Archive**: ~175MB compressed
- **Inno Setup Installer**: ~175MB
- **Self-contained .exe**: ~175MB (already compressed in publish folder)

The application is large because it's a self-contained .NET application with all dependencies bundled.

---

## Distribution Checklist

Before distributing:
- [ ] Application builds without errors
- [ ] All required files are in publish/ folder
- [ ] INSTALL.txt is included
- [ ] README.md is up to date
- [ ] Version number is updated in installer script (if using)
- [ ] Tested on clean Windows installation
- [ ] ComfyUI integration tested

---

## Questions?

For issues or questions, visit:
https://github.com/bongobongo2020/flippix/issues
