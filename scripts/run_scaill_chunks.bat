@echo off
cd /d "%~dp0\.."
echo Running WanVideo SCAIL Chunk Processor
echo =====================================
echo.
python scripts\run_scaill_chunks.py
pause